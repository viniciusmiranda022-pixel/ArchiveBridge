# Threat model — delta da Vertical Slice 2 (projetos, ondas, planejamento e mapping)

Delta de modelagem de ameaças para a Vertical Slice 2. Estende o threat model
on-premises da evidência do
[ADR-0008](../adr/evidence/0008-threat-model-avaliacao-dados.md) para os novos
ativos e fluxos: projetos de migração, ondas, planejamento de capacidade e a
**geração segura do CSV de mapping**. Não introduz adapter externo (sem
Purview/AzCopy/EV nesta slice).

- **Tipo:** threat model (STRIDE) — delta da Slice 2
- **Produzido por (Evidence Owner):** Engenharia ArchiveBridge
- **Revisor necessário:** Segurança/Privacidade (DPO)
- **Estado da revisão:** **pendente** — sustenta o desenho da Slice 2; **não** é
  a aceitação formal (ato do Decision Owner) nem substitui a revisão de
  Segurança/DPO.

> [!NOTE]
> A slice persiste **apenas metadados de planejamento e governança**. Em nenhuma
> das tabelas de planejamento há conteúdo de e-mail/PST, segredo, SAS ou token. Os
> metadados sensíveis (caminho de origem, nome de PST, mailbox) são tratados
> como PII de planejamento: ficam no SQL sob RLS, mas **nunca** aparecem em logs.
> O único artefato fora do SQL é o **`mapping.csv` imutável** (metadados de
> planejamento) persistido em filesystem on-premises versionado — sem Azure.

## 1. Ativos críticos da slice

- Configuração de projeto que **afeta o resultado** (destino, política de
  archive) e seu hash determinístico versionado.
- Seleção da onda (conjunto de PSTs) e seu hash; totais planejados por archive.
- Pasta de destino (`TargetRootFolder`) aprovada e congelada.
- Evidência de capacidade (`planning_assessments`) e de aprovação (`approvals`).
- **CSV de mapping** e sua evidência versionada (`mapping.sha256`,
  `mapping_csv_versions`, `mapping_csv_rows`) e o **artefato imutável** publicado
  em filesystem (`mapping.csv` + `mapping.sha256` + manifesto, por versão).
- **Contexto versionado do comando durável** (`planning_commands`): vincula cada
  operação à versão/estado que a originou.
- **Decisões de avaliação Microsoft** (`capacity_assessment_decisions`): liberam,
  de forma auditável e append-only, ondas bloqueadas por capacidade.

## 2. Ameaças e mitigações (delta STRIDE)

| Ameaça | Vetor concreto | Mitigação (código) | Mitigação (banco) |
| --- | --- | --- | --- |
| **Injeção de CSV** | valor entra em coluna e altera o parse do importador | serializer RFC 4180 com aspas reversíveis; validador rejeita nº de colunas ≠ 10 e colunas extras | `mapping_csv_rows` com colunas tipadas; `CK_mcr_sp_empty`, `CK_mcr_workload`, `CK_mcr_isarchive` |
| **Injeção de fórmula** | célula começa por `= + - @`, TAB ou CR (execução no Excel) | `MappingCsvSerializer` **falha fechado** ao emitir valor com gatilho de fórmula — nunca reescreve silenciosamente o valor autorizado; validador sinaliza o gatilho por índice | — |
| **Path traversal** | `FilePath` com `..` ou container reservado `ingestiondata` | `WaveEntry`/`MappingRow` rejeitam `..` e `ingestiondata` (case-insensitive) | — |
| **Troca de mailbox** | editar `Mailbox` do CSV para desviar destino | validador compara cada linha com a fonte autorizada (mailbox por nome de PST); divergência → inválido | metadados de destino imutáveis por versão de seleção |
| **Troca de TargetRootFolder** | apontar a onda para pasta de outro projeto | `TargetRootFolder` canônico e congelado após aprovação; validador exige a pasta aprovada | `UQ_migration_waves_target_root` (unicidade **global**); gatilho `TR_migration_waves_freeze` |
| **Duplicação de PST** | mesmo PST em duas linhas/ondas | seleção rejeita `PstName` duplicado; gerador e validador exigem unicidade | `UQ_wave_entries_pst`, `UQ_mcr_name` |
| **Manipulação de hash** | alterar hash de config/seleção para forjar equivalência | hash determinístico recomputável; ValidateProject recusa se hash não se reproduz | grants append-only; `UPDATE(status)` restrito em `mapping_csv_versions` |
| **Acesso cross-tenant** | operação lê/escreve linha de outro tenant | todo store exige `TenantScope`; RLS por `SESSION_CONTEXT('tenant_id')` | `rls.tenant_isolation_policy` (filter+block) em todas as 9 tabelas |
| **Acesso cross-projeto** | onda/mapping de outro projeto no mesmo tenant | filtro explícito por `project_id` em todas as leituras | chaves/índices compostos `(tenant_id, project_id, …)`; FKs compostas |
| **Aprovação indevida** | aprovar com validações pendentes ou anônima | máquina de estados só chega a Approved via ReadyForApproval; `Approve` exige responsável | `CK_migration_waves_approval` (Approved/Frozen/Completed exigem `approved_by`+data) |
| **Edição manual do CSV aprovado** | reeditar o CSV fora do gerador | evidência imutável; nova geração cria N+1 e não sobrescreve | `mapping_csv_versions`/`_rows` append-only; `UX_mcv_single_usable` |
| **Exposição de UPN/PII em log** | logar lista de mailboxes/paths/nomes de PST | mensagens de validação e razões usam **índices e contagens**, nunca valores | — (ver §3) |
| **Uso de versão obsoleta** | consumir CSV de versão substituída | geração marca a anterior como `Superseded`; só uma utilizável | `UX_mcv_single_usable` (índice único filtrado `status=0`) |
| **Config aprovada alterada em silêncio** | mudar destino após aprovação | domínio bloqueia `UpdateConfiguration` fora de Draft/ReadyForAssessment/Blocked | gatilho `TR_migration_projects_freeze_config` |
| **Bypass do limite de 100 GB** | dividir artificialmente as entradas | `CapacityPlanner` agrupa por **identidade canônica** (`TargetArchiveId`); soma por archive com `checked` (falha em overflow) | — |
| **Comando durável obsoleto** | executar comando criado para v1 depois que a seleção virou v2 | contexto do comando é **vinculado à versão** (schema/config/seleção/destino); worker compara e recusa (`STALE_COMMAND_CONTEXT`) | `planning_commands.expected_*`; grants append-only |
| **Job de controle sequestrado** | worker reivindica Job de controle sem contexto de planejamento | claim **exclusivo**: só Jobs com linha em `planning_commands` (EXISTS atômico com o carregamento do contexto); Job sem contexto não vira Processing | claim SQL com `EXISTS`; FK composta `planning_commands → jobs` |
| **Efeito de dono defasado** | worker A perde o lease, B reivindica, A grava | efeitos **cercados** pelo mesmo fencing do Job (época/dono/Processing) na MESMA transação; `Completed` só quando aplicado | guarda `50030`; `owner_worker`+`lease_epoch` conferidos no UPDATE/INSERT |
| **Documento novo com evidência antiga** | mesma seleção, code page diferente reaproveita versão | idempotência pela **impressão digital completa** (`MappingGenerationFingerprint`); reaproveitar devolve o artefato/hash daquela versão | `generation_fingerprint` gravado por versão |
| **Mapping concluído sem artefato** | Job `Completed` mas o `mapping.csv` não existe | artefato **publicado antes do commit** (temp→flush→rename atômico); versão persistida sempre tem artefato | `artifact_path`/`artifact_size_bytes` na versão |
| **Liberação indevida de bloqueio** | desbloquear onda >100 GB sem decisão | só decisão **aprovada**, da versão corrente e do archive, libera (Blocked→Validating) atomicamente; append-only | `capacity_assessment_decisions` (append-only); RLS + FK composta |
| **Estado parcial na validação** | avaliações sem transição (ou vice-versa) em falha | avaliações + transição + `row_version` numa **única transação** (rollback total em falha) | `SaveValidationAsync` transacional; `THROW 50021` em conflito |
| **Identidade de archive ambígua** | aliases não resolvidos mascaram o volume real | identidade **não resolvida** (só mailbox) **bloqueia** a validação; produção exige `TargetArchiveId` do manifesto | `wave_entries.is_archive_resolved` preserva a resolução |

## 3. Higiene de logs (obrigatória)

Os logs desta slice **não** podem conter, em hipótese alguma:

- lista completa de mailboxes;
- caminhos reais de origem (`FilePath`);
- nomes sensíveis de PST (`Name`/`PstName`);
- conteúdo de arquivo;
- tokens, SAS ou segredos.

Mecanismos:

- **Mensagens de validação sem PII** — `MappingValidationResult` e as razões de
  `planning_assessments` referenciam **linha/coluna e contagens**, nunca valores
  (ex.: *"Linha 42: divergência em relação à fonte autorizada."*).
- **Diff entre versões** — comparações usam hashes (`content_sha256`,
  `selection_hash`, `configuration_hash`); o diff exposto não expande PII.
- **Correlação, não conteúdo** — a trilha usa `correlation_id`; o detalhe fica no
  SQL sob RLS, acessível apenas ao tenant.
- **Sem BOM / UTF-8 puro** — evita artefatos de encoding que mascarem conteúdo.

## 4. Fora de escopo (reafirmado)

Sem adapter externo; sem Purview/AzCopy/EV; sem capability discovery do EV. A
execução real da importação e seus segredos são de slices futuras, sob nova
evidência e decisão do Decision Owner.
