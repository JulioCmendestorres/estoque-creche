using Estoque.Web.Data;
using Estoque.Web.Data.Interceptors;
using Estoque.Web.Infrastructure;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static Estoque.Tests.Sql;

namespace Estoque.Tests;

/// <summary>D-43 — entrada com vários itens: um cabeçalho, uma movimentação por linha, tudo ou nada.</summary>
[Collection(ColecaoBanco.Nome)]
public class EntradaVariosItensTests(BancoFixture db)
{
    private EstoqueDbContext Contexto(Cenario s)
    {
        var tenant = new TenantContext();
        tenant.Definir(s.Tenant, s.Matriz, s.Usuario, "127.0.0.1");
        tenant.DefinirAcesso(master: true, [], null, "Matriz");
        var opt = new DbContextOptionsBuilder<EstoqueDbContext>()
            .UseSqlServer(db.Cs)
            .AddInterceptors(new SessionContextInterceptor())
            .Options;
        return new EstoqueDbContext(opt, tenant);
    }

    private static Task<int> OutroItem(SqlConnection c, Cenario s, string codigo, string nome, decimal? preco = null) => Id(c,
        "INSERT INTO itens (tenant_id, codigo_interno, nome, categoria_id, unidade_medida, preco_estimado) VALUES (@t, @cod, @n, @c, 'kg', @p)",
        ("@t", s.Tenant), ("@cod", codigo), ("@n", nome), ("@c", s.Categoria), ("@p", preco));

    private static async Task<int> Contar(SqlConnection c, Cenario s)
    {
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM movimentacoes WHERE tenant_id = @t", c);
        cmd.Parameters.AddWithValue("@t", s.Tenant);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Cada_linha_vira_uma_movimentacao_e_soma_no_estoque()
    {
        Cenario s;
        int feijao;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            feijao = await OutroItem(c, s, "F1", "Feijão", preco: 8m);
        }

        await using (var ctx = Contexto(s))
        {
            var r = await new MovimentacaoService(ctx).RegistrarEntradaAsync(new EntradaInput
            {
                Origem = TiposMovimentacao.DoacaoRecebida, Data = Tempo.Hoje, Observacao = "Campanha de inverno",
                Linhas =
                [
                    new EntradaLinha { ItemId = s.Item, Quantidade = 10, ValorUnitario = 5m },
                    new EntradaLinha { ItemId = feijao, Quantidade = 4 },                 // sem valor: usa o preço estimado
                    new EntradaLinha { ItemId = s.Item, Quantidade = 2, ValorUnitario = 5m }, // mesmo item em duas linhas
                ],
            });
            Assert.True(r.Ok, r.Erro);
        }

        await using var v = await db.AbrirAsync();
        Assert.Equal(3, await Contar(v, s));
        Assert.Equal((12, 60m), await Saldo(v, s.Matriz, s.Item));
        Assert.Equal((4, 32m), await Saldo(v, s.Matriz, feijao));
    }

    [Fact]
    public async Task Linha_invalida_nao_grava_nenhuma_e_diz_qual_e()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync()) s = await NovoTenant(c);

        await using (var ctx = Contexto(s))
        {
            var r = await new MovimentacaoService(ctx).RegistrarEntradaAsync(new EntradaInput
            {
                Origem = TiposMovimentacao.Compra, Data = Tempo.Hoje,
                Linhas =
                [
                    new EntradaLinha { ItemId = s.Item, Quantidade = 3 },
                    new EntradaLinha { ItemId = int.MaxValue, Quantidade = 1 },
                ],
            });
            Assert.False(r.Ok);
            Assert.StartsWith("Linha 2:", r.Erro);
        }

        await using var v = await db.AbrirAsync();
        Assert.Equal(0, await Contar(v, s));
        Assert.Equal((0, 0m), await Saldo(v, s.Matriz, s.Item));
    }

    [Fact]
    public async Task Item_de_outro_tenant_e_recusado()
    {
        Cenario s, outro;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            outro = await NovoTenant(c);
        }

        await using var ctx = Contexto(s);
        var r = await new MovimentacaoService(ctx).RegistrarEntradaAsync(new EntradaInput
        {
            Origem = TiposMovimentacao.DoacaoRecebida, Data = Tempo.Hoje,
            Linhas = [new EntradaLinha { ItemId = outro.Item, Quantidade = 1 }],
        });
        Assert.False(r.Ok);
        Assert.Equal("Item não encontrado ou inativo.", r.Erro);
    }

    [Fact]
    public async Task Entrada_sem_linhas_ou_acima_do_limite_e_recusada()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync()) s = await NovoTenant(c);

        await using var ctx = Contexto(s);
        var servico = new MovimentacaoService(ctx);

        var vazia = await servico.RegistrarEntradaAsync(new EntradaInput { Origem = TiposMovimentacao.Compra, Data = Tempo.Hoje });
        Assert.False(vazia.Ok);

        var demais = await servico.RegistrarEntradaAsync(new EntradaInput
        {
            Origem = TiposMovimentacao.Compra, Data = Tempo.Hoje,
            Linhas = [.. Enumerable.Range(0, EntradaInput.MaximoLinhas + 1).Select(_ => new EntradaLinha { ItemId = s.Item, Quantidade = 1 })],
        });
        Assert.False(demais.Ok);
    }
}
