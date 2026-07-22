# Parecer — fila durável em SQL Server on-premises — gate do ADR-0003

Evidência requerida pelo gate do
[ADR-0003](../0003-azure-sql-e-service-bus-premium.md) (persistência e
execução durável on-premises). Como o Service Bus foi retirado, a garantia
de correção da fila passa a ser **responsabilidade direta do produto**;
este parecer define o **contrato** que a sustenta.

- **Tipo:** parecer técnico de correção (revisão Dev + Arquitetura/Segurança)
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge, sob direção
  do Decision Owner
- **Data:** 2026-07-21
- **Natureza:** define o contrato que **impede** os modos de falha listados;
  não é a aceitação formal (ato do Decision Owner). Revisão assumidamente
  não independente; competência exercida/aceita pelo Decision Owner até
  haver revisor distinto.

## Modos de falha a impedir

Dois workers no mesmo job; job preso após queda do worker; reprocessamento
gerando segunda importação; lease expirado causando execução concorrente;
starvation de jobs antigos; crescimento indefinido de Inbox/Outbox/DLQ;
failover do SQL Server deixando jobs inconsistentes.

## 1. Aquisição atômica e exclusiva do job (transacional)

Um único worker reivindica um job por vez, em transação, sem bloquear os
demais (`READPAST`) e sem corrida (`UPDLOCK`, `ROWLOCK`). A coluna
`rowversion` (`row_ver`) é **gerada automaticamente** pelo SQL Server a
cada `INSERT`/`UPDATE` — ela **não** aparece no `SET`; o claim apenas a
retorna no `OUTPUT`, junto com o **`lease_epoch`** (fencing token, item 2):

```sql
WITH nxt AS (
  SELECT TOP (1) *
  FROM job_queue WITH (READPAST, UPDLOCK, ROWLOCK)
  WHERE status = 'PENDING' AND visible_at <= SYSUTCDATETIME()
  ORDER BY
      -- prioridade EFETIVA com aging: quem espera, sobe (ver item 1.1)
      priority + DATEDIFF(minute, enqueued_at, SYSUTCDATETIME()) / @agingMinutes DESC,
      enqueued_at ASC
)
UPDATE nxt
SET status = 'PROCESSING',
    owner_worker = @worker,
    lease_until = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
    attempt = attempt + 1,
    lease_epoch = lease_epoch + 1
OUTPUT
    inserted.job_id,
    inserted.payload_ref,
    inserted.row_ver,
    inserted.lease_epoch;
```

Alternativa por partição/afinidade: `sp_getapplock` transacional por chave
lógica quando for necessário serializar um recurso (ex.: um mesmo archive).
Concorrência ótima via `rowversion` nas atualizações de estado.

### 1.1 Anti-starvation (obrigatório)

`ORDER BY priority DESC, enqueued_at ASC` **não** evita starvation: sob
chegada contínua de jobs de prioridade alta, um job de prioridade baixa
pode nunca executar. O contrato exige **pelo menos um** destes mecanismos,
com o **aging** como default do produto (já refletido no claim acima):

- **aging** — a prioridade efetiva cresce com o tempo de espera
  (`priority + espera/@agingMinutes`), garantindo que todo job
  eventualmente alcança o topo;
- **quota por prioridade** — N claims por classe de prioridade por ciclo;
- **round-robin ponderado** — capacidade reservada para filas menos
  prioritárias.

A eficácia do mecanismo escolhido é **asserção do teste multi-worker**
(item 8).

## 2. Lease, heartbeat, fencing e recuperação após crash

- Cada claim grava `lease_until` e **incrementa `lease_epoch`** (fencing
  token). O worker **renova por heartbeat** (ex.: a cada `leaseSeconds/3`),
  estendendo `lease_until` enquanto vivo.
- **Fencing:** heartbeat, checkpoint e conclusão são `UPDATE`s
  **condicionados a `job_id + owner_worker + lease_epoch + row_ver`**. Se
  zero linhas forem afetadas, o worker **perdeu o lease e deve interromper
  imediatamente** a execução — escrita tardia de worker zumbi nunca é
  aceita.
- **A expiração do lease protege o banco, não o mundo externo:** um worker
  zumbi pode ainda estar concluindo um efeito externo em voo (criar import
  job, reenviar parte, operação no Purview/Exchange Online). Por isso, como
  já determina o runbook (§12.4): *a expiração não autoriza outro worker
  até o orchestrator confirmar a ausência; operações destrutivas nunca
  dependem apenas do lease expirado*.
- **Reaper com duas rotas**, conforme o job:
  - job **sem efeito externo possível** (etapas puramente locais:
    inspeção, hash, cópia local): lease expirado → volta a `PENDING` (ou
    `DEAD_LETTER` se `attempt >= max_attempts`), preservando `checkpoint`;
  - job **com efeito externo possível** (qualquer interação com provedor):
    lease expirado → **`RECOVERY_REQUIRED`/`RECONCILING`**, **nunca**
    `PENDING` automático. Só é reassumido após **consulta ao provedor**
    (via ledger do item 3) confirmar o resultado da operação anterior.

## 3. Idempotência e deduplicação (efeitos externos via ledger)

- Toda unidade de trabalho carrega uma **idempotency key** determinística
  (derivada de origem+plano+destino, alinhada ao fingerprint/plan hash da
  cadeia de custódia), protegida por **unique constraint**.
- **Não existe transação distribuída** entre o SQL Server local e
  Purview/Graph/Exchange Online: o efeito externo **não** ocorre "na mesma
  transação" SQL. O padrão Outbox torna atômicos apenas (1) a alteração do
  estado local e (2) o **registro da intenção** local; a chamada externa
  acontece depois e permanece **at-least-once**, exigindo idempotência e
  reconciliação.
- O contrato usa um **ledger de operações externas**:

```text
external_operations
-------------------
operation_key       UNIQUE
job_id
provider
operation_type
status              INTENT | SUBMITTED | CONFIRMED | AMBIGUOUS | FAILED
provider_operation_id
request_hash
created_at
last_checked_at
```

Fluxo obrigatório:

```text
1. Gravar INTENT com operation_key única.
2. Commitar a transação SQL.
3. Executar a chamada externa.
4. Registrar provider_operation_id e SUBMITTED/CONFIRMED.
5. Em timeout ou resposta ambígua, marcar AMBIGUOUS.
6. Consultar o provedor antes de qualquer nova tentativa.
7. Nunca repetir automaticamente uma importação com resultado ambíguo.
```

Retry reencontra o `operation_key` (unique) e decide pelo **estado do
ledger + consulta ao provedor** — nunca repete às cegas. Assim,
reprocessamento **não** produz segunda importação, mesmo sem transação
distribuída. (Coerente com o runbook: importação com resposta ambígua
consulta ledger/resultado antes de decidir repetir.)

## 4. Outbox / Inbox transacionais

- **Outbox:** mudanças de estado e as mensagens resultantes são gravadas na
  **mesma transação** (`outbox_messages`); um relay publica/entrega e marca
  como enviado. Sem publicação fantasma nem perda.
- **Inbox:** mensagens/eventos recebidos são deduplicados por `message_id`
  com unique constraint antes de produzir efeito (exactly-once de efeito
  sobre at-least-once de entrega).

## 5. Retry, backoff e dead letter

- `attempt`, `max_attempts`, `visible_at` (backoff exponencial com jitter);
  falha transitória reagenda `visible_at`; ao exceder `max_attempts`, o job
  vai para `dead_letter_jobs` com o último erro (sanitizado).
- DLQ **não** é reprocessada automaticamente; exige ação do operador.

## 6. Retenção e limpeza

- Jobs concluídos, `outbox`/`inbox` liquidados: expurgo por política (ex.:
  N dias) via job de manutenção, evitando **crescimento indefinido**.
- DLQ retida por janela maior (investigação); expurgo só após disposição
  registrada. Índices e partição/limpeza dimensionados para a retenção.

## 7. Failover do SQL Server

- HA por **Always On/cluster** (perfil HA). Transações em voo no momento do
  failover **fazem rollback** — nenhum efeito parcial confirmado.
- Leases usam **tempo do banco** (`SYSUTCDATETIME()`), consistente após
  failover; leases expirados são tratados pelo reaper **pelas duas rotas do
  item 2** (local → `PENDING`; efeito externo possível →
  `RECOVERY_REQUIRED`/`RECONCILING`). O ledger do item 3 garante que um
  retry pós-failover **não** duplica efeito.
- Requisito: nenhuma operação com efeito externo é confirmada `COMPLETED`
  sem estado `CONFIRMED` no ledger.

## 8. Teste de concorrência (multi-worker)

- N workers disputando a mesma fila sob carga: asserções — **nenhum** job
  reivindicado por dois workers; **nenhum** job perdido; **nenhuma**
  segunda importação; **nenhuma escrita aceita de worker com `lease_epoch`
  antigo** (fencing efetivo); sem deadlock não tratado; throughput e
  latência de claim registrados por janela. Inclui cenário de **kill -9**
  de worker (recuperação por lease; job com efeito externo vai a
  `RECOVERY_REQUIRED`, não a `PENDING`) e de **failover** de SQL durante
  processamento.
- **Anti-starvation:** sob chegada contínua de jobs de prioridade alta,
  jobs de prioridade baixa previamente enfileirados **devem executar dentro
  de um limite de espera definido** (via aging/quota/WRR do item 1.1) — a
  asserção falha se algum job ultrapassar o limite sem ser reivindicado.

## 9. Critério objetivo para broker opcional

Introduzir um **broker local como adapter opcional** (sem trocar o contrato
de fila) **somente se**, em teste de carga representativo, ocorrer pelo
menos um: latência de claim acima do alvo por P95 sustentado; contenção de
lock/bloqueio acima do limite; throughput exigido acima do que a fila SQL
sustenta no hardware do cliente. Enquanto não houver essa evidência, a fila
SQL é suficiente e o broker **não** é requisito.

## Nota de segredos e HA (DPAPI)

**DPAPI vinculado à máquina** protege segredos apenas no **perfil de nó
único**. Instalações em **alta disponibilidade** (múltiplos nós de Control
Plane/workers) exigem um **mecanismo de segredos compatível com múltiplos
nós** (ex.: gMSA + certificado compartilhado/store replicado, ou chave de
proteção de dados compartilhada entre nós) — DPAPI por máquina **não** é
suficiente para HA.

## Recomendação

O contrato acima **impede** os modos de falha listados e torna a fila SQL
Server **suficiente e correta** para a escala inicial on-premises.
Recomenda-se **aceitar o ADR-0003**, com os itens 1–9 como **contrato de
implementação** verificável (o item 8 é teste obrigatório antes de
produção) e a nota de segredos como condição do perfil HA.

A **aceitação formal** é ato do Decision Owner; o ADR-0003 permanece
`proposto`. O flip para `aceito` ocorrerá **neste mesmo PR** após
autorização.
