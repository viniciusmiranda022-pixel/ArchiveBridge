-- Separação concreta de privilégios por papel (não apenas convenção em C#).
--   ab_app_role:         identidade da aplicação por tenant. DML nas tabelas de Job, SEM bypass
--                        de RLS (não pertence ao papel de manutenção).
--   ab_maintenance_role: identidade EXCLUSIVA do reaper. É o ÚNICO papel que a RLS honra para o
--                        modo de manutenção (SESSION_CONTEXT('is_maintenance')=1). A identidade da
--                        aplicação é tecnicamente incapaz de bypassar o isolamento por tenant,
--                        mesmo que defina 'is_maintenance', pois não é membro deste papel.
-- Os USUÁRIOS (com credenciais) são criados no provisionamento/implantação (e, nos testes, pela
-- fixture) e adicionados a estes papéis — nenhuma senha é versionada.

CREATE ROLE ab_app_role;
CREATE ROLE ab_maintenance_role;

-- Aplicação: DML completo do ciclo de vida (cria projeto/Job, tentativas e auditoria).
GRANT SELECT, INSERT, UPDATE ON dbo.projects TO ab_app_role;
GRANT SELECT, INSERT, UPDATE ON dbo.jobs TO ab_app_role;
GRANT SELECT, INSERT ON dbo.job_attempts TO ab_app_role;
GRANT SELECT, INSERT ON dbo.job_state_transitions TO ab_app_role;

-- Manutenção (reaper): apenas o necessário para recuperar leases expirados e auditar.
GRANT SELECT, UPDATE ON dbo.jobs TO ab_maintenance_role;
GRANT SELECT, INSERT ON dbo.job_state_transitions TO ab_maintenance_role;
GRANT SELECT ON dbo.projects TO ab_maintenance_role;
GRANT SELECT ON dbo.job_attempts TO ab_maintenance_role;
