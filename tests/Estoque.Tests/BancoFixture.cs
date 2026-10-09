using Estoque.Web.Data.Migrator;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Estoque.Tests;

/// <summary>
/// Cria um banco descartável (EstoqueTeste_xxxxxxxx) no SQL Server indicado por
/// ESTOQUE_TEST_ADMIN_CS (padrão: localhost com autenticação Windows), aplica as
/// migrações reais e remove o banco ao final. Os logins estoque_app/estoque_maint
/// NÃO são criados aqui: são de servidor e alterá-los afetaria o ambiente de dev.
/// </summary>
public sealed class BancoFixture : IAsyncLifetime
{
    public string AdminCs { get; } = Environment.GetEnvironmentVariable("ESTOQUE_TEST_ADMIN_CS")
        ?? "Server=localhost;Database=master;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True";

    public string NomeBanco { get; } = $"EstoqueTeste_{Guid.NewGuid():N}"[..21];
    public string Cs => new SqlConnectionStringBuilder(AdminCs) { InitialCatalog = NomeBanco }.ConnectionString;
    public static string PastaMigracoes => DatabaseMigrator.LocalizarPastaMigracoes(null);

    public async Task InitializeAsync()
    {
        await new DatabaseMigrator(new MigratorOptions
        {
            AdminConnectionString = AdminCs,
            DatabaseName = NomeBanco,
            MigrationsPath = PastaMigracoes,
        }, NullLogger.Instance).ExecutarAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"""
            ALTER DATABASE [{NomeBanco}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            DROP DATABASE [{NomeBanco}];
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<SqlConnection> AbrirAsync()
    {
        var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        return conn;
    }
}

[CollectionDefinition(Nome)]
public sealed class ColecaoBanco : ICollectionFixture<BancoFixture>
{
    public const string Nome = "banco";
}

/// <summary>Helpers de SQL para montar cenários pequenos e independentes por teste.</summary>
public static class Sql
{
    public static async Task<object?> Escalar(SqlConnection c, string sql, params (string, object?)[] ps)
    {
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }

    public static async Task<int> Id(SqlConnection c, string sqlInsert, params (string, object?)[] ps) =>
        Convert.ToInt32(await Escalar(c, sqlInsert + "; SELECT CAST(SCOPE_IDENTITY() AS INT);", ps));

    public static async Task Exec(SqlConnection c, string sql, params (string, object?)[] ps) => await Escalar(c, sql, ps);

    public sealed record Cenario(int Tenant, int Matriz, int Filial, int Usuario, int Categoria, int Item);

    /// <summary>Tenant novo com matriz, filial, usuário, categoria e um item.</summary>
    public static async Task<Cenario> NovoTenant(SqlConnection c)
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var t = await Id(c, "INSERT INTO tenants (nome) VALUES (@n)", ("@n", "T " + sufixo));
        var m = await Id(c, "INSERT INTO unidades (tenant_id, nome, tipo) VALUES (@t, 'Matriz', 'matriz')", ("@t", t));
        var f = await Id(c, "INSERT INTO unidades (tenant_id, nome, tipo) VALUES (@t, 'Filial', 'filial')", ("@t", t));
        var u = await Id(c, "INSERT INTO usuarios (tenant_id, nome, email, senha, papel) VALUES (@t, 'U', @e, 'AQAAAAhashFicticio', 'admin')",
            ("@t", t), ("@e", $"u{sufixo}@teste.local"));
        await Exec(c, "INSERT INTO usuarios_unidades (tenant_id, usuario_id, unidade_id) VALUES (@t, @u, @m), (@t, @u, @f)",
            ("@t", t), ("@u", u), ("@m", m), ("@f", f));
        await Exec(c, "INSERT INTO unidades_medida (tenant_id, codigo, descricao) VALUES (@t, 'pacote', 'Pacote'), (@t, 'kg', 'Quilograma')",
            ("@t", t));
        var cat = await Id(c, "INSERT INTO categorias_itens (tenant_id, nome) VALUES (@t, 'Alimento')", ("@t", t));
        var item = await Id(c, "INSERT INTO itens (tenant_id, codigo_interno, nome, categoria_id, unidade_medida) VALUES (@t, 'A1', 'Arroz', @c, 'pacote')",
            ("@t", t), ("@c", cat));
        return new Cenario(t, m, f, u, cat, item);
    }

    public static Task<int> Mov(SqlConnection c, Cenario s, string tipo, int qtd, decimal valor,
        int? unidade = null, int? parceiro = null, string? beneficiario = null, string status = "processada", int? item = null) =>
        Id(c, """
            INSERT INTO movimentacoes (tenant_id, unidade_id, tipo_movimentacao, item_id, quantidade, valor_unitario,
                                       data_movimentacao, parceiro_id, beneficiario, responsavel_id, status)
            VALUES (@t, @u, @tipo, @i, @q, @v, '2026-10-01', @p, @b, @r, @s)
            """, ("@t", s.Tenant), ("@u", unidade ?? s.Matriz), ("@tipo", tipo), ("@i", item ?? s.Item), ("@q", qtd),
            ("@v", valor), ("@p", parceiro), ("@b", beneficiario), ("@r", s.Usuario), ("@s", status));

    public static async Task<(int Qtd, decimal Valor)> Saldo(SqlConnection c, int unidade, int item)
    {
        await using var cmd = new SqlCommand(
            "SELECT quantidade_atual, valor_total_estoque FROM estoque_unidade WHERE unidade_id = @u AND item_id = @i", c);
        cmd.Parameters.AddWithValue("@u", unidade);
        cmd.Parameters.AddWithValue("@i", item);
        await using var rd = await cmd.ExecuteReaderAsync();
        return await rd.ReadAsync() ? (rd.GetInt32(0), rd.GetDecimal(1)) : (0, 0m);
    }
}
