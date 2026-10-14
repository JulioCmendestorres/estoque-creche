/* ============================================================================
   V005 — Gerenciamento de usuários (E5.1, decisões D-33 a D-36)
   - Perfis cadastráveis por tenant, cada um com a sua lista de permissões
     (o catálogo de permissões fica no código: Permissoes.cs).
   - Um único usuário MASTER por tenant: transita entre as unidades e tem
     todas as permissões, inclusive gerenciar usuários e perfis.
   - Os demais usuários ficam presos a UMA unidade (usuarios.unidade_id).
   - Substitui o papel fixo (papeis_usuario/usuarios.papel) e usuarios_unidades.
   Dados existentes: cada tenant ganha os perfis Administrador, Operacional e
   Visualizador; o admin ativo mais antigo vira o master; a unidade de cada
   usuário passa a ser a matriz (se tinha acesso a ela) ou a primeira vinculada.
   ============================================================================ */

CREATE TABLE perfis (
    id             INT IDENTITY(1,1) NOT NULL,
    tenant_id      INT           NOT NULL,
    nome           VARCHAR(60)   NOT NULL,
    descricao      VARCHAR(255)  NULL,
    ativo          BIT           NOT NULL CONSTRAINT DF_perfis_ativo DEFAULT 1,
    criado_em      DATETIME2     NOT NULL CONSTRAINT DF_perfis_criado DEFAULT SYSUTCDATETIME(),
    atualizado_em  DATETIME2     NULL,
    CONSTRAINT PK_perfis PRIMARY KEY (id),
    CONSTRAINT UQ_perfis_id_tenant UNIQUE (id, tenant_id),
    CONSTRAINT UQ_perfis_tenant_nome UNIQUE (tenant_id, nome),
    CONSTRAINT FK_perfis_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id)
);

CREATE TABLE perfis_permissoes (
    tenant_id   INT          NOT NULL,
    perfil_id   INT          NOT NULL,
    permissao   VARCHAR(50)  NOT NULL,
    CONSTRAINT PK_perfis_permissoes PRIMARY KEY (perfil_id, permissao),
    CONSTRAINT FK_pp_perfil FOREIGN KEY (perfil_id, tenant_id) REFERENCES perfis(id, tenant_id)
);

ALTER TABLE usuarios ADD
    perfil_id   INT NULL,
    unidade_id  INT NULL,
    master      BIT NOT NULL CONSTRAINT DF_usuarios_master DEFAULT 0;
GO

-- ----------------------------------------------------------------------------
-- Perfis padrão por tenant (mesma lista do Seeder)
-- ----------------------------------------------------------------------------
INSERT INTO perfis (tenant_id, nome, descricao)
SELECT t.id, p.nome, p.descricao
FROM tenants t
CROSS JOIN (VALUES
    ('Administrador', 'Pessoa de confiança: tudo, inclusive cadastros compartilhados e visão consolidada'),
    ('Operacional',   'Registra entradas e saídas e consulta relatórios da própria unidade'),
    ('Visualizador',  'Apenas consulta estoque, movimentações e relatórios da própria unidade')
) AS p(nome, descricao);

INSERT INTO perfis_permissoes (tenant_id, perfil_id, permissao)
SELECT p.tenant_id, p.id, x.permissao
FROM perfis p
JOIN (VALUES
    ('Administrador', 'estoque.movimentar'), ('Administrador', 'estoque.cancelar'),
    ('Administrador', 'cadastros.itens'), ('Administrador', 'cadastros.categorias'),
    ('Administrador', 'cadastros.unidades_medida'), ('Administrador', 'cadastros.fornecedores'),
    ('Administrador', 'relatorios.ver'), ('Administrador', 'relatorios.exportar'),
    ('Administrador', 'global.consolidado'),
    ('Operacional',   'estoque.movimentar'), ('Operacional', 'relatorios.ver'), ('Operacional', 'relatorios.exportar'),
    ('Visualizador',  'relatorios.ver')
) AS x(perfil, permissao) ON x.perfil = p.nome;

-- Papel antigo → perfil de mesmo sentido
UPDATE u SET perfil_id = p.id
FROM usuarios u
JOIN perfis p ON p.tenant_id = u.tenant_id
            AND p.nome = CASE u.papel WHEN 'admin' THEN 'Administrador'
                                      WHEN 'operacional' THEN 'Operacional'
                                      ELSE 'Visualizador' END;

-- Unidade única: matriz se o usuário tinha acesso a ela; senão a primeira vinculada; senão a matriz do tenant
UPDATE u SET unidade_id = COALESCE(
    (SELECT TOP 1 uu.unidade_id
     FROM usuarios_unidades uu JOIN unidades un ON un.id = uu.unidade_id
     WHERE uu.usuario_id = u.id
     ORDER BY CASE un.tipo WHEN 'matriz' THEN 0 ELSE 1 END, un.ativo DESC, un.id),
    (SELECT TOP 1 un.id FROM unidades un WHERE un.tenant_id = u.tenant_id
     ORDER BY CASE un.tipo WHEN 'matriz' THEN 0 ELSE 1 END, un.id))
FROM usuarios u;

-- Master: o admin ativo mais antigo de cada tenant
UPDATE u SET master = 1
FROM usuarios u
WHERE u.id = (SELECT MIN(x.id) FROM usuarios x WHERE x.tenant_id = u.tenant_id AND x.papel = 'admin' AND x.ativo = 1);
GO

-- ----------------------------------------------------------------------------
-- Restrições novas e remoção do modelo antigo
-- ----------------------------------------------------------------------------
ALTER TABLE usuarios ALTER COLUMN unidade_id INT NOT NULL;
ALTER TABLE usuarios ADD
    CONSTRAINT FK_usuarios_perfil  FOREIGN KEY (perfil_id, tenant_id)  REFERENCES perfis(id, tenant_id),
    CONSTRAINT FK_usuarios_unidade FOREIGN KEY (unidade_id, tenant_id) REFERENCES unidades(id, tenant_id),
    CONSTRAINT CK_usuarios_perfil  CHECK (master = 1 OR perfil_id IS NOT NULL);
CREATE UNIQUE INDEX UX_usuarios_um_master ON usuarios(tenant_id) WHERE master = 1;
CREATE INDEX IX_usuarios_perfil ON usuarios(perfil_id);

ALTER TABLE usuarios DROP CONSTRAINT FK_usuarios_papel;
ALTER TABLE usuarios DROP CONSTRAINT DF_usuarios_papel;
ALTER TABLE usuarios DROP COLUMN papel;
DROP TABLE usuarios_unidades;
DROP TABLE papeis_usuario;
GO

-- ----------------------------------------------------------------------------
-- Auditoria: usuários (sem o hash da senha), perfis e permissões
-- ----------------------------------------------------------------------------
CREATE OR ALTER TRIGGER trg_usuarios_auditoria ON usuarios AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @unidade_id INT         = TRY_CAST(SESSION_CONTEXT(N'unidade_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
    BEGIN
        UPDATE u SET atualizado_em = SYSUTCDATETIME()
        FROM usuarios u JOIN inserted i ON i.id = u.id;

        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'usuarios', 'UPDATE', i.id,
               (SELECT d.id, d.tenant_id, d.nome, d.email, d.perfil_id, d.unidade_id, d.master, d.ativo, d.trocar_senha,
                       d.ultimo_login_em, d.tentativas_falhas_login, d.bloqueado_ate, d.criado_em, d.atualizado_em,
                       CAST(CASE WHEN d.senha <> i.senha THEN 1 ELSE 0 END AS BIT) AS senha_alterada
                FROM deleted d WHERE d.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.id, n.tenant_id, n.nome, n.email, n.perfil_id, n.unidade_id, n.master, n.ativo, n.trocar_senha,
                       n.ultimo_login_em, n.tentativas_falhas_login, n.bloqueado_ate, n.criado_em, n.atualizado_em
                FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    END
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'usuarios', 'INSERT', i.id,
               (SELECT n.id, n.tenant_id, n.nome, n.email, n.perfil_id, n.unidade_id, n.master, n.ativo, n.trocar_senha, n.criado_em
                FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
        SELECT d.tenant_id, @unidade_id, @usuario_id, 'usuarios', 'DELETE', d.id,
               (SELECT o.id, o.tenant_id, o.nome, o.email, o.perfil_id, o.unidade_id, o.master, o.ativo
                FROM deleted o WHERE o.id = d.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM deleted d;
END;
GO

CREATE TRIGGER trg_perfis_auditoria ON perfis AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @unidade_id INT         = TRY_CAST(SESSION_CONTEXT(N'unidade_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
    BEGIN
        UPDATE p SET atualizado_em = SYSUTCDATETIME()
        FROM perfis p JOIN inserted i ON i.id = p.id;

        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'perfis', 'UPDATE', i.id,
               (SELECT d.* FROM deleted d WHERE d.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    END
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'perfis', 'INSERT', i.id,
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
        SELECT d.tenant_id, @unidade_id, @usuario_id, 'perfis', 'DELETE', d.id,
               (SELECT o.* FROM deleted o WHERE o.id = d.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM deleted d;
END;
GO

-- Uma linha de log por permissão concedida (INSERT) ou retirada (DELETE); registro_id = perfil
CREATE TRIGGER trg_perfis_permissoes_auditoria ON perfis_permissoes AFTER INSERT, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @unidade_id INT         = TRY_CAST(SESSION_CONTEXT(N'unidade_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
    SELECT i.tenant_id, @unidade_id, @usuario_id, 'perfis_permissoes', 'INSERT', i.perfil_id,
           (SELECT i.perfil_id, i.permissao FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
           @ip
    FROM inserted i;

    INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
    SELECT d.tenant_id, @unidade_id, @usuario_id, 'perfis_permissoes', 'DELETE', d.perfil_id,
           (SELECT d.perfil_id, d.permissao FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
           @ip
    FROM deleted d;
END;
GO
