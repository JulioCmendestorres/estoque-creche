using Estoque.Web.Data;
using Estoque.Web.Data.Interceptors;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static Estoque.Tests.Sql;

namespace Estoque.Tests;

/// <summary>D-46 — unidades com nome próprio: criar, renomear e desativar com confirmação pelo nome.</summary>
[Collection(ColecaoBanco.Nome)]
public class UnidadesTests(BancoFixture db)
{
    private EstoqueDbContext Contexto(Cenario s, int unidade)
    {
        var tenant = new TenantContext();
        tenant.Definir(s.Tenant, unidade, s.Usuario, "127.0.0.1");
        tenant.DefinirAcesso(master: true, [], null, "Matriz");
        var opt = new DbContextOptionsBuilder<EstoqueDbContext>()
            .UseSqlServer(db.Cs)
            .AddInterceptors(new SessionContextInterceptor())
            .Options;
        return new EstoqueDbContext(opt, tenant);
    }

    private async Task<Cenario> Novo()
    {
        await using var c = await db.AbrirAsync();
        return await NovoTenant(c);
    }

    private static async Task<T> Escalar<T>(SqlConnection c, string sql, params (string, object?)[] ps)
    {
        await using var cmd = new SqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Master_cria_terceira_unidade_e_renomeia_com_auditoria()
    {
        var s = await Novo();
        await using (var ctx = Contexto(s, s.Matriz))
        {
            var servico = new UnidadesService(ctx);
            Assert.True((await servico.SalvarAsync(new UnidadeInput { Nome = "  Vila Alice  " })).Ok);

            var linhas = await servico.ListarAsync();
            Assert.Equal(3, linhas.Count);
            var nova = linhas.Single(l => l.Unidade.Nome == "Vila Alice");   // nome sem os espaços das pontas
            Assert.True(nova.Unidade.Ativo);
            Assert.True(linhas.Single(l => l.Unidade.Id == s.Matriz).Atual);

            Assert.True((await servico.SalvarAsync(new UnidadeInput { Id = s.Filial, Nome = "Sede" })).Ok);
        }

        await using var c = await db.AbrirAsync();
        Assert.Equal("Sede", await Escalar<string>(c, "SELECT nome FROM unidades WHERE id = @i", ("@i", s.Filial)));
        // Criar e renomear ficam na auditoria, com o usuário que fez
        Assert.Equal(2, await Escalar<int>(c,
            "SELECT COUNT(*) FROM logs_auditoria WHERE tenant_id = @t AND tabela_afetada = 'unidades' AND usuario_id = @u",
            ("@t", s.Tenant), ("@u", s.Usuario)));
    }

    [Fact]
    public async Task Nome_repetido_ou_vazio_e_recusado()
    {
        var s = await Novo();
        await using var ctx = Contexto(s, s.Matriz);
        var servico = new UnidadesService(ctx);

        var repetido = await servico.SalvarAsync(new UnidadeInput { Nome = "matriz" });   // sem distinguir maiúsculas
        Assert.False(repetido.Ok);
        Assert.Contains("Já existe", repetido.Erro);

        Assert.False((await servico.SalvarAsync(new UnidadeInput { Nome = "   " })).Ok);
        Assert.Equal(2, (await servico.ListarAsync()).Count);
    }

    [Fact]
    public async Task Desativar_exige_o_nome_exato()
    {
        var s = await Novo();
        await using (var ctx = Contexto(s, s.Matriz))
        {
            var servico = new UnidadesService(ctx);
            Assert.Contains("não confere", (await servico.DesativarAsync(s.Filial, "filial")).Erro);
            Assert.Contains("não confere", (await servico.DesativarAsync(s.Filial, null)).Erro);
            Assert.True((await servico.DesativarAsync(s.Filial, " Filial ")).Ok);
            Assert.False((await servico.DesativarAsync(s.Filial, "Filial")).Ok);   // já desativada
        }

        await using (var c = await db.AbrirAsync())
            Assert.False(await Escalar<bool>(c, "SELECT ativo FROM unidades WHERE id = @i", ("@i", s.Filial)));

        await using var outro = Contexto(s, s.Matriz);
        Assert.True((await new UnidadesService(outro).ReativarAsync(s.Filial)).Ok);
    }

    [Fact]
    public async Task Nao_desativa_a_unidade_atual_nem_com_usuario_ativo_nem_com_saldo()
    {
        var s = await Novo();
        await using (var c = await db.AbrirAsync())
        {
            var perfil = await Id(c, "INSERT INTO perfis (tenant_id, nome) VALUES (@t, 'P')", ("@t", s.Tenant));
            await Exec(c, "INSERT INTO usuarios (tenant_id, nome, email, senha, unidade_id, perfil_id) VALUES (@t, 'Ana', @e, 'x', @u, @p)",
                ("@t", s.Tenant), ("@e", $"ana{Guid.NewGuid():N}@teste.local"), ("@u", s.Filial), ("@p", perfil));
            await Mov(c, s, "doacao_recebida", 5, 1m, unidade: s.Filial);
        }

        await using var ctx = Contexto(s, s.Matriz);
        var servico = new UnidadesService(ctx);

        Assert.Contains("Você está nesta unidade", (await servico.DesativarAsync(s.Matriz, "Matriz")).Erro);
        Assert.Contains("usuário ativo", (await servico.DesativarAsync(s.Filial, "Filial")).Erro);

        await using (var c = await db.AbrirAsync())
            await Exec(c, "UPDATE usuarios SET ativo = 0 WHERE unidade_id = @u AND master = 0", ("@u", s.Filial));
        Assert.Contains("item com saldo", (await servico.DesativarAsync(s.Filial, "Filial")).Erro);

        var linha = (await servico.ListarAsync()).Single(l => l.Unidade.Id == s.Filial);
        Assert.Equal((0, 1), (linha.UsuariosAtivos, linha.ItensComSaldo));

        await using (var c = await db.AbrirAsync())
            await Mov(c, s, "consumo_interno", 5, 1m, unidade: s.Filial);
        Assert.True((await servico.DesativarAsync(s.Filial, "Filial")).Ok);
    }

    [Fact]
    public async Task Unidade_de_outro_tenant_nao_e_alcancada()
    {
        var s = await Novo();
        var outro = await Novo();
        await using var ctx = Contexto(s, s.Matriz);
        var servico = new UnidadesService(ctx);

        Assert.Null(await servico.ObterAsync(outro.Filial));
        Assert.False((await servico.DesativarAsync(outro.Filial, "Filial")).Ok);
        Assert.False((await servico.SalvarAsync(new UnidadeInput { Id = outro.Filial, Nome = "Invadida" })).Ok);
    }
}
