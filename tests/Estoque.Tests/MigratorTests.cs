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

    /// <summary>
    /// V004 sobre um banco que já tinha dados da época da V001–V003: o CPF some da tabela E dos logs
    /// de auditoria, o CNPJ é preservado e cada tenant ganha a sua cópia das unidades de medida.
    /// </summary>
    [Fact]
    public async Task V004_remove_CPF_antigo_inclusive_da_auditoria_e_replica_unidades_por_tenant()
    {
        var fixture = new BancoFixture();
        var pasta = Path.Combine(Path.GetTempPath(), "estoque-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pasta);
        foreach (var f in DatabaseMigrator.ListarMigracoes(BancoFixture.PastaMigracoes).Where(m => m.Versao <= 3))
            File.Copy(f.Caminho, Path.Combine(pasta, f.Nome));
        Assert.Equal(3, Directory.GetFiles(pasta).Length);   // garante que o cenário parte do schema antigo
        var opt = new MigratorOptions { AdminConnectionString = fixture.AdminCs, DatabaseName = fixture.NomeBanco, MigrationsPath = pasta };
        const string cpf = "12345678901", cnpj = "11222333000181";

        try
        {
            await new DatabaseMigrator(opt, NullLogger.Instance).ExecutarAsync();   // schema antigo (cpf_cnpj)
            await using (var c = await fixture.AbrirAsync())
            {
                await Sql.Exec(c, "INSERT INTO tenants (nome) VALUES ('A'), ('B')");
                await Sql.Exec(c, "INSERT INTO categorias_itens (tenant_id, nome) VALUES (1, 'Alimento')");
                await Sql.Exec(c, "INSERT INTO itens (tenant_id, codigo_interno, nome, categoria_id, unidade_medida) VALUES (1, 'A1', 'Arroz', 1, 'pacote')");
                await Sql.Exec(c, "INSERT INTO parceiros (tenant_id, nome, tipo, cpf_cnpj) VALUES (1, 'Pessoa', 'pessoa_fisica', @d)", ("@d", cpf));
                await Sql.Exec(c, "INSERT INTO parceiros (tenant_id, nome, tipo, cpf_cnpj) VALUES (1, 'Empresa', 'empresa', @d)", ("@d", cnpj));
                await Sql.Exec(c, "UPDATE parceiros SET telefone = '1' WHERE nome = 'Pessoa'"); // gera log UPDATE com dados_antigos
                Assert.True((int)(await Sql.Escalar(c, "SELECT COUNT(*) FROM logs_auditoria WHERE dados_novos LIKE '%' + @d + '%' OR dados_antigos LIKE '%' + @d + '%'", ("@d", cpf)))! > 0);
            }

            File.Copy(Path.Combine(BancoFixture.PastaMigracoes, "V004__cnpj_e_unidades_por_tenant.sql"),
                Path.Combine(pasta, "V004__cnpj_e_unidades_por_tenant.sql"));
            await new DatabaseMigrator(opt, NullLogger.Instance).ExecutarAsync();

            await using var c2 = await fixture.AbrirAsync();
            Assert.IsType<DBNull>(await Sql.Escalar(c2, "SELECT cnpj FROM parceiros WHERE nome = 'Pessoa'"));
            Assert.Equal(cnpj, await Sql.Escalar(c2, "SELECT cnpj FROM parceiros WHERE nome = 'Empresa'"));
            Assert.Equal(0, await Sql.Escalar(c2, "SELECT COUNT(*) FROM logs_auditoria WHERE dados_novos LIKE '%' + @d + '%' OR dados_antigos LIKE '%' + @d + '%'", ("@d", cpf)));
            Assert.Equal(14, await Sql.Escalar(c2, "SELECT COUNT(*) FROM unidades_medida")); // 7 padrões × 2 tenants
            Assert.Equal("pacote", await Sql.Escalar(c2, "SELECT unidade_medida FROM itens WHERE codigo_interno = 'A1'"));
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
