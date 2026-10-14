/* ============================================================================
   V006 — Item ativo por unidade (D-38)
   O catálogo de itens continua compartilhado pelo tenant, mas cada unidade
   decide se usa o item: estoque_unidade.ativo = 0 tira o item da busca de
   entrada/saída e da tela de estoque SÓ daquela unidade. Sem linha em
   estoque_unidade, o item está ativo na unidade.
   A aplicação só desativa com saldo zerado; o banco recusa nova movimentação
   de item desativado na unidade (THROW 50006).
   ============================================================================ */

ALTER TABLE estoque_unidade ADD ativo BIT NOT NULL CONSTRAINT DF_est_ativo DEFAULT 1;
GO

-- Quem já podia mexer no catálogo de itens também ganha a permissão por unidade
INSERT INTO perfis_permissoes (tenant_id, perfil_id, permissao)
SELECT pp.tenant_id, pp.perfil_id, 'estoque.itens_unidade'
FROM perfis_permissoes pp
WHERE pp.permissao = 'cadastros.itens'
  AND NOT EXISTS (SELECT 1 FROM perfis_permissoes x WHERE x.perfil_id = pp.perfil_id AND x.permissao = 'estoque.itens_unidade');
GO

CREATE TRIGGER trg_mov_item_ativo_unidade ON movimentacoes AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN estoque_unidade e ON e.unidade_id = i.unidade_id AND e.item_id = i.item_id
        WHERE e.ativo = 0)
        THROW 50006, 'Item desativado nesta unidade.', 1;
END;
GO
