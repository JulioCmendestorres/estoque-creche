/* ============================================================================
   SISTEMA DE GESTÃO DE ESTOQUE E DOAÇÕES
   Modelagem de Banco de Dados — Microsoft SQL Server (T-SQL)
   Compatível com SQL Server 2016+ (usa STRING_AGG-free JSON nativo, DATETIME2)

   Este script parte da modelagem original (pensada em sintaxe MySQL) e faz
   os ajustes necessários para rodar em SQL Server, além de reforçar, no
   próprio banco, as regras de negócio já levantadas nos requisitos (RF01-RF13)
   e nos diagramas de atividade (Registrar entrada / Registrar saída).
   ============================================================================ */

-- ----------------------------------------------------------------------------
-- 0. BANCO DE DADOS
-- ----------------------------------------------------------------------------
IF DB_ID(N'EstoqueDoacoes') IS NULL
BEGIN
    CREATE DATABASE EstoqueDoacoes;
END
GO

USE EstoqueDoacoes;
GO

-- ----------------------------------------------------------------------------
-- (Opcional) Script de limpeza para reexecutar em ambiente de desenvolvimento.
-- Descomente apenas se quiser recriar tudo do zero.
-- ----------------------------------------------------------------------------
-- DROP TABLE IF EXISTS logs_auditoria;
-- DROP TABLE IF EXISTS movimentacoes;
-- DROP TABLE IF EXISTS parceiros;
-- DROP TABLE IF EXISTS itens;
-- DROP TABLE IF EXISTS categorias_itens;
-- DROP TABLE IF EXISTS unidades_medida;
-- DROP TABLE IF EXISTS usuarios;
-- DROP TABLE IF EXISTS papeis_usuario;
-- GO


/* ============================================================================
   1. TABELAS DE APOIO (lookup tables)

   Diferente de tipo_movimentacao/status/acao (que ficam em CHECK, porque um
   valor novo exigiria programar um comportamento novo mesmo assim), "papel"
   e "unidade_medida" são listas que podem crescer por decisão de negócio,
   sem precisar de deploy — por isso viram tabela em vez de CHECK.

   Usamos o próprio código (VARCHAR) como chave primária, e não um id
   substituto (surrogate int): como o código já é curto, estável e é
   exatamente o valor que a aplicação usa, um id numérico só adicionaria
   um JOIN sem necessidade. Isso também mantém o restante do modelo
   praticamente intocado (a coluna "papel" em usuarios continua se
   chamando "papel" e continua VARCHAR — só ganhou uma FK no lugar do CHECK).
   ============================================================================ */
CREATE TABLE papeis_usuario (
    codigo      VARCHAR(20)   NOT NULL,
    descricao   VARCHAR(100)  NULL,
    ativo       BIT           NOT NULL DEFAULT 1,

    CONSTRAINT PK_papeis_usuario PRIMARY KEY (codigo)
);
GO

INSERT INTO papeis_usuario (codigo, descricao) VALUES
    ('admin', 'Acesso total ao sistema'),
    ('operacional', 'Registra entradas, saídas e cadastros do dia a dia'),
    ('visualizador', 'Apenas consulta relatórios, estoque e dashboards');
GO

CREATE TABLE unidades_medida (
    codigo      VARCHAR(10)   NOT NULL,
    descricao   VARCHAR(50)   NULL,
    ativo       BIT           NOT NULL DEFAULT 1,

    CONSTRAINT PK_unidades_medida PRIMARY KEY (codigo)
);
GO

INSERT INTO unidades_medida (codigo, descricao) VALUES
    ('unidade', 'Unidade avulsa'),
    ('kg', 'Quilograma'),
    ('litro', 'Litro'),
    ('pacote', 'Pacote'),
    ('caixa', 'Caixa');
GO


/* ============================================================================
   2. USUARIOS
   ============================================================================ */
CREATE TABLE usuarios (
    id              INT IDENTITY(1,1) NOT NULL,
    nome            VARCHAR(255)  NOT NULL,
    email           VARCHAR(255)  NOT NULL,
    senha           VARCHAR(255)  NOT NULL,        -- hash da senha (nunca texto puro)
    papel           VARCHAR(20)   NOT NULL DEFAULT 'operacional',
    ativo           BIT           NOT NULL DEFAULT 1,
    criado_em       DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    atualizado_em   DATETIME2     NULL,

    CONSTRAINT PK_usuarios PRIMARY KEY (id),
    CONSTRAINT UQ_usuarios_email UNIQUE (email),
    CONSTRAINT FK_usuarios_papel FOREIGN KEY (papel) REFERENCES papeis_usuario(codigo)
);
GO


/* ============================================================================
   3. CATEGORIAS_ITENS
   ============================================================================ */
CREATE TABLE categorias_itens (
    id          INT IDENTITY(1,1) NOT NULL,
    nome        VARCHAR(100)  NOT NULL,
    descricao   VARCHAR(MAX)  NULL,
    ativo       BIT           NOT NULL DEFAULT 1,
    criado_em   DATETIME2     NOT NULL DEFAULT SYSDATETIME(),

    CONSTRAINT PK_categorias_itens PRIMARY KEY (id),
    CONSTRAINT UQ_categorias_itens_nome UNIQUE (nome)
);
GO


/* ============================================================================
   4. ITENS
   ============================================================================ */
CREATE TABLE itens (
    id                   INT IDENTITY(1,1) NOT NULL,
    codigo_interno       VARCHAR(50)   NOT NULL,
    nome                 VARCHAR(255)  NOT NULL,
    descricao            VARCHAR(MAX)  NULL,
    categoria_id         INT           NOT NULL,
    unidade_medida       VARCHAR(10)   NOT NULL,
    quantidade_atual     INT           NOT NULL DEFAULT 0,
    quantidade_minima    INT           NOT NULL DEFAULT 0,
    valor_total_estoque  DECIMAL(12,2) NOT NULL DEFAULT 0,
    ativo                BIT           NOT NULL DEFAULT 1,
    criado_em            DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    atualizado_em        DATETIME2     NULL,

    CONSTRAINT PK_itens PRIMARY KEY (id),
    CONSTRAINT UQ_itens_codigo_interno UNIQUE (codigo_interno),
    CONSTRAINT FK_itens_categoria FOREIGN KEY (categoria_id)
        REFERENCES categorias_itens(id),
    CONSTRAINT FK_itens_unidade_medida FOREIGN KEY (unidade_medida)
        REFERENCES unidades_medida(codigo),

    -- RF04: o estoque nunca pode ficar negativo (mesma regra já validada
    -- no diagrama de atividades de "Registrar saída", decisão "Quantidade suficiente?")
    CONSTRAINT CK_itens_quantidade_atual CHECK (quantidade_atual >= 0),
    CONSTRAINT CK_itens_quantidade_minima CHECK (quantidade_minima >= 0),
    CONSTRAINT CK_itens_valor_total_estoque CHECK (valor_total_estoque >= 0)
);
GO

-- RF12 (alertas de estoque baixo): coluna calculada e persistida para permitir
-- consultar/filtrar itens abaixo do mínimo sem repetir a comparação em toda query.
ALTER TABLE itens
    ADD abaixo_do_minimo AS (
        CASE WHEN quantidade_atual <= quantidade_minima THEN 1 ELSE 0 END
    ) PERSISTED;
GO


/* ============================================================================
   5. PARCEIROS  (fornecedores / doadores)
   ============================================================================ */
CREATE TABLE parceiros (
    id            INT IDENTITY(1,1) NOT NULL,
    nome          VARCHAR(255)  NOT NULL,
    tipo          VARCHAR(20)   NOT NULL,
    cpf_cnpj      VARCHAR(20)   NULL,
    email         VARCHAR(255)  NULL,
    telefone      VARCHAR(20)   NULL,
    endereco      VARCHAR(MAX)  NULL,
    ativo         BIT           NOT NULL DEFAULT 1,
    criado_em     DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    atualizado_em DATETIME2     NULL,

    CONSTRAINT PK_parceiros PRIMARY KEY (id),
    CONSTRAINT UQ_parceiros_cpf_cnpj UNIQUE (cpf_cnpj),
    CONSTRAINT CK_parceiros_tipo CHECK (
        tipo IN ('pessoa_fisica','empresa','organizacao')
    )
);
GO


/* ============================================================================
   6. MOVIMENTACOES
   ============================================================================ */
CREATE TABLE movimentacoes (
    id                    INT IDENTITY(1,1) NOT NULL,
    numero_movimentacao   VARCHAR(50)   NOT NULL,
    tipo_movimentacao     VARCHAR(20)   NOT NULL,
    item_id               INT           NOT NULL,
    quantidade            INT           NOT NULL,
    valor_unitario        DECIMAL(10,2) NOT NULL,
    -- valor_total deixou de ser uma coluna independente: agora é CALCULADA,
    -- para que nunca fique dessincronizada de quantidade x valor_unitario.
    valor_total           AS (CAST(quantidade * valor_unitario AS DECIMAL(12,2))) PERSISTED,
    data_movimentacao     DATE          NOT NULL,
    parceiro_id           INT           NULL,
    descricao_saida       VARCHAR(255)  NULL,
    beneficiario          VARCHAR(255)  NULL,
    responsavel_id        INT           NOT NULL,
    observacoes           VARCHAR(MAX)  NULL,
    status                VARCHAR(20)   NOT NULL DEFAULT 'pendente',
    criado_em             DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    atualizado_em         DATETIME2     NULL,

    CONSTRAINT PK_movimentacoes PRIMARY KEY (id),
    CONSTRAINT UQ_movimentacoes_numero UNIQUE (numero_movimentacao),

    CONSTRAINT FK_mov_item     FOREIGN KEY (item_id)        REFERENCES itens(id),
    CONSTRAINT FK_mov_parceiro FOREIGN KEY (parceiro_id)    REFERENCES parceiros(id),
    CONSTRAINT FK_mov_resp     FOREIGN KEY (responsavel_id) REFERENCES usuarios(id),

    CONSTRAINT CK_mov_tipo CHECK (
        tipo_movimentacao IN ('doacao_recebida','compra','doacao_realizada','consumo_interno')
    ),
    CONSTRAINT CK_mov_status CHECK (
        status IN ('pendente','processada','cancelada')
    ),
    CONSTRAINT CK_mov_quantidade CHECK (quantidade > 0),
    CONSTRAINT CK_mov_valor_unitario CHECK (valor_unitario >= 0),

    -- Ajuste de regra de negócio (RF02 + RF06):
    --   - 'compra'            -> fornecedor SEMPRE identificado (parceiro_id obrigatório)
    --   - 'doacao_recebida'   -> fornecedor pode ficar anônimo (parceiro_id opcional)
    --   - 'doacao_realizada'/'consumo_interno' (saídas) -> não usam parceiro,
    --      pois não há fornecedor envolvido em uma saída
    CONSTRAINT CK_mov_parceiro_coerente CHECK (
        (tipo_movimentacao = 'compra' AND parceiro_id IS NOT NULL)
        OR (tipo_movimentacao = 'doacao_recebida')
        OR (tipo_movimentacao IN ('doacao_realizada','consumo_interno') AND parceiro_id IS NULL)
    ),

    -- Ajuste de regra de negócio (RF03): toda saída por doação precisa
    -- identificar quem foi beneficiado, para fins de prestação de contas.
    CONSTRAINT CK_mov_beneficiario_coerente CHECK (
        (tipo_movimentacao = 'doacao_realizada' AND beneficiario IS NOT NULL)
        OR (tipo_movimentacao <> 'doacao_realizada')
    )
);
GO

-- Índices em FKs: diferente do MySQL/InnoDB, o SQL Server NÃO cria automaticamente
-- um índice para colunas de chave estrangeira. Sem isso, toda consulta de
-- "movimentações de um item" ou "movimentações de um fornecedor" faria table scan.
CREATE INDEX IX_mov_item       ON movimentacoes(item_id);
CREATE INDEX IX_mov_parceiro   ON movimentacoes(parceiro_id);
CREATE INDEX IX_mov_resp       ON movimentacoes(responsavel_id);
CREATE INDEX IX_mov_data       ON movimentacoes(data_movimentacao);
CREATE INDEX IX_itens_categoria ON itens(categoria_id);
GO


/* ============================================================================
   7. LOGS_AUDITORIA
   ============================================================================ */
CREATE TABLE logs_auditoria (
    id              INT IDENTITY(1,1) NOT NULL,
    usuario_id      INT           NULL,
    tabela_afetada  VARCHAR(100)  NOT NULL,
    acao            VARCHAR(10)   NOT NULL,
    registro_id     INT           NULL,
    -- SQL Server não tem um tipo de coluna JSON nativo (diferente do MySQL).
    -- Guarda-se como NVARCHAR(MAX) e valida-se o conteúdo com ISJSON().
    dados_antigos   NVARCHAR(MAX) NULL,
    dados_novos     NVARCHAR(MAX) NULL,
    ip_usuario      VARCHAR(45)   NULL,
    criado_em       DATETIME2     NOT NULL DEFAULT SYSDATETIME(),

    CONSTRAINT PK_logs_auditoria PRIMARY KEY (id),
    CONSTRAINT FK_log_usuario FOREIGN KEY (usuario_id) REFERENCES usuarios(id),
    CONSTRAINT CK_log_acao CHECK (acao IN ('INSERT','UPDATE','DELETE')),
    CONSTRAINT CK_log_dados_antigos_json CHECK (dados_antigos IS NULL OR ISJSON(dados_antigos) = 1),
    CONSTRAINT CK_log_dados_novos_json   CHECK (dados_novos   IS NULL OR ISJSON(dados_novos)   = 1)
);
GO

CREATE INDEX IX_log_usuario ON logs_auditoria(usuario_id);
CREATE INDEX IX_log_tabela_registro ON logs_auditoria(tabela_afetada, registro_id);
GO


/* ============================================================================
   8. TRIGGERS — "ON UPDATE CURRENT_TIMESTAMP" não existe no SQL Server.
   No MySQL a coluna atualizado_em se atualiza sozinha; aqui isso precisa
   ser feito via trigger em cada tabela que tem essa coluna.
   ============================================================================ */
GO
CREATE TRIGGER trg_usuarios_atualizado_em ON usuarios AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE u SET atualizado_em = SYSDATETIME()
    FROM usuarios u JOIN inserted i ON i.id = u.id;
END;
GO

CREATE TRIGGER trg_itens_atualizado_em ON itens AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(quantidade_atual) OR UPDATE(valor_total_estoque) OR UPDATE(nome)
       OR UPDATE(descricao) OR UPDATE(categoria_id) OR UPDATE(unidade_medida)
       OR UPDATE(quantidade_minima) OR UPDATE(ativo)
    BEGIN
        UPDATE it SET atualizado_em = SYSDATETIME()
        FROM itens it JOIN inserted i ON i.id = it.id;
    END
END;
GO

CREATE TRIGGER trg_parceiros_atualizado_em ON parceiros AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    UPDATE p SET atualizado_em = SYSDATETIME()
    FROM parceiros p JOIN inserted i ON i.id = p.id;
END;
GO

CREATE TRIGGER trg_movimentacoes_atualizado_em ON movimentacoes AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF NOT UPDATE(atualizado_em)
    BEGIN
        UPDATE m SET atualizado_em = SYSDATETIME()
        FROM movimentacoes m JOIN inserted i ON i.id = m.id;
    END
END;
GO


/* ============================================================================
   9. TRIGGER — RF04: manter itens.quantidade_atual / valor_total_estoque
   sempre coerentes com as movimentações PROCESSADAS (sem depender da
   aplicação lembrar de atualizar o estoque manualmente).

   Regra implementada:
     - dispara quando uma movimentação PASSA a ter status = 'processada'
       (seja na inserção, seja numa atualização de status);
     - estorna automaticamente o efeito se uma movimentação processada
       for cancelada.

   Limitação assumida (documentar na monografia): este trigger não trata
   a edição de quantidade/valor de uma movimentação já processada — a
   recomendação de regra de negócio é NÃO permitir editar uma movimentação
   processada pela aplicação; para corrigi-la, cancele e crie uma nova.
   ============================================================================ */
GO
CREATE TRIGGER trg_movimentacoes_atualiza_estoque
ON movimentacoes
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- (1) Movimentações que passaram a "processada" agora (entrada ou saída)
    ;WITH recem_processadas AS (
        SELECT ins.item_id, ins.quantidade, ins.valor_unitario, ins.tipo_movimentacao
        FROM inserted ins
        LEFT JOIN deleted del ON del.id = ins.id
        WHERE ins.status = 'processada'
          AND (del.id IS NULL OR del.status <> 'processada')
    )
    UPDATE it
    SET it.quantidade_atual =
            it.quantidade_atual
            + CASE WHEN rp.tipo_movimentacao IN ('doacao_recebida','compra')
                   THEN rp.quantidade ELSE -rp.quantidade END,
        it.valor_total_estoque =
            it.valor_total_estoque
            + CASE WHEN rp.tipo_movimentacao IN ('doacao_recebida','compra')
                   THEN rp.quantidade * rp.valor_unitario
                   ELSE -(rp.quantidade * rp.valor_unitario) END
    FROM itens it
    JOIN recem_processadas rp ON rp.item_id = it.id;

    -- (2) Movimentações processadas que agora foram canceladas -> estorno
    ;WITH estornadas AS (
        SELECT ins.item_id, ins.quantidade, ins.valor_unitario, ins.tipo_movimentacao
        FROM inserted ins
        JOIN deleted del ON del.id = ins.id
        WHERE del.status = 'processada' AND ins.status = 'cancelada'
    )
    UPDATE it
    SET it.quantidade_atual =
            it.quantidade_atual
            - CASE WHEN es.tipo_movimentacao IN ('doacao_recebida','compra')
                   THEN es.quantidade ELSE -es.quantidade END,
        it.valor_total_estoque =
            it.valor_total_estoque
            - CASE WHEN es.tipo_movimentacao IN ('doacao_recebida','compra')
                   THEN es.quantidade * es.valor_unitario
                   ELSE -(es.quantidade * es.valor_unitario) END
    FROM itens it
    JOIN estornadas es ON es.item_id = it.id;

    -- Rede de segurança no nível do banco: mesmo que a aplicação falhe em
    -- checar o estoque antes de processar uma saída, o banco recusa a
    -- operação em vez de permitir estoque negativo (reforça RF04).
    IF EXISTS (SELECT 1 FROM itens WHERE quantidade_atual < 0)
    BEGIN
        RAISERROR(N'Operação bloqueada: deixaria a quantidade em estoque negativa.', 16, 1);
        ROLLBACK TRANSACTION;
    END
END;
GO


/* ============================================================================
   10. TRIGGERS DE AUDITORIA — RF13 (log de atividades)
   Implementados para "itens" e "movimentacoes" por serem as tabelas mais
   sensíveis do ponto de vista de prestação de contas. O mesmo padrão pode
   ser replicado para parceiros/categorias_itens/usuarios, se a banca exigir
   auditoria completa de todas as tabelas.

   Observação importante: um trigger não sabe, sozinho, qual usuário da
   APLICAÇÃO fez a alteração (ele só vê a conexão do banco). Por isso a
   aplicação deve informar o usuário logado por sessão, assim:
       EXEC sp_set_session_context 'usuario_id', @id_do_usuario_logado;
   logo após abrir a conexão, antes de qualquer INSERT/UPDATE/DELETE.
   ============================================================================ */
GO
CREATE TRIGGER trg_itens_auditoria ON itens AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        INSERT INTO logs_auditoria (usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos)
        SELECT @usuario_id, 'itens', 'UPDATE', i.id,
               (SELECT d.* FROM deleted d WHERE d.id = i.id FOR JSON AUTO),
               (SELECT i2.* FROM inserted i2 WHERE i2.id = i.id FOR JSON AUTO)
        FROM inserted i;
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (usuario_id, tabela_afetada, acao, registro_id, dados_novos)
        SELECT @usuario_id, 'itens', 'INSERT', i.id,
               (SELECT i2.* FROM inserted i2 WHERE i2.id = i.id FOR JSON AUTO)
        FROM inserted i;
    ELSE
        INSERT INTO logs_auditoria (usuario_id, tabela_afetada, acao, registro_id, dados_antigos)
        SELECT @usuario_id, 'itens', 'DELETE', d.id,
               (SELECT d2.* FROM deleted d2 WHERE d2.id = d.id FOR JSON AUTO)
        FROM deleted d;
END;
GO

CREATE TRIGGER trg_movimentacoes_auditoria ON movimentacoes AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @usuario_id INT = TRY_CAST(SESSION_CONTEXT(N'usuario_id') AS INT);

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
        INSERT INTO logs_auditoria (usuario_id, tabela_afetada, acao, registro_id, dados_antigos, dados_novos)
        SELECT @usuario_id, 'movimentacoes', 'UPDATE', m.id,
               (SELECT d.* FROM deleted d WHERE d.id = m.id FOR JSON AUTO),
               (SELECT i.* FROM inserted i WHERE i.id = m.id FOR JSON AUTO)
        FROM inserted m;
    ELSE IF EXISTS (SELECT 1 FROM inserted)
        INSERT INTO logs_auditoria (usuario_id, tabela_afetada, acao, registro_id, dados_novos)
        SELECT @usuario_id, 'movimentacoes', 'INSERT', m.id,
               (SELECT i.* FROM inserted i WHERE i.id = m.id FOR JSON AUTO)
        FROM inserted m;
    ELSE
        INSERT INTO logs_auditoria (usuario_id, tabela_afetada, acao, registro_id, dados_antigos)
        SELECT @usuario_id, 'movimentacoes', 'DELETE', d.id,
               (SELECT d2.* FROM deleted d2 WHERE d2.id = d.id FOR JSON AUTO)
        FROM deleted d;
END;
GO
