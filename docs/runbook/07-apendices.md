<!-- Gerado por tools/convert_runbook.py a partir de docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx. Não editar manualmente: alterações devem ser feitas no DOCX e reconvertidas. -->

# Apêndice A - DDL de referência

O DDL abaixo é uma baseline para migrations EF Core. Nomes, particionamento e ledger precisam ser revisados pelo DBA e pelo ADR final.

```sql
CREATE TABLE dbo.tenants (
  tenant_id uniqueidentifier NOT NULL PRIMARY KEY,
  name nvarchar(200) NOT NULL,
  status varchar(30) NOT NULL,
  region varchar(50) NOT NULL,
  created_at datetime2(7) NOT NULL CONSTRAINT DF_tenants_created DEFAULT SYSUTCDATETIME(),
  row_ver rowversion NOT NULL
);

CREATE TABLE dbo.projects (
  project_id uniqueidentifier NOT NULL PRIMARY KEY,
  tenant_id uniqueidentifier NOT NULL,
  name nvarchar(200) NOT NULL,
  policy_version varchar(80) NOT NULL,
  policy_sha256 char(64) NOT NULL,
  status varchar(30) NOT NULL,
  created_at datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
  row_ver rowversion NOT NULL,
  CONSTRAINT FK_projects_tenant FOREIGN KEY (tenant_id) REFERENCES dbo.tenants(tenant_id),
  CONSTRAINT CK_projects_policy_hash CHECK (LEN(policy_sha256)=64)
);
CREATE INDEX IX_projects_tenant_status ON dbo.projects(tenant_id,status);

CREATE TABLE dbo.mailbox_targets (
  mailbox_target_id uniqueidentifier NOT NULL PRIMARY KEY,
  tenant_id uniqueidentifier NOT NULL,
  project_id uniqueidentifier NOT NULL,
  entra_object_id uniqueidentifier NULL,
  exchange_guid uniqueidentifier NOT NULL,
  archive_guid uniqueidentifier NULL,
  upn_ciphertext varbinary(max) NULL,
  upn_hmac char(64) NOT NULL,
  recipient_type varchar(50) NOT NULL,
  archive_status varchar(30) NOT NULL,
  auto_expanding_enabled bit NOT NULL DEFAULT 0,
  observed_archive_bytes bigint NULL,
  observed_at datetime2(7) NULL,
  row_ver rowversion NOT NULL,
  CONSTRAINT FK_targets_project FOREIGN KEY(project_id) REFERENCES dbo.projects(project_id)
);
CREATE UNIQUE INDEX UX_targets_tenant_exchange ON dbo.mailbox_targets(tenant_id,exchange_guid);

CREATE TABLE dbo.source_objects (
  source_id uniqueidentifier NOT NULL PRIMARY KEY,
  tenant_id uniqueidentifier NOT NULL,
  project_id uniqueidentifier NOT NULL,
  mailbox_target_id uniqueidentifier NOT NULL,
  source_type varchar(40) NOT NULL,
  artifact_uri nvarchar(1000) NOT NULL,
  sha256 char(64) NULL,
  total_bytes bigint NULL,
  status varchar(40) NOT NULL,
  discovered_at datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
  hashed_at datetime2(7) NULL,
  row_ver rowversion NOT NULL,
  CONSTRAINT FK_sources_project FOREIGN KEY(project_id) REFERENCES dbo.projects(project_id),
  CONSTRAINT FK_sources_target FOREIGN KEY(mailbox_target_id) REFERENCES dbo.mailbox_targets(mailbox_target_id),
  CONSTRAINT CK_sources_bytes CHECK(total_bytes IS NULL OR total_bytes >= 0)
);
CREATE UNIQUE INDEX UX_source_tenant_sha ON dbo.source_objects(tenant_id,sha256) WHERE sha256 IS NOT NULL;

CREATE TABLE dbo.pst_parts (
  part_id uniqueidentifier NOT NULL PRIMARY KEY,
  tenant_id uniqueidentifier NOT NULL,
  project_id uniqueidentifier NOT NULL,
  source_id uniqueidentifier NOT NULL,
  plan_id uniqueidentifier NOT NULL,
  logical_name varchar(180) NOT NULL,
  artifact_uri nvarchar(1000) NOT NULL,
  sha256 char(64) NOT NULL,
  total_bytes bigint NOT NULL,
  expected_items bigint NOT NULL,
  min_date datetime2(7) NULL,
  max_date datetime2(7) NULL,
  status varchar(40) NOT NULL,
  engine_name varchar(100) NOT NULL,
  engine_version varchar(50) NOT NULL,
  validated_at datetime2(7) NULL,
  import_locked bit NOT NULL DEFAULT 0,
  row_ver rowversion NOT NULL,
  CONSTRAINT FK_parts_source FOREIGN KEY(source_id) REFERENCES dbo.source_objects(source_id),
  CONSTRAINT CK_parts_hash CHECK(LEN(sha256)=64),
  CONSTRAINT CK_parts_bytes CHECK(total_bytes > 0),
  CONSTRAINT CK_parts_items CHECK(expected_items >= 0)
);
CREATE UNIQUE INDEX UX_part_tenant_sha ON dbo.pst_parts(tenant_id,sha256);

CREATE TABLE dbo.import_waves (
  wave_id uniqueidentifier NOT NULL PRIMARY KEY,
  tenant_id uniqueidentifier NOT NULL,
  project_id uniqueidentifier NOT NULL,
  mailbox_target_id uniqueidentifier NOT NULL,
  provider varchar(60) NOT NULL,
  provider_capability_id uniqueidentifier NOT NULL,
  target_root_folder nvarchar(200) NOT NULL,
  planned_bytes bigint NOT NULL,
  planned_items bigint NOT NULL,
  status varchar(40) NOT NULL,
  portal_job_name varchar(100) NULL,
  portal_job_id nvarchar(300) NULL,
  created_at datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
  row_ver rowversion NOT NULL,
  CONSTRAINT FK_waves_target FOREIGN KEY(mailbox_target_id) REFERENCES dbo.mailbox_targets(mailbox_target_id),
  CONSTRAINT CK_waves_root CHECK(target_root_folder <> '/' AND target_root_folder LIKE '/%')
);

CREATE TABLE dbo.wave_parts (
  wave_part_id uniqueidentifier NOT NULL PRIMARY KEY,
  tenant_id uniqueidentifier NOT NULL,
  wave_id uniqueidentifier NOT NULL,
  part_id uniqueidentifier NOT NULL,
  mailbox_target_id uniqueidentifier NOT NULL,
  target_root_folder nvarchar(200) NOT NULL,
  part_sha256 char(64) NOT NULL,
  ordinal int NOT NULL,
  CONSTRAINT FK_waveparts_wave FOREIGN KEY(wave_id) REFERENCES dbo.import_waves(wave_id),
  CONSTRAINT FK_waveparts_part FOREIGN KEY(part_id) REFERENCES dbo.pst_parts(part_id)
);
CREATE UNIQUE INDEX UX_wave_part_target_root
ON dbo.wave_parts(tenant_id,mailbox_target_id,target_root_folder,part_sha256);

CREATE TABLE dbo.custody_events (
  custody_event_id uniqueidentifier NOT NULL PRIMARY KEY,
  tenant_id uniqueidentifier NOT NULL,
  project_id uniqueidentifier NOT NULL,
  aggregate_id uniqueidentifier NOT NULL,
  sequence_no bigint NOT NULL,
  event_type varchar(100) NOT NULL,
  actor_type varchar(40) NOT NULL,
  actor_id nvarchar(200) NOT NULL,
  occurred_at datetime2(7) NOT NULL,
  previous_event_hash char(64) NULL,
  payload_hash char(64) NOT NULL,
  event_hash char(64) NOT NULL,
  payload_json nvarchar(max) NOT NULL,
  CONSTRAINT CK_custody_json CHECK(ISJSON(payload_json)=1)
);
CREATE UNIQUE INDEX UX_custody_aggregate_sequence
ON dbo.custody_events(tenant_id,aggregate_id,sequence_no);

CREATE TABLE dbo.outbox_messages (
  message_id uniqueidentifier NOT NULL PRIMARY KEY,
  tenant_id uniqueidentifier NOT NULL,
  event_type varchar(150) NOT NULL,
  payload_json nvarchar(max) NOT NULL,
  occurred_at datetime2(7) NOT NULL,
  published_at datetime2(7) NULL,
  publish_attempts int NOT NULL DEFAULT 0,
  CONSTRAINT CK_outbox_json CHECK(ISJSON(payload_json)=1)
);
CREATE INDEX IX_outbox_unpublished ON dbo.outbox_messages(published_at,occurred_at) WHERE published_at IS NULL;

CREATE TABLE dbo.inbox_messages (
  consumer_name varchar(100) NOT NULL,
  message_id uniqueidentifier NOT NULL,
  tenant_id uniqueidentifier NOT NULL,
  processed_at datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
  PRIMARY KEY(consumer_name,message_id)
);
```

# Apêndice B - Manifesto de partição

```json
{
  "schema": "pst-part-manifest/v1",
  "manifestId": "man_01J...",
  "tenantId": "tnt_01J...",
  "projectId": "prj_01J...",
  "source": {
    "sourceId": "src_01J...",
    "sha256": "1f5d...a91e",
    "bytes": 536870912000,
    "sourceType": "PstFile"
  },
  "part": {
    "partId": "part_01J...",
    "logicalName": "p_01JABC_part001.pst",
    "sha256": "6a72...f0b4",
    "bytes": 18942001321,
    "expectedItems": 184233,
    "dateMin": "2014-01-01T00:00:00Z",
    "dateMax": "2014-06-30T23:59:59Z",
    "partitionRule": "folder+date+size",
    "targetRootFolder": "/ImportedPst_PRJ01_W001"
  },
  "engine": {
    "name": "Aspose.Email",
    "version": "<PINNED_VERSION>",
    "plannerVersion": "1.0.0",
    "policyVersion": "pv_01J..."
  },
  "validation": {
    "primary": "PASS",
    "independent": "PASS",
    "malwareScan": "PASS"
  },
  "createdAt": "2026-07-20T12:00:00Z",
  "manifestSha256": "sha256-of-canonical-json-without-this-field"
}
```

# Apêndice C - Códigos de erro

| **Código** | **Retry** | **Significado** |
| --- | --- | --- |
| `SOURCE_STILL_CHANGING` | não automático | arquivo não está estável |
| `SOURCE_HASH_MISMATCH` | não | byte stream mudou |
| `PST_OPEN_FAILED` | policy | engine não abriu |
| `PST_PASSWORD_REQUIRED` | externo | senha ausente |
| `PST_PART_OVERSIZE` | replan | part excedeu limite |
| `PST_VALIDATION_DIVERGENCE` | não | engines divergiram |
| `TARGET_IDENTITY_AMBIGUOUS` | externo | owner/target não único |
| `TARGET_ARCHIVE_DISABLED` | externo | archive não ativo |
| `M365_ARCHIVE_IMPORT_LIMIT` | não | excede limite Purview |
| `PURVIEW_CSV_TOO_MANY_ROWS` | replan | \>500 linhas |
| `PURVIEW_TARGET_ROOT_UNSAFE` | não | `/` ou inválido |
| `PURVIEW_SAS_EXPIRED` | externo | novo SAS requerido |
| `PURVIEW_MAPPING_INVALID` | corrigir | validation report |
| `UNSAFE_REPLAY_BLOCKED` | não | hash/root/lineage divergem |
| `GRAPH_FTS_ARCHIVE_NOT_APPROVED` | não | capability bloqueada |
| `EVIDENCE_INCOMPLETE` | corrigir | job não pode fechar |

# Apêndice D - Checklist diário do operador

186. Confirmar Service Health da Microsoft e saúde do Azure.
187. Verificar DLQ, queue age e workers.
188. Verificar disk pressure e certificados.
189. Revisar jobs `WAITING_EXTERNAL`, `FAILED` e `QUARANTINED`.
190. Confirmar ondas aprovadas e four-eyes.
191. Confirmar secrets/SAS com expiração próxima.
192. Não iniciar duas ondas concorrentes para a mesma mailbox sem autorização do scheduler.
193. Conferir upload → CSV → portal job IDs.
194. Registrar status do provider e anexar relatórios.
195. Revisar evidence completeness antes de encerrar o turno.

# Apêndice E - Pacote de evidência final

```text
evidence-<projectId>-<jobId>.zip
  manifest.json
  manifest.sig
  manifest-cert-chain.pem
  source/
    source-inventory.json
    source-hashes.csv
    ev-export-report.csv
  preparation/
    inspections/
    partition-plan.json
    part-manifests/
    validation-reports/
  destination/
    capability-evidence.json
    prechecks.json
    azcopy-summary.json
    mapping.csv
    mapping.sha256
    purview-validation-report/
    provider-results/
  reconciliation/
    before/
    after/
    recon-result.json
    exception-dispositions.csv
  audit/
    approvals.json
    custody-chain.ndjson
    custody-chain-root.sha256
  closeout/
    final-certificate.pdf
    client-acceptance.json
```

# Apêndice F - Referências oficiais e source of truth

196. [Microsoft Learn - PST Import overview e permissões](https://learn.microsoft.com/en-us/purview/pst-import-overview)
197. [Microsoft Learn - Network upload, AzCopy e mapping CSV](https://learn.microsoft.com/en-us/purview/pst-import-network-upload)
198. [Microsoft Learn - Troubleshooting PST Import, 24 GB/dia e limite de archive](https://learn.microsoft.com/en-us/troubleshoot/microsoft-365/purview/pst-import-service/issues-with-pst-import-job)
199. [Microsoft Learn - PST Import FAQ e SourceEntryId](https://learn.microsoft.com/en-us/purview/pst-import-faq)
200. [Microsoft Learn - Enable auto-expanding archiving](https://learn.microsoft.com/en-us/purview/enable-autoexpanding-archiving)
201. [Microsoft Learn - Graph mailbox import/export concept](https://learn.microsoft.com/en-us/graph/mailbox-import-export-concept-overview)
202. [Microsoft Learn - Graph mailbox import/export v1.0](https://learn.microsoft.com/en-us/graph/api/resources/mailbox-import-export-api-overview?view=graph-rest-1.0)
203. [Microsoft Learn - Import Exchange mailbox item com FTS](https://learn.microsoft.com/en-us/graph/import-exchange-mailbox-item)
204. [Microsoft Learn - Archive mailbox redirects](https://learn.microsoft.com/en-us/graph/handle-archive-mailbox-redirects)
205. [Microsoft Learn - EWS deprecation](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/deprecation-of-ews-exchange-online)
206. [Microsoft Learn - Get-EXOMailboxStatistics](https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/get-exomailboxstatistics?view=exchange-ps)
207. [Microsoft Learn - Get-EXOMailboxFolderStatistics](https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/get-exomailboxfolderstatistics?view=exchange-ps)
208. [Microsoft Learn - App-only authentication Exchange Online PowerShell](https://learn.microsoft.com/en-us/powershell/exchange/app-only-auth-powershell-v2?view=exchange-ps)
209. [Microsoft Open Specifications - MS-PST](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-pst/141923d5-15ab-4ef1-a524-6dce75aae546)
210. [Veritas - Export-EVArchive](https://www.veritas.com/support/en_US/doc/96069939-161741702-0/v118534516-161741702)
211. [Aspose.Email - Split PST](https://docs.aspose.com/email/net/managing-messages-in-pst-files/)
212. [Aspose API - PersonalStorage.SplitInto](https://reference.aspose.com/email/net/aspose.email.storage.pst/personalstorage/splitinto/)
213. [libpff - repositório oficial](https://github.com/libyal/libpff)
214. [Quest - Archive Shuttle datasheet](https://www.quest.com/documents/quadrotech-archive-shuttle-datasheet-147908.pdf)
215. [Microsoft Learn - Azure Service Bus Well-Architected](https://learn.microsoft.com/en-us/azure/well-architected/service-guides/azure-service-bus)

> [!IMPORTANT]
> **NOTA DE ENGENHARIA**
> As referências foram validadas em 20/07/2026. O módulo `support-matrix` precisa revalidá-las antes de cada release e sempre que um provider alterar versão, limite, portal ou API.
