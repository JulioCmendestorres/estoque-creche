using System.Security.Claims;
using Estoque.Web.Data;
using Estoque.Web.Data.Interceptors;
using Estoque.Web.Data.Migrator;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Estoque.Tests.Sql;

namespace Estoque.Tests;

/// <summary>E5.1 — master único, perfis com permissões, usuário preso à unidade e migração V005.</summary>
[Collection(ColecaoBanco.Nome)]
public class UsuariosPerfisTests(BancoFixture db)
{
    private EstoqueDbContext Contexto(Cenario s)
    {
        var tenant = new TenantContext();
        tenant.Definir(s.Tenant, s.Matriz, s.Usuario, "127.0.0.1");
        tenant.DefinirAcesso(master: true, [], null, null);
        var opt = new DbContextOptionsBuilder<EstoqueDbContext>()
            .UseSqlServer(db.Cs)
            .AddInterceptors(new SessionContextInterceptor())
            .Options;
        return new EstoqueDbContext(opt, tenant);
    }

    [Fact]
    public async Task So_um_master_por_tenant_e_usuario_comum_exige_perfil()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        var outro = await NovoTenant(c);

        var segundoMaster = await Assert.ThrowsAsync<SqlException>(() => Exec(c,
            "INSERT INTO usuarios (tenant_id, nome, email, senha, unidade_id, master) VALUES (@t, 'M2', @e, 'x', @u, 1)",
            ("@t", s.Tenant), ("@e", $"m2{Guid.NewGuid():N}@t.local"), ("@u", s.Filial)));
        Assert.Equal(2601, segundoMaster.Number);

        var semPerfil = await Assert.ThrowsAsync<SqlException>(() => Exec(c,
            "INSERT INTO usuarios (tenant_id, nome, email, senha, unidade_id) VALUES (@t, 'Sem', @e, 'x', @u)",
            ("@t", s.Tenant), ("@e", $"s{Guid.NewGuid():N}@t.local"), ("@u", s.Filial)));
        Assert.Equal(547, semPerfil.Number);

        // Unidade de outro tenant: FK composta (unidade_id, tenant_id)
        var perfil = await Id(c, "INSERT INTO perfis (tenant_id, nome) VALUES (@t, 'P')", ("@t", s.Tenant));
        var unidadeAlheia = await Assert.ThrowsAsync<SqlException>(() => Exec(c,
            "INSERT INTO usuarios (tenant_id, nome, email, senha, unidade_id, perfil_id) VALUES (@t, 'X', @e, 'x', @u, @p)",
            ("@t", s.Tenant), ("@e", $"x{Guid.NewGuid():N}@t.local"), ("@u", outro.Matriz), ("@p", perfil)));
        Assert.Equal(547, unidadeAlheia.Number);
    }

    [Fact]
    public async Task Permissoes_do_perfil_sao_gravadas_trocadas_e_auditadas()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync()) s = await NovoTenant(c);

        int perfilId;
        await using (var ctx = Contexto(s))
        {
            var perfil = new Perfil { Nome = "Cozinha" };
            perfil.Permissoes.Add(new PerfilPermissao { Permissao = Permissoes.EstoqueMovimentar });
            perfil.Permissoes.Add(new PerfilPermissao { Permissao = Permissoes.RelatoriosVer });
            ctx.Perfis.Add(perfil);
            await ctx.SaveChangesAsync();
            perfilId = perfil.Id;
        }

        await using (var ctx = Contexto(s))
        {
            var perfil = await ctx.Perfis.Include(p => p.Permissoes).SingleAsync(p => p.Id == perfilId);
            perfil.Permissoes.RemoveAll(p => p.Permissao == Permissoes.RelatoriosVer);
            perfil.Permissoes.Add(new PerfilPermissao { Permissao = Permissoes.EstoqueCancelar });
            await ctx.SaveChangesAsync();
        }

        await using var c2 = await db.AbrirAsync();
        Assert.Equal(2, await Escalar(c2, "SELECT COUNT(*) FROM perfis_permissoes WHERE perfil_id = @p", ("@p", perfilId)));
        Assert.Equal(s.Tenant, await Escalar(c2, "SELECT DISTINCT tenant_id FROM perfis_permissoes WHERE perfil_id = @p", ("@p", perfilId)));
        Assert.Equal(1, await Escalar(c2, """
            SELECT COUNT(*) FROM logs_auditoria
            WHERE tabela_afetada = 'perfis_permissoes' AND acao = 'DELETE' AND registro_id = @p AND usuario_id = @u
              AND JSON_VALUE(dados_antigos, '$.permissao') = 'relatorios.ver'
            """, ("@p", perfilId), ("@u", s.Usuario)));
    }

    [Fact]
    public void Contexto_ignora_permissao_desconhecida_e_master_tem_todas()
    {
        var comum = new TenantContext();
        comum.DefinirAcesso(master: false, [Permissoes.RelatoriosVer, "usuarios.gerenciar", "qualquer"], "Visualizador", "Filial");
        Assert.True(comum.Tem(Permissoes.RelatoriosVer));
        Assert.False(comum.Tem(Permissoes.VisaoConsolidada));
        Assert.Single(comum.Permissoes);
        Assert.Equal("Visualizador", comum.PerfilNome);

        var master = new TenantContext();
        master.DefinirAcesso(master: true, [], null, "Matriz");
        Assert.All(Permissoes.Todas, p => Assert.True(master.Tem(p.Codigo)));
        Assert.Equal("Master", master.PerfilNome);
    }

    [Theory]
    [InlineData(false, Permissoes.EstoqueMovimentar, true)]
    [InlineData(false, Permissoes.EstoqueCancelar, false)]
    [InlineData(false, null, false)]   // só master
    [InlineData(true, null, true)]
    [InlineData(true, Permissoes.VisaoConsolidada, true)]
    public async Task Handler_de_permissao_decide_pelo_contexto(bool master, string? permissao, bool esperado)
    {
        var acesso = new TenantContext();
        acesso.DefinirAcesso(master, [Permissoes.EstoqueMovimentar], "Operacional", "Matriz");
        var req = new PermissaoRequirement(permissao);
        var ctx = new AuthorizationHandlerContext([req], new ClaimsPrincipal(new ClaimsIdentity("teste")), null);

        await new PermissaoHandler(acesso).HandleAsync(ctx);

        Assert.Equal(esperado, ctx.HasSucceeded);
    }

    /// <summary>
    /// V005 sobre dados da época dos papéis fixos: perfis criados por tenant, admin mais antigo vira master,
    /// os demais mantêm o sentido do papel e ficam numa unidade só (a matriz, se tinham acesso a ela).
    /// </summary>
    [Fact]
    public async Task V005_converte_papeis_em_perfis_e_escolhe_master_e_unidade()
    {
        var fixture = new BancoFixture();
        var pasta = Path.Combine(Path.GetTempPath(), "estoque-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pasta);
        foreach (var f in DatabaseMigrator.ListarMigracoes(BancoFixture.PastaMigracoes).Where(m => m.Versao <= 4))
            File.Copy(f.Caminho, Path.Combine(pasta, f.Nome));
        var opt = new MigratorOptions { AdminConnectionString = fixture.AdminCs, DatabaseName = fixture.NomeBanco, MigrationsPath = pasta };

        try
        {
            await new DatabaseMigrator(opt, NullLogger.Instance).ExecutarAsync();
            int admin1, admin2, oper, visu;
            await using (var c = await fixture.AbrirAsync())
            {
                var t = await Id(c, "INSERT INTO tenants (nome) VALUES ('A')");
                var m = await Id(c, "INSERT INTO unidades (tenant_id, nome, tipo) VALUES (@t, 'Matriz', 'matriz')", ("@t", t));
                var f = await Id(c, "INSERT INTO unidades (tenant_id, nome, tipo) VALUES (@t, 'Filial', 'filial')", ("@t", t));
                async Task<int> Usuario(string email, string papel, params int[] unidades)
                {
                    var id = await Id(c, "INSERT INTO usuarios (tenant_id, nome, email, senha, papel) VALUES (@t, @e, @e, 'x', @p)",
                        ("@t", t), ("@e", email), ("@p", papel));
                    foreach (var u in unidades)
                        await Exec(c, "INSERT INTO usuarios_unidades (tenant_id, usuario_id, unidade_id) VALUES (@t, @i, @u)",
                            ("@t", t), ("@i", id), ("@u", u));
                    return id;
                }
                admin1 = await Usuario("a1@t.local", "admin", f, m);
                admin2 = await Usuario("a2@t.local", "admin", m);
                oper = await Usuario("op@t.local", "operacional", f);
                visu = await Usuario("vi@t.local", "visualizador");
            }

            File.Copy(Path.Combine(BancoFixture.PastaMigracoes, "V005__perfis_e_usuario_master.sql"),
                Path.Combine(pasta, "V005__perfis_e_usuario_master.sql"));
            await new DatabaseMigrator(opt, NullLogger.Instance).ExecutarAsync();

            await using var c2 = await fixture.AbrirAsync();
            Assert.Equal(3, await Escalar(c2, "SELECT COUNT(*) FROM perfis"));
            Assert.Equal(admin1, await Escalar(c2, "SELECT id FROM usuarios WHERE master = 1"));
            Assert.Equal("Administrador", await Escalar(c2, "SELECT p.nome FROM usuarios u JOIN perfis p ON p.id = u.perfil_id WHERE u.id = @i", ("@i", admin2)));
            Assert.Equal("Operacional", await Escalar(c2, "SELECT p.nome FROM usuarios u JOIN perfis p ON p.id = u.perfil_id WHERE u.id = @i", ("@i", oper)));
            Assert.Equal("Matriz", await Escalar(c2, "SELECT un.nome FROM usuarios u JOIN unidades un ON un.id = u.unidade_id WHERE u.id = @i", ("@i", admin1)));
            Assert.Equal("Filial", await Escalar(c2, "SELECT un.nome FROM usuarios u JOIN unidades un ON un.id = u.unidade_id WHERE u.id = @i", ("@i", oper)));
            Assert.Equal("Matriz", await Escalar(c2, "SELECT un.nome FROM usuarios u JOIN unidades un ON un.id = u.unidade_id WHERE u.id = @i", ("@i", visu)));
            Assert.Equal(1, await Escalar(c2, "SELECT COUNT(*) FROM perfis_permissoes pp JOIN perfis p ON p.id = pp.perfil_id WHERE p.nome = 'Visualizador'"));
            Assert.Equal(0, await Escalar(c2, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('usuarios') AND name = 'papel'"));
            Assert.IsType<DBNull>(await Escalar(c2, "SELECT OBJECT_ID('usuarios_unidades')"));
        }
        finally
        {
            await fixture.DisposeAsync();
            Directory.Delete(pasta, recursive: true);
        }
    }
}
