using Estoque.Web.Data;
using Estoque.Web.Data.Interceptors;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using static Estoque.Tests.Sql;

namespace Estoque.Tests;

/// <summary>T2.2 — um usuário do tenant A não lê nem altera dados do tenant B, nem de outra unidade.</summary>
[Collection(ColecaoBanco.Nome)]
public class IsolamentoTenantTests(BancoFixture db)
{
    private EstoqueDbContext Contexto(Cenario s, int? unidade = null)
    {
        var tenant = new TenantContext();
        tenant.Definir(s.Tenant, unidade ?? s.Matriz, s.Usuario, "127.0.0.1");
        tenant.DefinirAcesso(master: true, [], null, null);
        var opt = new DbContextOptionsBuilder<EstoqueDbContext>()
            .UseSqlServer(db.Cs)
            .AddInterceptors(new SessionContextInterceptor())
            .Options;
        return new EstoqueDbContext(opt, tenant);
    }

    [Fact]
    public async Task Usuario_do_tenant_A_nao_le_dados_do_tenant_B()
    {
        Cenario a, b;
        await using (var c = await db.AbrirAsync())
        {
            a = await NovoTenant(c);
            b = await NovoTenant(c);
            await Mov(c, b, "doacao_recebida", 7, 1m);
        }

        await using var ctx = Contexto(a);
        Assert.Null(await ctx.Itens.FirstOrDefaultAsync(i => i.Id == b.Item));     // acesso por ID direto
        Assert.Null(await ctx.Itens.FindAsync(b.Item));
        Assert.DoesNotContain(await ctx.Itens.Select(i => i.TenantId).Distinct().ToListAsync(), t => t != a.Tenant);
        Assert.Empty(await ctx.Movimentacoes.Where(m => m.TenantId == b.Tenant).ToListAsync());
        Assert.Empty(await ctx.Estoques.Where(e => e.ItemId == b.Item).ToListAsync());
        Assert.Empty(await ctx.Unidades.Where(u => u.Id == b.Matriz).ToListAsync());
    }

    [Fact]
    public async Task Gravar_entidade_de_outro_tenant_e_recusado()
    {
        Cenario a, b;
        await using (var c = await db.AbrirAsync())
        {
            a = await NovoTenant(c);
            b = await NovoTenant(c);
        }

        await using var ctx = Contexto(a);
        ctx.Categorias.Add(new Categoria { TenantId = b.Tenant, Nome = "Invasora" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Estoque_e_movimentacoes_sao_filtrados_pela_unidade_atual()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            await Mov(c, s, "doacao_recebida", 4, 1m, unidade: s.Matriz);
            await Mov(c, s, "doacao_recebida", 9, 1m, unidade: s.Filial);
        }

        await using (var matriz = Contexto(s, s.Matriz))
            Assert.Equal(4, (await matriz.Estoques.SingleAsync(e => e.ItemId == s.Item)).QuantidadeAtual);
        await using (var filial = Contexto(s, s.Filial))
        {
            Assert.Equal(9, (await filial.Estoques.SingleAsync(e => e.ItemId == s.Item)).QuantidadeAtual);
            Assert.Single(await filial.Movimentacoes.ToListAsync());
        }
    }

    [Fact]
    public async Task Insercao_pelo_EF_carimba_tenant_e_unidade_e_trigger_atualiza_saldo()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync()) s = await NovoTenant(c);

        await using var ctx = Contexto(s, s.Filial);
        var mov = new Movimentacao
        {
            TipoMovimentacao = TiposMovimentacao.DoacaoRecebida, ItemId = s.Item, Quantidade = 3, ValorUnitario = 2m,
            DataMovimentacao = new DateOnly(2026, 10, 1), ResponsavelId = s.Usuario, Status = StatusMovimentacao.Processada,
        };
        ctx.Movimentacoes.Add(mov);
        await ctx.SaveChangesAsync();

        Assert.Equal(s.Tenant, mov.TenantId);
        Assert.Equal(s.Filial, mov.UnidadeId);
        Assert.StartsWith("MOV-", mov.NumeroMovimentacao);
        await using var c2 = await db.AbrirAsync();
        Assert.Equal((3, 6m), await Saldo(c2, s.Filial, s.Item));
        Assert.Equal(s.Usuario, await Escalar(c2,
            "SELECT TOP 1 usuario_id FROM logs_auditoria WHERE tabela_afetada = 'movimentacoes' AND registro_id = @id", ("@id", mov.Id)));
    }
}
