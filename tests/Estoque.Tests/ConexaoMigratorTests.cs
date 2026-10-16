using Estoque.Web.Data.Migrator;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Estoque.Tests;

/// <summary>Não precisam de banco: só conferem a string de conexão que o migrator monta.</summary>
public class ConexaoMigratorTests
{
    private static int TempoDeConexao(string conexaoAdmin)
    {
        var opt = new MigratorOptions { AdminConnectionString = conexaoAdmin, DatabaseName = "EstoqueDoacoes", MigrationsPath = "." };
        return new SqlConnectionStringBuilder(new DatabaseMigrator(opt, NullLogger.Instance).DbConnectionString).ConnectTimeout;
    }

    [Fact]
    public void Conexao_ao_banco_espera_pelo_menos_60_segundos_mesmo_com_o_padrao_do_driver()
        => Assert.Equal(DatabaseMigrator.TempoConexaoBancoSegundos,
            TempoDeConexao("Server=sqlserver,1433;Database=master;User Id=sa;Password=x"));

    [Fact]
    public void Tempo_maior_configurado_pelo_operador_e_respeitado()
        => Assert.Equal(120, TempoDeConexao("Server=sqlserver,1433;Database=master;User Id=sa;Password=x;Connect Timeout=120"));

    [Fact]
    public void Conexao_ao_banco_aponta_para_o_banco_do_sistema()
    {
        var opt = new MigratorOptions { AdminConnectionString = "Server=s;Database=master;User Id=sa;Password=x", DatabaseName = "EstoqueDoacoes", MigrationsPath = "." };
        var b = new SqlConnectionStringBuilder(new DatabaseMigrator(opt, NullLogger.Instance).DbConnectionString);
        Assert.Equal("EstoqueDoacoes", b.InitialCatalog);
    }
}
