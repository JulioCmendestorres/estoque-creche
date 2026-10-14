using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Estoque.Web.Data.Migrator;

public sealed record MigratorOptions
{
    /// <summary>Conexão administrativa (sa ou Windows) apontando para master.</summary>
    public required string AdminConnectionString { get; init; }
    public required string DatabaseName { get; init; }
    public required string MigrationsPath { get; init; }
    public string? AppDbPassword { get; init; }
    public string? MaintDbPassword { get; init; }
    public TimeSpan TempoMaximoEspera { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Migrator próprio (T1.3): cria o banco, aplica db/migrations/V###__*.sql em ordem,
/// uma transação por arquivo, registra checksum em schema_migrations e falha se uma
/// migração já aplicada foi alterada. Depois cria/atualiza os logins de menor privilégio.
/// </summary>
public sealed partial class DatabaseMigrator(MigratorOptions opt, ILogger logger)
{
    public const string Collation = "Latin1_General_100_CI_AI";
    public const string LoginApp = "estoque_app";
    public const string LoginMaint = "estoque_maint";

    [GeneratedRegex(@"^\s*GO\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LinhaGo();

    [GeneratedRegex(@"^V(\d+)__.+\.sql$", RegexOptions.IgnoreCase)]
    private static partial Regex NomeMigracao();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,99}$")]
    private static partial Regex NomeBancoValido();

    public string DbConnectionString => new SqlConnectionStringBuilder(opt.AdminConnectionString)
    {
        InitialCatalog = opt.DatabaseName
    }.ConnectionString;

    public async Task ExecutarAsync(CancellationToken ct = default)
    {
        if (!NomeBancoValido().IsMatch(opt.DatabaseName))
            throw new InvalidOperationException($"Nome de banco inválido: '{opt.DatabaseName}'.");

        await AguardarServidorAsync(ct);
        await CriarBancoSeNecessarioAsync(ct);

        await using var conn = new SqlConnection(DbConnectionString);
        await conn.OpenAsync(ct);

        // Evita duas instâncias do migrator rodando ao mesmo tempo
        await ExecAsync(conn, null, """
            DECLARE @r INT;
            EXEC @r = sp_getapplock @Resource = N'estoque_migrator', @LockMode = N'Exclusive',
                                    @LockOwner = N'Session', @LockTimeout = 60000;
            IF @r < 0 THROW 50100, N'Não foi possível obter o lock do migrator.', 1;
            """, ct);
        try
        {
            await GarantirTabelaControleAsync(conn, ct);
            await AplicarMigracoesAsync(conn, ct);
            await ConfigurarSegurancaAsync(conn, ct);
        }
        finally
        {
            await ExecAsync(conn, null,
                "EXEC sp_releaseapplock @Resource = N'estoque_migrator', @LockOwner = N'Session';", ct);
        }
    }

    private async Task AguardarServidorAsync(CancellationToken ct)
    {
        var limite = DateTime.UtcNow + opt.TempoMaximoEspera;
        var espera = TimeSpan.FromSeconds(1);
        var tentativa = 0;
        while (true)
        {
            tentativa++;
            try
            {
                await using var conn = new SqlConnection(opt.AdminConnectionString);
                await conn.OpenAsync(ct);
                await ExecAsync(conn, null, "SELECT 1;", ct);
                return;
            }
            catch (SqlException ex) when (DateTime.UtcNow < limite)
            {
                logger.LogInformation("SQL Server indisponível (tentativa {Tentativa}): {Erro}. Nova tentativa em {Espera}s.",
                    tentativa, ex.Message, espera.TotalSeconds);
                await Task.Delay(espera, ct);
                espera = TimeSpan.FromSeconds(Math.Min(espera.TotalSeconds * 2, 10));
            }
        }
    }

    private async Task CriarBancoSeNecessarioAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(opt.AdminConnectionString);
        await conn.OpenAsync(ct);
        var nome = opt.DatabaseName; // validado por regex; QUOTENAME no servidor
        await ExecAsync(conn, null, $"""
            IF DB_ID(@nome) IS NULL
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@nome) + N' COLLATE {Collation}';
                EXEC (@sql);
                SET @sql = N'ALTER DATABASE ' + QUOTENAME(@nome) + N' SET READ_COMMITTED_SNAPSHOT ON';
                EXEC (@sql);
                SET @sql = N'ALTER DATABASE ' + QUOTENAME(@nome) + N' SET RECURSIVE_TRIGGERS OFF';
                EXEC (@sql);
                PRINT 'criado';
            END
            """, ct, ("@nome", nome));
        logger.LogInformation("Banco {Banco} disponível.", nome);
    }

    private static Task GarantirTabelaControleAsync(SqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn, null, """
            IF OBJECT_ID(N'dbo.schema_migrations', N'U') IS NULL
            CREATE TABLE dbo.schema_migrations (
                versao           INT           NOT NULL CONSTRAINT PK_schema_migrations PRIMARY KEY,
                nome             VARCHAR(255)  NOT NULL,
                checksum_sha256  CHAR(64)      NOT NULL,
                aplicado_em      DATETIME2     NOT NULL CONSTRAINT DF_schema_migrations_aplicado DEFAULT SYSUTCDATETIME()
            );
            """, ct);

    public sealed record Migracao(int Versao, string Nome, string Caminho);

    public static IReadOnlyList<Migracao> ListarMigracoes(string pasta)
    {
        if (!Directory.Exists(pasta))
            throw new DirectoryNotFoundException($"Pasta de migrações não encontrada: {pasta}");
        var lista = new List<Migracao>();
        foreach (var arq in Directory.GetFiles(pasta, "*.sql"))
        {
            var nome = Path.GetFileName(arq);
            var m = NomeMigracao().Match(nome);
            if (!m.Success) continue;
            lista.Add(new Migracao(int.Parse(m.Groups[1].Value), nome, arq));
        }
        var duplicada = lista.GroupBy(x => x.Versao).FirstOrDefault(g => g.Count() > 1);
        if (duplicada is not null)
            throw new InvalidOperationException($"Versão de migração duplicada: {duplicada.Key}");
        return lista.OrderBy(x => x.Versao).ToList();
    }

    /// <summary>Normaliza fim de linha e BOM para o checksum não mudar entre Windows e Linux.</summary>
    public static string Normalizar(string conteudo) =>
        conteudo.TrimStart('﻿').Replace("\r\n", "\n");

    public static string Checksum(string conteudoNormalizado) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(conteudoNormalizado)));

    public static IEnumerable<string> DividirEmLotes(string sql) =>
        LinhaGo().Split(sql).Select(b => b.Trim()).Where(b => b.Length > 0);

    private async Task AplicarMigracoesAsync(SqlConnection conn, CancellationToken ct)
    {
        var aplicadas = new Dictionary<int, string>();
        await using (var cmd = new SqlCommand("SELECT versao, checksum_sha256 FROM dbo.schema_migrations;", conn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                aplicadas[rd.GetInt32(0)] = rd.GetString(1);

        var novas = 0;
        foreach (var mig in ListarMigracoes(opt.MigrationsPath))
        {
            var conteudo = Normalizar(await File.ReadAllTextAsync(mig.Caminho, Encoding.UTF8, ct));
            var hash = Checksum(conteudo);

            if (aplicadas.TryGetValue(mig.Versao, out var hashAplicado))
            {
                if (!string.Equals(hashAplicado, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"A migração {mig.Nome} já foi aplicada e foi alterada depois (checksum diferente). " +
                        "Nunca edite migração aplicada: crie uma nova V###.");
                continue;
            }

            logger.LogInformation("Aplicando {Migracao}...", mig.Nome);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                foreach (var lote in DividirEmLotes(conteudo))
                    await ExecAsync(conn, tx, lote, ct);
                await ExecAsync(conn, tx,
                    "INSERT INTO dbo.schema_migrations (versao, nome, checksum_sha256) VALUES (@v, @n, @c);", ct,
                    ("@v", mig.Versao), ("@n", mig.Nome), ("@c", hash));
                await tx.CommitAsync(ct);
                novas++;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        logger.LogInformation("Migrações: {Novas} aplicada(s) agora, {Total} no total.", novas, aplicadas.Count + novas);
    }

    /// <summary>
    /// A18/A21: logins de menor privilégio. A senha vai como parâmetro e é escapada
    /// no servidor (REPLACE de aspas) — nada de concatenação no lado da aplicação.
    /// </summary>
    private async Task ConfigurarSegurancaAsync(SqlConnection conn, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(opt.AppDbPassword))
        {
            await GarantirLoginAsync(LoginApp, opt.AppDbPassword, ct);
            await ExecAsync(conn, null, """
                IF USER_ID(N'estoque_app') IS NULL CREATE USER estoque_app FOR LOGIN estoque_app;
                ALTER ROLE db_datareader ADD MEMBER estoque_app;
                ALTER ROLE db_datawriter ADD MEMBER estoque_app;
                GRANT UPDATE ON OBJECT::dbo.seq_movimentacao TO estoque_app;
                DENY DELETE ON dbo.movimentacoes   TO estoque_app;
                DENY DELETE ON dbo.estoque_unidade TO estoque_app;
                DENY DELETE, UPDATE ON dbo.logs_auditoria TO estoque_app;
                DENY INSERT, UPDATE, DELETE ON dbo.schema_migrations     TO estoque_app;
                DENY INSERT, UPDATE, DELETE ON dbo.manutencao_log        TO estoque_app;
                DENY INSERT, UPDATE, DELETE ON dbo.saude_banco_historico TO estoque_app;
                DENY INSERT, UPDATE, DELETE ON dbo.tenants               TO estoque_app;
                """, ct);
            logger.LogInformation("Login {Login} configurado.", LoginApp);
        }
        else
        {
            logger.LogWarning("APP_DB_PASSWORD não definido: login {Login} não foi configurado.", LoginApp);
        }

        if (!string.IsNullOrEmpty(opt.MaintDbPassword))
        {
            await GarantirLoginAsync(LoginMaint, opt.MaintDbPassword, ct);
            await ExecAsync(conn, null, """
                IF USER_ID(N'estoque_maint') IS NULL CREATE USER estoque_maint FOR LOGIN estoque_maint;
                GRANT SELECT, INSERT, DELETE ON dbo.logs_auditoria     TO estoque_maint;
                GRANT SELECT, INSERT, UPDATE ON dbo.manutencao_log     TO estoque_maint;
                GRANT SELECT, INSERT ON dbo.saude_banco_historico      TO estoque_maint;
                GRANT SELECT ON dbo.vw_saude_banco                     TO estoque_maint;
                GRANT SELECT ON dbo.vw_tamanho_tabelas                 TO estoque_maint;
                GRANT SELECT ON dbo.vw_espaco_volume                   TO estoque_maint;
                GRANT VIEW DATABASE STATE                              TO estoque_maint;
                """, ct);
            try
            {
                await using var master = new SqlConnection(opt.AdminConnectionString);
                await master.OpenAsync(ct);
                await ExecAsync(master, null, "GRANT VIEW SERVER PERFORMANCE STATE TO estoque_maint;", ct);
            }
            catch (SqlException ex)
            {
                logger.LogWarning("Não foi possível conceder VIEW SERVER PERFORMANCE STATE: {Erro}", ex.Message);
            }
            logger.LogInformation("Login {Login} configurado.", LoginMaint);
        }
        else
        {
            logger.LogWarning("MAINT_DB_PASSWORD não definido: login {Login} não foi configurado.", LoginMaint);
        }
    }

    private async Task GarantirLoginAsync(string login, string senha, CancellationToken ct)
    {
        await using var master = new SqlConnection(opt.AdminConnectionString);
        await master.OpenAsync(ct);
        await ExecAsync(master, null, """
            DECLARE @sql NVARCHAR(MAX);
            DECLARE @senhaEsc NVARCHAR(MAX) = N'N''' + REPLACE(@senha, N'''', N'''''') + N'''';
            IF SUSER_ID(@login) IS NULL
                SET @sql = N'CREATE LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + @senhaEsc
                         + N', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = ' + QUOTENAME(@banco);
            ELSE
                SET @sql = N'ALTER LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + @senhaEsc
                         + N'; ALTER LOGIN ' + QUOTENAME(@login) + N' ENABLE';
            EXEC (@sql);
            """, ct, ("@login", login), ("@senha", senha), ("@banco", opt.DatabaseName));
    }

    internal static async Task ExecAsync(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct,
        params (string Nome, object Valor)[] parametros)
    {
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 300 };
        foreach (var (nome, valor) in parametros)
            cmd.Parameters.Add(new SqlParameter(nome, valor is string ? SqlDbType.NVarChar : SqlDbType.Int)
            {
                Value = valor,
                Size = valor is string ? -1 : 0
            });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Procura db/migrations a partir do binário e subindo pelas pastas (dev e container).</summary>
    public static string LocalizarPastaMigracoes(string? configurada)
    {
        if (!string.IsNullOrWhiteSpace(configurada)) return Path.GetFullPath(configurada);
        foreach (var inicio in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(inicio);
            while (dir is not null)
            {
                var candidato = Path.Combine(dir.FullName, "db", "migrations");
                if (Directory.Exists(candidato)) return candidato;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Pasta db/migrations não encontrada. Defina Migrator:Path.");
    }
}
