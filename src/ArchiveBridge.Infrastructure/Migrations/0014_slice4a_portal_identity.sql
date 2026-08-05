-- Slice 4A — identidade do Portal Operacional (Control Plane).
--
-- Estas são tabelas de IDENTIDADE/ACESSO do portal e, DELIBERADAMENTE, NÃO estão sob a Row-Level
-- Security por tenant: elas são justamente o mecanismo que estabelece o tenant efetivo de um usuário
-- (o login precisa localizar o usuário ANTES de existir um contexto de tenant). O isolamento aqui é
-- por PRIVILÉGIO (GRANT à identidade da aplicação) e por constraints. Nenhuma senha em claro é
-- armazenada — apenas o hash derivado (algoritmo, iterações, sal e subchave em Base64).
--
-- Aditiva e não destrutiva: cria apenas objetos novos; não altera migrations anteriores
-- (0001–0013) nem tabelas existentes.

CREATE TABLE dbo.portal_users
(
    user_id             UNIQUEIDENTIFIER NOT NULL,
    username            NVARCHAR(200)    NOT NULL,
    display_name        NVARCHAR(200)    NOT NULL,
    tenant_id           UNIQUEIDENTIFIER NOT NULL,
    project_id          UNIQUEIDENTIFIER NOT NULL,
    enabled             BIT              NOT NULL CONSTRAINT DF_portal_users_enabled DEFAULT 1,
    password_algorithm  NVARCHAR(50)     NOT NULL,
    password_iterations INT              NOT NULL,
    password_salt       NVARCHAR(200)    NOT NULL,
    password_hash       NVARCHAR(200)    NOT NULL,
    created_at_utc      DATETIME2(3)     NOT NULL CONSTRAINT DF_portal_users_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_portal_users PRIMARY KEY (user_id),
    CONSTRAINT UQ_portal_users_username UNIQUE (username),
    CONSTRAINT CK_portal_users_iterations CHECK (password_iterations > 0)
);

-- Catálogo fechado de papéis (RBAC). O CHECK em portal_user_roles referencia esta tabela, de modo que
-- um papel desconhecido é recusado pelo banco (fail-closed), não apenas pela aplicação.
CREATE TABLE dbo.portal_roles
(
    role_name NVARCHAR(50) NOT NULL,
    CONSTRAINT PK_portal_roles PRIMARY KEY (role_name)
);

INSERT INTO dbo.portal_roles (role_name)
VALUES (N'Viewer'), (N'Operator'), (N'Approver'), (N'Auditor'), (N'Administrator');

CREATE TABLE dbo.portal_user_roles
(
    user_id   UNIQUEIDENTIFIER NOT NULL,
    role_name NVARCHAR(50)     NOT NULL,
    CONSTRAINT PK_portal_user_roles PRIMARY KEY (user_id, role_name),
    CONSTRAINT FK_portal_user_roles_user FOREIGN KEY (user_id) REFERENCES dbo.portal_users (user_id),
    CONSTRAINT FK_portal_user_roles_role FOREIGN KEY (role_name) REFERENCES dbo.portal_roles (role_name)
);

-- Auditoria de autenticação: registra SUCESSO e FALHA. Append-only (apenas SELECT/INSERT para a app).
CREATE TABLE dbo.portal_sign_in_events
(
    event_id        BIGINT IDENTITY (1, 1) NOT NULL,
    username        NVARCHAR(200) NOT NULL,
    succeeded       BIT           NOT NULL,
    reason          NVARCHAR(100) NOT NULL,
    remote_address  NVARCHAR(100) NULL,
    occurred_at_utc DATETIME2(3)  NOT NULL,
    CONSTRAINT PK_portal_sign_in_events PRIMARY KEY (event_id)
);

CREATE INDEX IX_portal_sign_in_events_time ON dbo.portal_sign_in_events (occurred_at_utc DESC);

-- Privilégios da identidade da aplicação (ab_app_role). O login lê usuários/papéis; a administração
-- insere usuários e vínculos; a auditoria insere/lê tentativas. Sem UPDATE/DELETE (identidade imutável
-- nesta fatia; alterações administrativas entram em rodada posterior, com seus próprios grants).
GRANT SELECT, INSERT ON dbo.portal_users TO ab_app_role;
GRANT SELECT ON dbo.portal_roles TO ab_app_role;
GRANT SELECT, INSERT ON dbo.portal_user_roles TO ab_app_role;
GRANT SELECT, INSERT ON dbo.portal_sign_in_events TO ab_app_role;
