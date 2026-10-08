/* ============================================================================
   V003 — Manutenção e monitoramento (A20). Os logins estoque_app e
   estoque_maint (A18, A21) e suas permissões são criados pelo migrator,
   porque as senhas vêm do ambiente e não podem ficar em arquivo versionado.
   ============================================================================ */

CREATE TABLE manutencao_log (
    id               BIGINT IDENTITY(1,1) NOT NULL,
    tarefa           VARCHAR(50)   NOT NULL,
    inicio           DATETIME2     NOT NULL CONSTRAINT DF_manut_inicio DEFAULT SYSUTCDATETIME(),
    fim              DATETIME2     NULL,
    sucesso          BIT           NULL,
    linhas_afetadas  BIGINT        NULL,
    detalhes         NVARCHAR(MAX) NULL,
    CONSTRAINT PK_manutencao_log PRIMARY KEY (id),
    CONSTRAINT CK_manut_detalhes_json CHECK (detalhes IS NULL OR ISJSON(detalhes) = 1)
);
CREATE INDEX IX_manut_tarefa_inicio ON manutencao_log(tarefa, inicio DESC);

CREATE TABLE saude_banco_historico (
    id                   BIGINT IDENTITY(1,1) NOT NULL,
    data                 DATETIME2     NOT NULL CONSTRAINT DF_saude_data DEFAULT SYSUTCDATETIME(),
    tamanho_total_mb     DECIMAL(12,2) NOT NULL,
    tamanho_logs_aud_mb  DECIMAL(12,2) NOT NULL,
    CONSTRAINT PK_saude_banco_historico PRIMARY KEY (id)
);

-- Purga em lotes por data (E8.1)
CREATE INDEX IX_log_criado_em ON logs_auditoria(criado_em) WITH (DATA_COMPRESSION = PAGE);
GO

-- Tamanho por arquivo do banco (dados e log), em MB
CREATE VIEW vw_saude_banco AS
SELECT
    f.file_id,
    f.name                                         AS arquivo,
    f.type_desc                                    AS tipo,
    CAST(f.size * 8.0 / 1024 AS DECIMAL(12,2))     AS tamanho_mb,
    CAST(FILEPROPERTY(f.name, 'SpaceUsed') * 8.0 / 1024 AS DECIMAL(12,2)) AS usado_mb,
    CASE WHEN f.max_size = -1 THEN NULL
         ELSE CAST(f.max_size * 8.0 / 1024 AS DECIMAL(12,2)) END AS maximo_mb
FROM sys.database_files f;
GO

-- Linhas e MB por tabela (reservado), via sys.dm_db_partition_stats
CREATE VIEW vw_tamanho_tabelas AS
SELECT
    s.name + '.' + t.name                                              AS tabela,
    SUM(CASE WHEN ps.index_id IN (0,1) THEN ps.row_count ELSE 0 END)   AS linhas,
    CAST(SUM(ps.reserved_page_count) * 8.0 / 1024 AS DECIMAL(12,2))    AS reservado_mb,
    CAST(SUM(ps.used_page_count) * 8.0 / 1024 AS DECIMAL(12,2))        AS usado_mb
FROM sys.dm_db_partition_stats ps
JOIN sys.tables  t ON t.object_id = ps.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
GROUP BY s.name, t.name;
GO

-- Espaço livre do volume onde estão os arquivos do banco.
-- Exige VIEW SERVER PERFORMANCE STATE; por isso fica numa view separada.
CREATE VIEW vw_espaco_volume AS
SELECT DISTINCT
    vs.volume_mount_point                                       AS ponto_montagem,
    CAST(vs.total_bytes / 1048576.0 AS DECIMAL(14,2))           AS total_mb,
    CAST(vs.available_bytes / 1048576.0 AS DECIMAL(14,2))       AS livre_mb,
    CAST(100.0 * vs.available_bytes / NULLIF(vs.total_bytes,0) AS DECIMAL(5,2)) AS livre_pct
FROM sys.database_files f
CROSS APPLY sys.dm_os_volume_stats(DB_ID(), f.file_id) vs;
GO
