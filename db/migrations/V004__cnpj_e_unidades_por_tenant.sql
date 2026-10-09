/* ============================================================================
   V004 — (1) Parceiros: só CNPJ (opcional); CPF de pessoa não é mais registrado.
          (2) Unidades de medida passam a ser cadastráveis por tenant.
   ============================================================================ */

-- ----------------------------------------------------------------------------
-- (1) CPF fora do sistema (minimização de dados pessoais, LGPD)
-- ----------------------------------------------------------------------------
-- O trigger de auditoria é desligado só durante a limpeza: sem isso o UPDATE
-- gravaria o CPF antigo, de novo, em logs_auditoria.dados_antigos.
DISABLE TRIGGER trg_parceiros_auditoria ON parceiros;
GO

UPDATE parceiros
SET cpf_cnpj = NULL, atualizado_em = SYSUTCDATETIME()
WHERE cpf_cnpj IS NOT NULL AND (LEN(cpf_cnpj) <> 14 OR cpf_cnpj LIKE '%[^0-9]%');
GO

ENABLE TRIGGER trg_parceiros_auditoria ON parceiros;
GO

-- Remove o valor do JSON dos logs antigos (a chave deixa de existir no registro).
UPDATE logs_auditoria
SET dados_antigos = CASE
        WHEN JSON_VALUE(dados_antigos, '$.cpf_cnpj') IS NOT NULL
         AND (LEN(JSON_VALUE(dados_antigos, '$.cpf_cnpj')) <> 14 OR JSON_VALUE(dados_antigos, '$.cpf_cnpj') LIKE '%[^0-9]%')
        THEN JSON_MODIFY(dados_antigos, '$.cpf_cnpj', NULL) ELSE dados_antigos END,
    dados_novos = CASE
        WHEN JSON_VALUE(dados_novos, '$.cpf_cnpj') IS NOT NULL
         AND (LEN(JSON_VALUE(dados_novos, '$.cpf_cnpj')) <> 14 OR JSON_VALUE(dados_novos, '$.cpf_cnpj') LIKE '%[^0-9]%')
        THEN JSON_MODIFY(dados_novos, '$.cpf_cnpj', NULL) ELSE dados_novos END
WHERE tabela_afetada = 'parceiros'
  AND (JSON_VALUE(dados_antigos, '$.cpf_cnpj') IS NOT NULL OR JSON_VALUE(dados_novos, '$.cpf_cnpj') IS NOT NULL);
GO

-- O índice único filtrado usa a coluna no filtro, o que impede o RENAME: recria-se com o novo nome.
DROP INDEX UX_parceiros_tenant_doc ON parceiros;
GO
EXEC sp_rename 'dbo.parceiros.cpf_cnpj', 'cnpj', 'COLUMN';
GO
CREATE UNIQUE INDEX UX_parceiros_tenant_cnpj ON parceiros(tenant_id, cnpj) WHERE cnpj IS NOT NULL;
GO

-- O banco passa a recusar qualquer documento que não seja um CNPJ de 14 dígitos
ALTER TABLE parceiros ADD CONSTRAINT CK_parceiros_cnpj
    CHECK (cnpj IS NULL OR (LEN(cnpj) = 14 AND cnpj NOT LIKE '%[^0-9]%'));
GO

-- ----------------------------------------------------------------------------
-- (2) Unidades de medida por tenant
--     itens.unidade_medida continua guardando o código; a FK agora é composta
--     (tenant_id, codigo), então um tenant não usa a unidade de outro.
-- ----------------------------------------------------------------------------
SELECT codigo, descricao, ativo INTO #um FROM unidades_medida;
GO

ALTER TABLE itens DROP CONSTRAINT FK_itens_unidade_medida;
GO
DROP TABLE unidades_medida;
GO

CREATE TABLE unidades_medida (
    id          INT IDENTITY(1,1) NOT NULL,
    tenant_id   INT           NOT NULL,
    codigo      VARCHAR(10)   NOT NULL,          -- sigla; não muda depois de criada (itens a referenciam)
    descricao   VARCHAR(50)   NOT NULL,
    ativo       BIT           NOT NULL CONSTRAINT DF_um_ativo DEFAULT 1,
    criado_em   DATETIME2     NOT NULL CONSTRAINT DF_um_criado DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_unidades_medida PRIMARY KEY (id),
    CONSTRAINT FK_um_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT UQ_um_tenant_codigo UNIQUE (tenant_id, codigo),
    CONSTRAINT CK_um_codigo CHECK (LEN(codigo) > 0)
);
GO

-- Cada tenant existente recebe uma cópia da lista padrão anterior
INSERT INTO unidades_medida (tenant_id, codigo, descricao, ativo)
SELECT t.id, u.codigo, ISNULL(u.descricao, u.codigo), u.ativo
FROM tenants t CROSS JOIN #um u;
GO

DROP TABLE #um;
GO

ALTER TABLE itens ADD CONSTRAINT FK_itens_unidade_medida
    FOREIGN KEY (tenant_id, unidade_medida) REFERENCES unidades_medida(tenant_id, codigo);
GO

CREATE TRIGGER trg_unidades_medida_auditoria ON unidades_medida AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @unidade_id INT         = TRY_CAST(SESSION_CONTEXT(N'unidade_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'unidades_medida', 'UPDATE', i.id,
               (SELECT d.* FROM deleted d WHERE d.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'unidades_medida', 'INSERT', i.id,
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
        SELECT d.tenant_id, @unidade_id, @usuario_id, 'unidades_medida', 'DELETE', d.id,
               (SELECT o.* FROM deleted o WHERE o.id = d.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM deleted d;
END;
GO
