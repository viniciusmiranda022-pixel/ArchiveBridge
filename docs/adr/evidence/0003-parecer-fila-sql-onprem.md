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
demais (`READPAST`) e sem corrida (`UPDLOCK`, `ROWLOCK`):

```sql
WITH nxt AS (
  SELECT TOP (1) *
  FROM job_queue WITH (READPAST, UPDLOCK, ROWLOCK)
  WHERE status = 'PENDING' AND visible_at <= SYSUTCDATETIME()
  ORDER BY priority DESC, enqueued_at ASC        -- FIFO ponderada; evita starvation
)
UPDATE nxt
SET status = 'PROCESSING',
    owner_worker = @worker,
    lease_until = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
    attempt = attempt + 1,
    row_ver = DEFAULT
OUTPUT inserted.job_id, inserted.payload_ref;
```

Alternativa por partição/afinidade: `sp_getapplock` transacional por chave
lógica quando for necessário serializar um recurso (ex.: um mesmo archive).
Concorrência ótima via `rowversion` nas atualizações de estado.

## 2. Lease, heartbeat e recuperação após crash

- Cada claim grava `lease_until`. O worker **renova por heartbeat** (ex.: a
  cada `leaseSeconds/3`), estendendo `lease_until` enquanto vivo.
- Um **reaper** (job agendado) devolve à fila os jobs com
  `status='PROCESSING' AND lease_until < SYSUTCDATETIME()`: volta a
  `PENDING` (ou `DEAD_LETTER` se `attempt >= max_attempts`), preservando o
  `checkpoint`. Assim, worker morto **não** deixa job preso e o lease
  expirado **não** permite dois executores — a retomada só ocorre após o
  lease vencer, e o `row_ver` detecta escrita tardia do worker zumbi.

## 3. Idempotência e deduplicação

- Toda unidade de trabalho carrega uma **idempotency key** determinística
  (derivada de origem+plano+destino, alinhada ao fingerprint/plan hash da
  cadeia de custódia).
- Efeitos externos (ex.: criar import job/enviar parte) gravam um registro
  com **unique constraint** na idempotency key **na mesma transação** do
  efeito; retry reencontra o registro e **não** repete o efeito. Assim,
  reprocessamento **não** produz segunda importação.

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
  failover; leases expirados são reclamados pelo reaper. A idempotência
  (item 3) garante que um retry pós-failover **não** duplica efeito.
- Requisito: operações de efeito externo são idempotentes **antes** de
  confirmar `COMPLETED`.

## 8. Teste de concorrência (multi-worker)

- N workers disputando a mesma fila sob carga: asserções — **nenhum** job
  reivindicado por dois workers; **nenhum** job perdido; **nenhuma**
  segunda importação; sem deadlock não tratado; throughput e latência de
  claim registrados por janela. Inclui cenário de **kill -9** de worker
  (recuperação por lease) e de **failover** de SQL durante processamento.

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
