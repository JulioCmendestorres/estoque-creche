/* ============================================================================
   V008 — Unidades com nome próprio, sem "matriz" e "filial" (D-46)
   Antes (V001, A02): cada unidade tinha um tipo ('matriz' ou 'filial') e só
   podia haver uma matriz por tenant. Nenhuma regra de negócio dependia do tipo:
   servia para ordenar a lista e escrever "(Matriz)"/"(Filial)" ao lado do nome.
   Agora uma unidade é só nome + ativa/inativa, e o master cadastra quantas
   precisar. As unidades existentes mantêm o nome que já tinham.
   Também passa a auditar a tabela (criar, renomear, desativar, reativar).
   ============================================================================ */

DROP INDEX UX_unidades_uma_matriz ON unidades;
GO

ALTER TABLE unidades DROP CONSTRAINT CK_unidades_tipo;
GO

ALTER TABLE unidades DROP COLUMN tipo;
GO

CREATE TRIGGER trg_unidades_auditoria ON unidades AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @unidade_id INT         = TRY_CAST(SESSION_CONTEXT(N'unidade_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'unidades', 'UPDATE', i.id,
               (SELECT d.* FROM deleted d WHERE d.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'unidades', 'INSERT', i.id,
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
        SELECT d.tenant_id, @unidade_id, @usuario_id, 'unidades', 'DELETE', d.id,
               (SELECT o.* FROM deleted o WHERE o.id = d.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM deleted d;
END;
GO
