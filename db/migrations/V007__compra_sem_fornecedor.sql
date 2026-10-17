/* ============================================================================
   V007 — Compra sem fornecedor obrigatório (D-40)
   Antes (V001, RF02/RF06): compra exigia parceiro_id. Agora qualquer entrada
   (compra ou doação recebida) pode ficar sem fornecedor; saídas continuam sem.
   ============================================================================ */

ALTER TABLE movimentacoes DROP CONSTRAINT CK_mov_parceiro_coerente;

ALTER TABLE movimentacoes ADD CONSTRAINT CK_mov_parceiro_coerente CHECK (
    tipo_movimentacao IN ('compra', 'doacao_recebida')
    OR (tipo_movimentacao IN ('doacao_realizada', 'consumo_interno', 'avaria') AND parceiro_id IS NULL)
);
