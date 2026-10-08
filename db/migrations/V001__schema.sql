/* ============================================================================
   V001 — Schema consolidado (multi-tenant)
   Parte de docs/referencia/Cria_Banco.sql e aplica os ajustes A01–A17 e A19
   do PLANO.md (seção 3). O banco é criado pelo migrator (A01), por isso este
   script não tem CREATE DATABASE nem USE.
   ============================================================================ */

-- ----------------------------------------------------------------------------
-- Lookups (mantidos do script original; A09 acrescenta lata e sache)
-- ----------------------------------------------------------------------------
CREATE TABLE papeis_usuario (
    codigo      VARCHAR(20)   NOT NULL,
    descricao   VARCHAR(100)  NULL,
    ativo       BIT           NOT NULL CONSTRAINT DF_papeis_ativo DEFAULT 1,
    CONSTRAINT PK_papeis_usuario PRIMARY KEY (codigo)
);

INSERT INTO papeis_usuario (codigo, descricao) VALUES
    ('admin',        'Acesso total ao sistema'),
    ('operacional',  'Registra entradas, saídas e cadastros do dia a dia'),
    ('visualizador', 'Apenas consulta relatórios, estoque e dashboards');

CREATE TABLE unidades_medida (
    codigo      VARCHAR(10)   NOT NULL,
    descricao   VARCHAR(50)   NULL,
    ativo       BIT           NOT NULL CONSTRAINT DF_um_ativo DEFAULT 1,
    CONSTRAINT PK_unidades_medida PRIMARY KEY (codigo)
);

INSERT INTO unidades_medida (codigo, descricao) VALUES
    ('unidade', 'Unidade avulsa'),
    ('kg',      'Quilograma'),
    ('litro',   'Litro'),
    ('pacote',  'Pacote'),
    ('caixa',   'Caixa'),
    ('lata',    'Lata'),
    ('sache',   'Sachê');

-- ----------------------------------------------------------------------------
-- Tenants e unidades (A02)
-- ----------------------------------------------------------------------------
CREATE TABLE tenants (
    id          INT IDENTITY(1,1) NOT NULL,
    nome        VARCHAR(255)  NOT NULL,
    cnpj        VARCHAR(20)   NULL,
    ativo       BIT           NOT NULL CONSTRAINT DF_tenants_ativo DEFAULT 1,
    criado_em   DATETIME2     NOT NULL CONSTRAINT DF_tenants_criado DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_tenants PRIMARY KEY (id)
);

CREATE TABLE unidades (
    id          INT IDENTITY(1,1) NOT NULL,
    tenant_id   INT           NOT NULL,
    nome        VARCHAR(100)  NOT NULL,
    tipo        VARCHAR(10)   NOT NULL,
    ativo       BIT           NOT NULL CONSTRAINT DF_unidades_ativo DEFAULT 1,
    criado_em   DATETIME2     NOT NULL CONSTRAINT DF_unidades_criado DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_unidades PRIMARY KEY (id),
    CONSTRAINT FK_unidades_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT CK_unidades_tipo CHECK (tipo IN ('matriz','filial')),
    CONSTRAINT UQ_unidades_tenant_nome UNIQUE (tenant_id, nome),
    CONSTRAINT UQ_unidades_id_tenant UNIQUE (id, tenant_id)
);

-- No máximo uma matriz por tenant
CREATE UNIQUE INDEX UX_unidades_uma_matriz ON unidades(tenant_id) WHERE tipo = 'matriz';

-- ----------------------------------------------------------------------------
-- Usuários (A02, A05, A12, A15)
-- ----------------------------------------------------------------------------
CREATE TABLE usuarios (
    id                       INT IDENTITY(1,1) NOT NULL,
    tenant_id                INT           NOT NULL,
    nome                     VARCHAR(255)  NOT NULL,
    email                    VARCHAR(255)  NOT NULL,
    senha                    VARCHAR(255)  NOT NULL,        -- hash PBKDF2 (PasswordHasher); nunca texto puro
    papel                    VARCHAR(20)   NOT NULL CONSTRAINT DF_usuarios_papel DEFAULT 'operacional',
    ativo                    BIT           NOT NULL CONSTRAINT DF_usuarios_ativo DEFAULT 1,
    trocar_senha             BIT           NOT NULL CONSTRAINT DF_usuarios_trocar DEFAULT 0,
    ultimo_login_em          DATETIME2     NULL,
    tentativas_falhas_login  INT           NOT NULL CONSTRAINT DF_usuarios_tentativas DEFAULT 0,
    bloqueado_ate            DATETIME2     NULL,
    criado_em                DATETIME2     NOT NULL CONSTRAINT DF_usuarios_criado DEFAULT SYSUTCDATETIME(),
    atualizado_em            DATETIME2     NULL,
    CONSTRAINT PK_usuarios PRIMARY KEY (id),
    CONSTRAINT UQ_usuarios_email UNIQUE (email),               -- global: é o login
    CONSTRAINT UQ_usuarios_id_tenant UNIQUE (id, tenant_id),
    CONSTRAINT FK_usuarios_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT FK_usuarios_papel FOREIGN KEY (papel) REFERENCES papeis_usuario(codigo),
    CONSTRAINT CK_usuarios_tentativas CHECK (tentativas_falhas_login >= 0)
);

CREATE TABLE usuarios_unidades (
    tenant_id   INT NOT NULL,
    usuario_id  INT NOT NULL,
    unidade_id  INT NOT NULL,
    CONSTRAINT PK_usuarios_unidades PRIMARY KEY (usuario_id, unidade_id),
    CONSTRAINT FK_uu_usuario FOREIGN KEY (usuario_id, tenant_id) REFERENCES usuarios(id, tenant_id),
    CONSTRAINT FK_uu_unidade FOREIGN KEY (unidade_id, tenant_id) REFERENCES unidades(id, tenant_id)
);
CREATE INDEX IX_uu_unidade ON usuarios_unidades(unidade_id);

-- ----------------------------------------------------------------------------
-- Catálogo: categorias e itens (A02, A03, A05, A08)
-- ----------------------------------------------------------------------------
CREATE TABLE categorias_itens (
    id          INT IDENTITY(1,1) NOT NULL,
    tenant_id   INT           NOT NULL,
    nome        VARCHAR(100)  NOT NULL,
    descricao   VARCHAR(MAX)  NULL,
    ativo       BIT           NOT NULL CONSTRAINT DF_cat_ativo DEFAULT 1,
    criado_em   DATETIME2     NOT NULL CONSTRAINT DF_cat_criado DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_categorias_itens PRIMARY KEY (id),
    CONSTRAINT FK_cat_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT UQ_cat_tenant_nome UNIQUE (tenant_id, nome),
    CONSTRAINT UQ_cat_id_tenant UNIQUE (id, tenant_id)
);

-- "itens" virou catálogo do tenant: saldo, mínimo e valor foram para estoque_unidade (A03)
CREATE TABLE itens (
    id              INT IDENTITY(1,1) NOT NULL,
    tenant_id       INT           NOT NULL,
    codigo_interno  VARCHAR(50)   NOT NULL,
    nome            VARCHAR(255)  NOT NULL,
    descricao       VARCHAR(MAX)  NULL,          -- "observação" do RF01
    categoria_id    INT           NOT NULL,      -- "tipo do item" do RF01
    unidade_medida  VARCHAR(10)   NOT NULL,
    preco_estimado  DECIMAL(10,2) NULL,
    origem_padrao   VARCHAR(10)   NULL,
    ativo           BIT           NOT NULL CONSTRAINT DF_itens_ativo DEFAULT 1,
    criado_em       DATETIME2     NOT NULL CONSTRAINT DF_itens_criado DEFAULT SYSUTCDATETIME(),
    atualizado_em   DATETIME2     NULL,
    CONSTRAINT PK_itens PRIMARY KEY (id),
    CONSTRAINT UQ_itens_tenant_codigo UNIQUE (tenant_id, codigo_interno),
    CONSTRAINT UQ_itens_id_tenant UNIQUE (id, tenant_id),
    CONSTRAINT FK_itens_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT FK_itens_categoria FOREIGN KEY (categoria_id, tenant_id) REFERENCES categorias_itens(id, tenant_id),
    CONSTRAINT FK_itens_unidade_medida FOREIGN KEY (unidade_medida) REFERENCES unidades_medida(codigo),
    CONSTRAINT CK_itens_preco CHECK (preco_estimado IS NULL OR preco_estimado >= 0),
    CONSTRAINT CK_itens_origem CHECK (origem_padrao IS NULL OR origem_padrao IN ('doacao','compra'))
);
CREATE INDEX IX_itens_categoria ON itens(categoria_id);

-- Saldo por unidade (A03). Esboço da seção 4 do plano.
CREATE TABLE estoque_unidade (
    tenant_id            INT           NOT NULL,
    unidade_id           INT           NOT NULL,
    item_id              INT           NOT NULL,
    quantidade_atual     INT           NOT NULL CONSTRAINT DF_est_qtd DEFAULT 0,
    quantidade_minima    INT           NOT NULL CONSTRAINT DF_est_min DEFAULT 0,
    valor_total_estoque  DECIMAL(12,2) NOT NULL CONSTRAINT DF_est_valor DEFAULT 0,
    -- RF12: persistida para filtrar alertas sem repetir a comparação
    abaixo_do_minimo     AS (CAST(CASE WHEN quantidade_atual <= quantidade_minima THEN 1 ELSE 0 END AS BIT)) PERSISTED,
    atualizado_em        DATETIME2     NULL,
    CONSTRAINT PK_estoque_unidade PRIMARY KEY (unidade_id, item_id),
    CONSTRAINT FK_est_unidade FOREIGN KEY (unidade_id, tenant_id) REFERENCES unidades(id, tenant_id),
    CONSTRAINT FK_est_item    FOREIGN KEY (item_id, tenant_id)    REFERENCES itens(id, tenant_id),
    CONSTRAINT CK_est_qtd   CHECK (quantidade_atual >= 0),
    CONSTRAINT CK_est_min   CHECK (quantidade_minima >= 0),
    CONSTRAINT CK_est_valor CHECK (valor_total_estoque >= 0)
);
CREATE INDEX IX_est_item ON estoque_unidade(item_id);
-- A17: filtered index não aceita coluna computada; a coluna persistida entra na chave
CREATE INDEX IX_est_alertas ON estoque_unidade(tenant_id, unidade_id, abaixo_do_minimo);

-- ----------------------------------------------------------------------------
-- Parceiros (A02, A04, A06)
-- ----------------------------------------------------------------------------
CREATE TABLE parceiros (
    id            INT IDENTITY(1,1) NOT NULL,
    tenant_id     INT           NOT NULL,
    nome          VARCHAR(255)  NOT NULL,
    tipo          VARCHAR(20)   NOT NULL,
    cpf_cnpj      VARCHAR(20)   NULL,
    email         VARCHAR(255)  NULL,
    telefone      VARCHAR(20)   NULL,
    endereco      VARCHAR(MAX)  NULL,
    ativo         BIT           NOT NULL CONSTRAINT DF_parceiros_ativo DEFAULT 1,
    criado_em     DATETIME2     NOT NULL CONSTRAINT DF_parceiros_criado DEFAULT SYSUTCDATETIME(),
    atualizado_em DATETIME2     NULL,
    CONSTRAINT PK_parceiros PRIMARY KEY (id),
    CONSTRAINT UQ_parceiros_id_tenant UNIQUE (id, tenant_id),
    CONSTRAINT FK_parceiros_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT CK_parceiros_tipo CHECK (tipo IN ('pessoa_fisica','empresa','organizacao'))
);
-- A06: o UNIQUE original aceitava só um NULL; o índice filtrado permite vários sem documento
CREATE UNIQUE INDEX UX_parceiros_tenant_doc ON parceiros(tenant_id, cpf_cnpj) WHERE cpf_cnpj IS NOT NULL;

-- ----------------------------------------------------------------------------
-- Movimentações (A02, A04, A05, A07, A12, A13, A17, A19)
-- ----------------------------------------------------------------------------
CREATE SEQUENCE seq_movimentacao AS BIGINT START WITH 1 INCREMENT BY 1;

CREATE TABLE movimentacoes (
    id                    INT IDENTITY(1,1) NOT NULL,
    tenant_id             INT           NOT NULL,
    unidade_id            INT           NOT NULL,
    numero_movimentacao   VARCHAR(50)   NOT NULL
        CONSTRAINT DF_mov_numero DEFAULT ('MOV-' + CONVERT(VARCHAR(4), YEAR(SYSUTCDATETIME())) + '-'
            + RIGHT('000000' + CONVERT(VARCHAR(20), NEXT VALUE FOR seq_movimentacao), 6)),
    tipo_movimentacao     VARCHAR(20)   NOT NULL,
    item_id               INT           NOT NULL,
    quantidade            INT           NOT NULL,
    valor_unitario        DECIMAL(10,2) NOT NULL,
    valor_total           AS (CAST(quantidade * valor_unitario AS DECIMAL(12,2))) PERSISTED,
    data_movimentacao     DATE          NOT NULL,
    parceiro_id           INT           NULL,
    descricao_saida       VARCHAR(255)  NULL,
    beneficiario          VARCHAR(255)  NULL,
    responsavel_id        INT           NOT NULL,
    observacoes           VARCHAR(MAX)  NULL,
    status                VARCHAR(20)   NOT NULL CONSTRAINT DF_mov_status DEFAULT 'pendente',
    criado_em             DATETIME2     NOT NULL CONSTRAINT DF_mov_criado DEFAULT SYSUTCDATETIME(),
    atualizado_em         DATETIME2     NULL,

    CONSTRAINT PK_movimentacoes PRIMARY KEY (id),   -- clustered: comprimido pelo WITH da tabela
    CONSTRAINT UQ_mov_tenant_numero UNIQUE (tenant_id, numero_movimentacao) WITH (DATA_COMPRESSION = PAGE),

    -- A04: FKs compostas impedem referência cruzada entre tenants
    CONSTRAINT FK_mov_tenant   FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT FK_mov_unidade  FOREIGN KEY (unidade_id, tenant_id)     REFERENCES unidades(id, tenant_id),
    CONSTRAINT FK_mov_item     FOREIGN KEY (item_id, tenant_id)        REFERENCES itens(id, tenant_id),
    CONSTRAINT FK_mov_parceiro FOREIGN KEY (parceiro_id, tenant_id)    REFERENCES parceiros(id, tenant_id),
    CONSTRAINT FK_mov_resp     FOREIGN KEY (responsavel_id, tenant_id) REFERENCES usuarios(id, tenant_id),

    CONSTRAINT CK_mov_tipo CHECK (
        tipo_movimentacao IN ('doacao_recebida','compra','doacao_realizada','consumo_interno','avaria')
    ),
    CONSTRAINT CK_mov_status CHECK (status IN ('pendente','processada','cancelada')),
    CONSTRAINT CK_mov_quantidade CHECK (quantidade > 0),
    CONSTRAINT CK_mov_valor_unitario CHECK (valor_unitario >= 0),
    -- RF02 + RF06: compra exige fornecedor; doação recebida pode ser anônima; saídas não têm fornecedor
    CONSTRAINT CK_mov_parceiro_coerente CHECK (
        (tipo_movimentacao = 'compra' AND parceiro_id IS NOT NULL)
        OR (tipo_movimentacao = 'doacao_recebida')
        OR (tipo_movimentacao IN ('doacao_realizada','consumo_interno','avaria') AND parceiro_id IS NULL)
    ),
    -- RF03: doação realizada precisa identificar o beneficiário
    CONSTRAINT CK_mov_beneficiario_coerente CHECK (
        tipo_movimentacao <> 'doacao_realizada'
        OR (beneficiario IS NOT NULL AND LEN(beneficiario) > 0)
    )
) WITH (DATA_COMPRESSION = PAGE);

CREATE INDEX IX_mov_item      ON movimentacoes(item_id)        WITH (DATA_COMPRESSION = PAGE);
CREATE INDEX IX_mov_parceiro  ON movimentacoes(parceiro_id)    WITH (DATA_COMPRESSION = PAGE);
CREATE INDEX IX_mov_resp      ON movimentacoes(responsavel_id) WITH (DATA_COMPRESSION = PAGE);
CREATE INDEX IX_mov_unidade_data ON movimentacoes(tenant_id, unidade_id, data_movimentacao DESC)
    INCLUDE (tipo_movimentacao, item_id, quantidade, status, beneficiario)
    WITH (DATA_COMPRESSION = PAGE);

-- ----------------------------------------------------------------------------
-- Auditoria (A14, A16, A17, A19)
-- ----------------------------------------------------------------------------
CREATE TABLE logs_auditoria (
    id              BIGINT IDENTITY(1,1) NOT NULL,
    tenant_id       INT           NULL,
    unidade_id      INT           NULL,
    usuario_id      INT           NULL,
    tabela_afetada  VARCHAR(100)  NOT NULL,
    acao            VARCHAR(30)   NOT NULL,
    registro_id     INT           NULL,
    dados_antigos   NVARCHAR(MAX) NULL,
    dados_novos     NVARCHAR(MAX) NULL,
    ip_usuario      VARCHAR(45)   NULL,
    criado_em       DATETIME2     NOT NULL CONSTRAINT DF_log_criado DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_logs_auditoria PRIMARY KEY (id),  -- clustered: comprimido pelo WITH da tabela
    CONSTRAINT FK_log_tenant  FOREIGN KEY (tenant_id)  REFERENCES tenants(id),
    CONSTRAINT FK_log_unidade FOREIGN KEY (unidade_id) REFERENCES unidades(id),
    CONSTRAINT FK_log_usuario FOREIGN KEY (usuario_id) REFERENCES usuarios(id),
    CONSTRAINT CK_log_acao CHECK (acao IN (
        'INSERT','UPDATE','DELETE',
        'LOGIN','LOGIN_FALHA','LOGOUT','ALTERNAR_UNIDADE','EXPORTAR_RELATORIO','ALTERAR_SENHA','PURGA_AUDITORIA'
    )),
    CONSTRAINT CK_log_dados_antigos_json CHECK (dados_antigos IS NULL OR ISJSON(dados_antigos) = 1),
    CONSTRAINT CK_log_dados_novos_json   CHECK (dados_novos   IS NULL OR ISJSON(dados_novos)   = 1)
) WITH (DATA_COMPRESSION = PAGE);

CREATE INDEX IX_log_usuario         ON logs_auditoria(usuario_id) WITH (DATA_COMPRESSION = PAGE);
CREATE INDEX IX_log_tabela_registro ON logs_auditoria(tabela_afetada, registro_id) WITH (DATA_COMPRESSION = PAGE);
CREATE INDEX IX_log_tenant_data     ON logs_auditoria(tenant_id, criado_em DESC) WITH (DATA_COMPRESSION = PAGE);
