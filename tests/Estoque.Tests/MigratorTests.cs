using Estoque.Web.Data.Migrator;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Estoque.Tests;

/// <summary>T1.3 — idempotência e proteção contra edição de migração aplicada.</summary>
public class MigratorTests
{
    [Fact]
    public void Divide_lotes_pela_linha_GO()
    {
        var lotes = DatabaseMigrator.DividirEmLotes("SELECT 1\nGO\n  go  \nSELECT 2 -- GO no meio não divide\nGO -- comentário\n").ToList();
        Assert.Equal(["SELECT 1", "SELECT 2 -- GO no meio não divide"], lotes);
    }

    [Fact]
    public void Checksum_ignora_diferenca_de_fim_de_linha_e_BOM()
    {
        var a = DatabaseMigrator.Checksum(DatabaseMigrator.Normalizar("﻿SELECT 1\r\nGO\r\n"));
        var b = DatabaseMigrator.Checksum(DatabaseMigrator.Normalizar("SELECT 1\nGO\n"));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Segunda_execucao_nao_altera_nada_e_migracao_editada_falha()
    {
        var fixture = new BancoFixture();
        var pasta = Path.Combine(Path.GetTempPath(), "estoque-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pasta);
        foreach (var f in Directory.GetFiles(BancoFixture.PastaMigracoes, "V*.sql"))
            File.Copy(f, Path.Combine(pasta, Path.GetFileName(f)));

        var opt = new MigratorOptions { AdminConnectionString = fixture.AdminCs, DatabaseName = fixture.NomeBanco, MigrationsPath = pasta };
        try
        {
            await new DatabaseMigrator(opt, NullLogger.Instance).ExecutarAsync();
            var antes = await Contar(fixture);
            await new DatabaseMigrator(opt, NullLogger.Instance).ExecutarAsync();
            Assert.Equal(antes, await Contar(fixture));

            await File.AppendAllTextAsync(Path.Combine(pasta, "V001__schema.sql"), "\n-- edição indevida\n");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new DatabaseMigrator(opt, NullLogger.Instance).ExecutarAsync());
            Assert.Contains("checksum", ex.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
            Directory.Delete(pasta, recursive: true);
        }
    }

    private static async Task<(int Migracoes, int Objetos)> Contar(BancoFixture f)
    {
        await using var c = await f.AbrirAsync();
        await using var cmd = new SqlCommand(
            "SELECT (SELECT COUNT(*) FROM schema_migrations), (SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0)", c);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return (rd.GetInt32(0), rd.GetInt32(1));
    }
}
