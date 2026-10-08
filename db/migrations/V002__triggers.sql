/* ============================================================================
   V002 — Triggers (A10, A11, A12, A14)

   Convenções:
   - Cada tabela auditada tem UM trigger "_auditoria" que também preenche
     atualizado_em (UTC). Um trigger separado só para atualizado_em faria o
     UPDATE interno disparar a auditoria de novo (RECURSIVE_TRIGGERS OFF só
     impede a recursão direta do mesmo trigger), gerando logs duplicados.
   - O usuário da APLICAÇÃO chega via SESSION_CONTEXT (usuario_id, tenant_id,
     unidade_id, ip), preenchido pelo interceptor de conexão a cada abertura.
   - Erros de negócio usam THROW com números 50001+ para a aplicação
     traduzir em mensagens amigáveis.
   ============================================================================ */

-- ----------------------------------------------------------------------------
-- movimentacoes: validação de imutabilidade e de transição de status (A11)
-- ----------------------------------------------------------------------------
CREATE TRIGGER trg_mov_validacao ON movimentacoes AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted d ON d.id = i.id
        WHERE d.status IN ('processada','cancelada')
          AND (   i.quantidade        <> d.quantidade
               OR i.valor_unitario    <> d.valor_unitario
               OR i.item_id           <> d.item_id
               OR i.unidade_id        <> d.unidade_id
               OR i.tenant_id         <> d.tenant_id
               OR i.tipo_movimentacao <> d.tipo_movimentacao
               OR ISNULL(i.parceiro_id, -1) <> ISNULL(d.parceiro_id, -1))
    )
        THROW 50002, N'Movimentação processada ou cancelada não pode ser alterada. Cancele e registre outra.', 1;

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted d ON d.id = i.id
        WHERE i.status <> d.status
          AND NOT (   (d.status = 'pendente'   AND i.status IN ('processada','cancelada'))
                   OR (d.status = 'processada' AND i.status = 'cancelada'))
    )
        THROW 50003, N'Transição de status inválida para a movimentação.', 1;
END;
GO

EXEC sp_settriggerorder @triggername = N'trg_mov_validacao', @order = N'First', @stmttype = N'UPDATE';
GO

-- ----------------------------------------------------------------------------
-- movimentacoes: atualização do estoque por unidade (A10)
--   (a) agrega por (unidade, item): várias linhas do mesmo item no mesmo
--       INSERT somam corretamente;
--   (b) cria a linha de estoque_unidade se ainda não existir (upsert);
--   (c) saída baixa o valor pelo CUSTO MÉDIO; o valor_unitario da saída é
--       apenas informativo (valor da doação para prestação de contas);
--   (d) THROW 50001 quando faltaria saldo;
--   (e) só as linhas afetadas são validadas.
--   Estorno (processada -> cancelada): entrada cancelada retira quantidade e
--   o valor pelo qual entrou; saída cancelada devolve a quantidade pelo custo
--   médio atual (ou pelo valor_unitario, se o saldo estiver zerado).
-- ----------------------------------------------------------------------------
CREATE TRIGGER trg_mov_estoque ON movimentacoes AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    DECLARE @ev TABLE (
        tenant_id  INT NOT NULL,
        unidade_id INT NOT NULL,
        item_id    INT NOT NULL,
        e_q  INT            NOT NULL,   -- entradas processadas agora
        e_v  DECIMAL(18,4)  NOT NULL,
        s_q  INT            NOT NULL,   -- saídas processadas agora
        ce_q INT            NOT NULL,   -- entradas canceladas (estorno)
        ce_v DECIMAL(18,4)  NOT NULL,
        cs_q INT            NOT NULL,   -- saídas canceladas (estorno)
        cs_v DECIMAL(18,4)  NOT NULL,
        PRIMARY KEY (unidade_id, item_id)
    );

    INSERT INTO @ev (tenant_id, unidade_id, item_id, e_q, e_v, s_q, ce_q, ce_v, cs_q, cs_v)
    SELECT x.tenant_id, x.unidade_id, x.item_id,
           SUM(CASE WHEN x.processou = 1 AND x.entrada = 1 THEN x.quantidade ELSE 0 END),
           SUM(CASE WHEN x.processou = 1 AND x.entrada = 1 THEN x.quantidade * x.valor_unitario ELSE 0 END),
           SUM(CASE WHEN x.processou = 1 AND x.entrada = 0 THEN x.quantidade ELSE 0 END),
           SUM(CASE WHEN x.cancelou  = 1 AND x.entrada = 1 THEN x.quantidade ELSE 0 END),
           SUM(CASE WHEN x.cancelou  = 1 AND x.entrada = 1 THEN x.quantidade * x.valor_unitario ELSE 0 END),
           SUM(CASE WHEN x.cancelou  = 1 AND x.entrada = 0 THEN x.quantidade ELSE 0 END),
           SUM(CASE WHEN x.cancelou  = 1 AND x.entrada = 0 THEN x.quantidade * x.valor_unitario ELSE 0 END)
    FROM (
        SELECT i.tenant_id, i.unidade_id, i.item_id, i.quantidade, i.valor_unitario,
               CASE WHEN i.status = 'processada' AND (d.id IS NULL OR d.status <> 'processada') THEN 1 ELSE 0 END AS processou,
               CASE WHEN i.status = 'cancelada'  AND d.status = 'processada' THEN 1 ELSE 0 END AS cancelou,
               CASE WHEN i.tipo_movimentacao IN ('doacao_recebida','compra') THEN 1 ELSE 0 END AS entrada
        FROM inserted i
        LEFT JOIN deleted d ON d.id = i.id
    ) x
    WHERE x.processou = 1 OR x.cancelou = 1
    GROUP BY x.tenant_id, x.unidade_id, x.item_id;

    IF NOT EXISTS (SELECT 1 FROM @ev) RETURN;

    -- (b) upsert da linha de saldo
    INSERT INTO estoque_unidade (tenant_id, unidade_id, item_id)
    SELECT e.tenant_id, e.unidade_id, e.item_id
    FROM @ev e
    WHERE NOT EXISTS (
        SELECT 1 FROM estoque_unidade eu WITH (UPDLOCK, HOLDLOCK)
        WHERE eu.unidade_id = e.unidade_id AND eu.item_id = e.item_id
    );

    DECLARE @calc TABLE (
        unidade_id INT NOT NULL,
        item_id    INT NOT NULL,
        q_final    INT NOT NULL,
        v_final    DECIMAL(12,2) NOT NULL,
        PRIMARY KEY (unidade_id, item_id)
    );

    INSERT INTO @calc (unidade_id, item_id, q_final, v_final)
    SELECT e.unidade_id, e.item_id, p2.q2,
           CAST(CASE WHEN p2.q2 <= 0 OR p2.v2 < 0 THEN 0 ELSE ROUND(p2.v2, 2) END AS DECIMAL(12,2))
    FROM @ev e
    JOIN estoque_unidade eu WITH (UPDLOCK, HOLDLOCK)
      ON eu.unidade_id = e.unidade_id AND eu.item_id = e.item_id
    CROSS APPLY (SELECT CAST(eu.quantidade_atual AS INT) AS q0,
                        CAST(eu.valor_total_estoque AS DECIMAL(18,4)) AS v0) p0
    -- 1º: tudo que soma (entradas e estornos de saída)
    CROSS APPLY (SELECT p0.q0 + e.e_q + e.cs_q AS q1,
                        p0.v0 + e.e_v
                          + CASE WHEN e.cs_q = 0 THEN 0
                                 WHEN p0.q0 > 0 THEN e.cs_q * (p0.v0 / p0.q0)
                                 ELSE e.cs_v END AS v1) p1
    -- 2º: tudo que subtrai (saídas pelo custo médio e estornos de entrada pelo valor de entrada)
    CROSS APPLY (SELECT p1.q1 - e.s_q - e.ce_q AS q2,
                        p1.v1 - e.ce_v
                          - CASE WHEN p1.q1 > 0 THEN e.s_q * (p1.v1 / p1.q1) ELSE 0 END AS v2) p2;

    IF EXISTS (SELECT 1 FROM @calc WHERE q_final < 0)
        THROW 50001, N'Estoque insuficiente para concluir a operação.', 1;

    UPDATE eu
    SET eu.quantidade_atual    = c.q_final,
        eu.valor_total_estoque = c.v_final
    FROM estoque_unidade eu
    JOIN @calc c ON c.unidade_id = eu.unidade_id AND c.item_id = eu.item_id;
END;
GO

-- ----------------------------------------------------------------------------
-- Bloqueio de DELETE (A11). Também há DENY DELETE ao login estoque_app.
-- ----------------------------------------------------------------------------
CREATE TRIGGER trg_mov_sem_delete ON movimentacoes INSTEAD OF DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50004, N'Movimentações não podem ser excluídas; use o cancelamento.', 1;
END;
GO

CREATE TRIGGER trg_est_sem_delete ON estoque_unidade INSTEAD OF DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 50005, N'Linhas de estoque não podem ser excluídas.', 1;
END;
GO

-- ----------------------------------------------------------------------------
-- Auditoria (A14) + atualizado_em em UTC (A12)
-- ----------------------------------------------------------------------------
CREATE TRIGGER trg_movimentacoes_auditoria ON movimentacoes AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
    BEGIN
        UPDATE m SET atualizado_em = SYSUTCDATETIME()
        FROM movimentacoes m JOIN inserted i ON i.id = m.id;

        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos, ip_usuario)
        SELECT i.tenant_id, i.unidade_id, @usuario_id, 'movimentacoes', 'UPDATE', i.id,
               (SELECT d.* FROM deleted d WHERE d.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    END
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, i.unidade_id, @usuario_id, 'movimentacoes', 'INSERT', i.id,
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
        SELECT d.tenant_id, d.unidade_id, @usuario_id, 'movimentacoes', 'DELETE', d.id,
               (SELECT o.* FROM deleted o WHERE o.id = d.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM deleted d;
END;
GO

CREATE TRIGGER trg_estoque_unidade_auditoria ON estoque_unidade AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
    BEGIN
        UPDATE eu SET atualizado_em = SYSUTCDATETIME()
        FROM estoque_unidade eu JOIN inserted i ON i.unidade_id = eu.unidade_id AND i.item_id = eu.item_id;

        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos, ip_usuario)
        SELECT i.tenant_id, i.unidade_id, @usuario_id, 'estoque_unidade', 'UPDATE', i.item_id,
               (SELECT d.* FROM deleted d WHERE d.unidade_id = i.unidade_id AND d.item_id = i.item_id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.* FROM inserted n WHERE n.unidade_id = i.unidade_id AND n.item_id = i.item_id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    END
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, i.unidade_id, @usuario_id, 'estoque_unidade', 'INSERT', i.item_id,
               (SELECT n.* FROM inserted n WHERE n.unidade_id = i.unidade_id AND n.item_id = i.item_id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
END;
GO

CREATE TRIGGER trg_itens_auditoria ON itens AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @unidade_id INT         = TRY_CAST(SESSION_CONTEXT(N'unidade_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
    BEGIN
        UPDATE it SET atualizado_em = SYSUTCDATETIME()
        FROM itens it JOIN inserted i ON i.id = it.id;

        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'itens', 'UPDATE', i.id,
               (SELECT d.* FROM deleted d WHERE d.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    END
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'itens', 'INSERT', i.id,
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
        SELECT d.tenant_id, @unidade_id, @usuario_id, 'itens', 'DELETE', d.id,
               (SELECT o.* FROM deleted o WHERE o.id = d.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM deleted d;
END;
GO

CREATE TRIGGER trg_parceiros_auditoria ON parceiros AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @unidade_id INT         = TRY_CAST(SESSION_CONTEXT(N'unidade_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
    BEGIN
        UPDATE p SET atualizado_em = SYSUTCDATETIME()
        FROM parceiros p JOIN inserted i ON i.id = p.id;

        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'parceiros', 'UPDATE', i.id,
               (SELECT d.* FROM deleted d WHERE d.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    END
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'parceiros', 'INSERT', i.id,
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
        SELECT d.tenant_id, @unidade_id, @usuario_id, 'parceiros', 'DELETE', d.id,
               (SELECT o.* FROM deleted o WHERE o.id = d.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM deleted d;
END;
GO

CREATE TRIGGER trg_categorias_itens_auditoria ON categorias_itens AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT         = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);
    DECLARE @unidade_id INT         = TRY_CAST(SESSION_CONTEXT(N'unidade_id') AS INT);
    DECLARE @ip         VARCHAR(45) = TRY_CAST(SESSION_CONTEXT(N'ip') AS VARCHAR(45));

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'categorias_itens', 'UPDATE', i.id,
               (SELECT d.* FROM deleted d WHERE d.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'categorias_itens', 'INSERT', i.id,
               (SELECT n.* FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
        SELECT d.tenant_id, @unidade_id, @usuario_id, 'categorias_itens', 'DELETE', d.id,
               (SELECT o.* FROM deleted o WHERE o.id = d.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM deleted d;
END;
GO

-- usuarios: a coluna "senha" (hash) NUNCA vai para o JSON de auditoria (A14)
CREATE TRIGGER trg_usuarios_auditoria ON usuarios AFTER INSERT, UPDATE, DELETE AS
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
               (SELECT d.id, d.tenant_id, d.nome, d.email, d.papel, d.ativo, d.trocar_senha, d.ultimo_login_em,
                       d.tentativas_falhas_login, d.bloqueado_ate, d.criado_em, d.atualizado_em,
                       CAST(CASE WHEN d.senha <> i.senha THEN 1 ELSE 0 END AS BIT) AS senha_alterada
                FROM deleted d WHERE d.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               (SELECT n.id, n.tenant_id, n.nome, n.email, n.papel, n.ativo, n.trocar_senha, n.ultimo_login_em,
                       n.tentativas_falhas_login, n.bloqueado_ate, n.criado_em, n.atualizado_em
                FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    END
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_novos, ip_usuario)
        SELECT i.tenant_id, @unidade_id, @usuario_id, 'usuarios', 'INSERT', i.id,
               (SELECT n.id, n.tenant_id, n.nome, n.email, n.papel, n.ativo, n.trocar_senha, n.criado_em
                FROM inserted n WHERE n.id = i.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (tenant_id, unidade_id, usuario_id, tabela_afetada, acao, registro_id, dados_antigos, ip_usuario)
        SELECT d.tenant_id, @unidade_id, @usuario_id, 'usuarios', 'DELETE', d.id,
               (SELECT o.id, o.tenant_id, o.nome, o.email, o.papel, o.ativo
                FROM deleted o WHERE o.id = d.id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
               @ip
        FROM deleted d;
END;
GO
