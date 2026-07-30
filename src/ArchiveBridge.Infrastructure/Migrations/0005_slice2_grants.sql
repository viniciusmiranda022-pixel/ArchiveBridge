-- Slice 2 — privilégios das identidades SQL sobre as novas tabelas (separação concreta, não apenas
-- convenção em C#). A identidade da aplicação (ab_app_role) escreve o ciclo de planejamento; a de
-- manutenção (ab_maintenance_role, reaper do Slice 1) tem apenas leitura para auditoria.
--
-- Imutabilidade reforçada por PRIVILÉGIO (append-only): tabelas de versão/evidência recebem apenas
-- SELECT/INSERT (sem UPDATE/DELETE). Em mapping_csv_versions, o único UPDATE permitido é da coluna
-- 'status' (marcar Superseded) — os hashes e a contagem nunca podem ser alterados pela aplicação.

-- Agregados mutáveis (transições de estado, versionamento controlado pelo domínio + gatilhos).
GRANT SELECT, INSERT, UPDATE ON dbo.migration_projects TO ab_app_role;
GRANT SELECT, INSERT, UPDATE ON dbo.migration_waves TO ab_app_role;

-- Histórico/evidência imutável (append-only).
GRANT SELECT, INSERT ON dbo.project_configuration_versions TO ab_app_role;
GRANT SELECT, INSERT ON dbo.wave_versions TO ab_app_role;
GRANT SELECT, INSERT ON dbo.wave_entries TO ab_app_role;
GRANT SELECT, INSERT ON dbo.mapping_csv_rows TO ab_app_role;
GRANT SELECT, INSERT ON dbo.planning_assessments TO ab_app_role;
GRANT SELECT, INSERT ON dbo.approvals TO ab_app_role;

-- Versões de mapping: insere novas versões e só pode mudar o STATUS (Superseded). Hashes imutáveis.
GRANT SELECT, INSERT ON dbo.mapping_csv_versions TO ab_app_role;
GRANT UPDATE (status) ON dbo.mapping_csv_versions TO ab_app_role;

-- Manutenção (reaper): não opera sobre planejamento; apenas leitura para auditoria/observabilidade.
GRANT SELECT ON dbo.migration_projects TO ab_maintenance_role;
GRANT SELECT ON dbo.migration_waves TO ab_maintenance_role;
GRANT SELECT ON dbo.planning_assessments TO ab_maintenance_role;
GRANT SELECT ON dbo.approvals TO ab_maintenance_role;
