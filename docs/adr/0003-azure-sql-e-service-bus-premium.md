# ADR-0003 — Persistência e execução durável on-premises

- **Status:** proposto _(reescrito em 2026-07-21; aguardando revisão do Decision Owner)_
- **Data:** 2026-07-20 (versão original) · 2026-07-21 (reescrito — pivô on-premises)
- **Gate de aprovação:** Decision Owner + revisão Dev + Arquitetura/Segurança
- **Aprovadores:** _(pendente)_
- **Substitui / substituído por:** reescreve integralmente a versão anterior
  deste ADR ("Azure SQL e Service Bus Premium"), que **não** foi aceita.

> **Reescrita (pivô de baseline).** Por decisão do Decision Owner
> (2026-07-21), o ArchiveBridge é um **produto on-premises**, instalado na
> infraestrutura do cliente, **sem Control Plane SaaS** e **sem dependência
> obrigatória de serviços PaaS da Azure**. A versão anterior deste ADR
> (Azure SQL + Service Bus Premium como componentes obrigatórios) fica
> descartada antes de aceitação. O nome do arquivo é mantido por
> estabilidade de referências.

## Precedência sobre o runbook

Este ADR **substitui, até a consolidação do runbook v1.1, qualquer
interpretação** das seções que pressupõem **Azure SQL** como sistema de
registro ([§12](../runbook/02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco))
e **Service Bus Premium** como mensageria
([§14](../runbook/02-parte-ii-arquitetura.md#14-mensageria-e-execução-durável)),
bem como as demais suposições de PaaS Azure (Blob Storage, Key Vault,
Managed Identity, Bicep/recursos Azure, registry Azure, observabilidade
Azure, modelos de custo PaaS e alta disponibilidade gerenciada pela Azure).
O DOCX fonte **não** é alterado agora; registra-se aqui a precedência da
decisão. A revisão dessas seções para a v1.1 é ação pendente do owner do
documento.

## Contexto

O produto é instalado e operado **dentro da infraestrutura do cliente**:
banco, aplicação, workers, logs, segredos, storage e backups permanecem no
ambiente do cliente. O **Microsoft 365** continua sendo **externo**, pois é
o **destino** da migração — alcançado por **HTTPS 443 de saída** aos
endpoints Microsoft exigidos pelo adapter de destino (ver "Conectividade e
fluxos"). Não há Control Plane SaaS; não há **assinatura Azure nem serviço
PaaS provisionado e administrado pelo cliente**.

```text
┌──────────────────────────────────────────────┐
│ Infraestrutura do cliente                    │
│  ArchiveBridge Control Plane (API+Orq+Portal) │
│      IIS ou Windows Service                    │
│            │                                   │
│  SQL Server local  ── estado, jobs, auditoria, │
│      │               leases, outbox, inbox, recon │
│  Workers Windows  ── EV, PST, validação,       │
│      │               upload, recon              │
│  Storage local / NAS / SMB ── PSTs, partes,    │
│                      hashes e evidências        │
└───────────────┬──────────────────────────────┘
                │ HTTPS 443 de saída
                ▼
      Microsoft 365 / Exchange Online (externo, destino)
```

## Decisão

> O ArchiveBridge será implantado **on-premises** na infraestrutura do
> cliente. O **SQL Server local** será o **sistema de registro
> transacional** do plano de controle, armazenando estados, locks, leases,
> checkpoints, outbox, inbox, auditoria e reconciliação. A execução
> assíncrona utilizará **inicialmente uma fila durável baseada em SQL
> Server**. PSTs e demais artefatos serão armazenados em **storage local,
> NAS ou compartilhamento SMB protegido**. **Azure SQL, Azure Service Bus e
> outros serviços PaaS não serão dependências obrigatórias do produto.**

### Premissas obrigatórias

- aplicação instalada na infraestrutura do cliente;
- SQL Server local como sistema de registro;
- fila durável inicialmente implementada no SQL Server;
- workers como Windows Services;
- storage local, NAS ou SMB para PSTs e evidências;
- **nenhuma dependência obrigatória de assinatura Azure ou serviço PaaS
  provisionado e administrado pelo cliente** (Azure SQL, Service Bus, Key
  Vault etc.); adapters Microsoft **poderão utilizar infraestrutura
  temporária fornecida pelo próprio serviço de destino** (ex.: o Azure
  Storage temporário do Purview, acessado por URL SAS — sem exigir
  assinatura Azure do cliente);
- **nenhuma porta de entrada publicada na internet**; conectividade externa
  somente por **HTTPS 443 de saída** aos endpoints Microsoft exigidos pelo
  adapter (ver "Conectividade e fluxos");
- Azure e brokers externos poderão existir futuramente **apenas como
  adapters opcionais**;
- não criar scaffolding ou código antes da aprovação deste ADR revisado.

## Conectividade e fluxos

**Regra:** o ArchiveBridge **não publica portas de entrada vindas da
internet**. A única conectividade externa é **HTTPS 443 de saída** para os
endpoints Microsoft requeridos pelo adapter de destino, incluindo:

- **Microsoft Entra ID** (autenticação/token, quando aplicável);
- **Exchange Online**;
- **Microsoft Graph**;
- **Microsoft Purview**;
- o **Azure Storage temporário fornecido pela Microsoft** para importação
  de PST (upload via **AzCopy** para área temporária, autenticado por **URL
  SAS**) — **não** exige assinatura Azure do cliente; é infraestrutura do
  próprio serviço de destino.

**Comunicação interna** (dentro da infra do cliente) é restrita por
**allowlist** e documentada em uma **matriz de fluxos e portas**, cobrindo:
worker ↔ Enterprise Vault; Control Plane ↔ SQL Server; workers ↔ SQL
Server; workers ↔ NAS/SMB; navegadores internos ↔ Portal; autenticação com
AD local ou Entra ID; adapters ↔ endpoints Microsoft necessários. A matriz
de fluxos/portas é entregue com a implantação (não neste ADR).

## Componentes on-premises

| Função | Tecnologia on-premises |
| --- | --- |
| Control Plane | ASP.NET Core em IIS ou Windows Service |
| Orquestração | Serviço .NET local |
| Banco | SQL Server local |
| Fila inicial | Tabelas duráveis no SQL Server |
| Workers | Windows Services isolados |
| PSTs e artefatos | NTFS, volume dedicado, NAS ou SMB |
| Segredos | gMSA, Certificate Store, DPAPI e ACLs |
| Logs | Windows Event Log, arquivos estruturados ou SIEM local |
| Backup | Backup corporativo do cliente |
| Autenticação administrativa | AD local ou Entra ID, conforme ambiente |
| Integração M365 | Conexão de saída HTTPS com certificado |

O SQL Server é o sistema de registro de: projetos e configurações;
inventário dos PSTs; hashes e metadados; estados da migração; tentativas e
checkpoints; aprovações; leases dos workers; outbox e inbox; reconciliação;
cadeia de custódia; pacotes de evidência. **Os PSTs não ficam dentro do SQL
Server** — o banco guarda apenas estado, referências, hashes e informação
operacional.

## Fila durável em SQL Server (sem broker)

Para a primeira versão **não há necessidade de Service Bus Premium,
RabbitMQ ou outro broker**. A fila é implementada no próprio SQL Server:

```text
job_queue
job_attempts
worker_leases
outbox_messages
inbox_messages
dead_letter_jobs
```

Aquisição transacional de trabalho por um worker:

1. procura job pendente e disponível;
2. adquire lease exclusivo;
3. marca o job como `PROCESSING`;
4. executa a operação;
5. atualiza o checkpoint;
6. finaliza ou agenda retry;
7. move para dead letter após o limite.

Mecanismos do SQL Server usados: transações; `rowversion`; unique
constraints; `sp_getapplock`; locks transacionais; `UPDLOCK`; `READPAST`;
controle de concorrência; recuperação após falha. Para a escala inicial,
essa abordagem é mais simples, barata e operável. Um **broker local poderá
ser adicionado futuramente como adapter opcional**, caso os testes
demonstrem que o SQL Server virou gargalo — **não** é requisito inicial.

> **Contrato de correção da fila.** Como o broker foi retirado, a garantia
> contra dupla execução, job preso, dupla importação, lease expirado,
> starvation, crescimento de Inbox/Outbox/DLQ e inconsistência em failover é
> responsabilidade do produto e está especificada em
> [`evidence/0003-parecer-fila-sql-onprem.md`](evidence/0003-parecer-fila-sql-onprem.md)
> (aquisição atômica; lease/heartbeat/recuperação; idempotência/dedup;
> Outbox/Inbox; retry/backoff/DLQ; retenção; failover; teste de
> concorrência multi-worker; critério objetivo para broker opcional). Nota:
> DPAPI por máquina só serve ao **perfil de nó único**; HA exige mecanismo
> de segredos multi-nó.

## Perfis de implantação

| Perfil | Composição | Uso |
| --- | --- | --- |
| **Instalação básica** | 1 servidor ArchiveBridge + 1 SQL Server existente + 1 storage local/SMB | laboratórios e migrações menores |
| **Produção padrão** | Servidor 1 Control Plane · Servidor 2 SQL Server · Servidor 3 Workers EV/PST · Servidor 4 Workers Upload/Recon · Storage NAS/SMB | produção típica |
| **Alta disponibilidade (opcional)** | 2 nós de Control Plane · SQL Server Always On ou cluster · múltiplos workers · storage corporativo redundante · load balancer local | resiliência |

**Alta disponibilidade é opção de implantação, não dependência de Azure.**

## Segurança on-premises

Contas de serviço/gMSA; mínimo privilégio; TLS interno; certificados do
cliente; BitLocker nos workers; ACL exclusiva nos diretórios de staging;
WDAC/App Control; Defender ou EDR corporativo; bloqueio de execução nos
diretórios de dados; **nenhuma porta de entrada vinda da internet**;
somente saída HTTPS 443 aos endpoints Microsoft exigidos pelo adapter
(Entra ID, Exchange Online, Graph, Purview e o Azure Storage temporário da
Microsoft); rotação de certificados; **logs sem assunto ou corpo de
e-mail**; limpeza segura do staging após retenção; backup e DR sob controle
do cliente.

## Alternativas consideradas

| Alternativa | Prós | Contras | Por que não |
| --- | --- | --- | --- |
| Azure SQL + Service Bus Premium obrigatórios (versão anterior) | serviços gerenciados | exige Azure; incompatível com produto on-premises na infra do cliente | contraria a baseline on-premises first |
| Broker externo (RabbitMQ/etc.) como requisito inicial | mensageria dedicada | mais um componente para instalar/operar no cliente | fila em SQL Server basta na escala inicial; broker vira adapter opcional |
| PSTs/artefatos no banco | tudo em um lugar | infla o banco; custo e performance | banco guarda só estado/hashes/refs; artefatos em NTFS/NAS/SMB |

## Consequências

- Positivas: sem dependência de Azure; instalável e operável na infra do
  cliente; menos componentes; custo e operação mais simples na escala
  inicial; dados/segredos/backup sob controle do cliente.
- Negativas / dívidas assumidas: alta disponibilidade e recuperação passam
  a ser responsabilidade do cliente (SQL Always On, storage redundante);
  a fila em SQL exige disciplina de concorrência (leases, `READPAST`,
  applock) para não virar gargalo.
- Riscos e mitigação: SQL como gargalo → medir em teste de carga; se
  comprovado, adicionar broker local como adapter opcional (sem trocar o
  contrato de fila); heterogeneidade de ambientes do cliente → perfis de
  implantação e requisitos mínimos documentados.

## Adapters opcionais futuros (não requisito)

Azure (SQL/Service Bus/Blob/Key Vault) e brokers externos podem ser
adicionados **como adapters opcionais** se um cliente específico os exigir,
atrás das mesmas abstrações de persistência/fila/storage/segredos —
**nunca** como dependência obrigatória do produto.

## Evidências

Baseline on-premises definida pelo Decision Owner (2026-07-21). Referência
de mecanismos SQL Server (locks, `sp_getapplock`, `rowversion`, outbox/inbox)
— documentação oficial do SQL Server. Confirmação do gate: parecer de Dev +
Arquitetura/Segurança sobre este desenho de persistência e fila durável
on-premises. **A evidência de custos Azure da versão anterior não se aplica**
(produto não é cloud-native no Azure).
