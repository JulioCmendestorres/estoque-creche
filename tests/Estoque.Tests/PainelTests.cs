using Estoque.Web.Data;
using Estoque.Web.Data.Interceptors;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Estoque.Web.Services;
using Microsoft.EntityFrameworkCore;
using static Estoque.Tests.Sql;

namespace Estoque.Tests;

/// <summary>D-45 — painel com gráficos: somas por período, série mensal e retrato do estoque.</summary>
[Collection(ColecaoBanco.Nome)]
public class PainelTests(BancoFixture db)
{
    // As movimentações do auxiliar Mov() são gravadas com data 01/10/2026
    private static readonly DateOnly DataMov = new(2026, 10, 1);

    private EstoqueDbContext Contexto(Cenario s, int unidade)
    {
        var tenant = new TenantContext();
        tenant.Definir(s.Tenant, unidade, s.Usuario, "127.0.0.1");
        tenant.DefinirAcesso(master: true, [], null, unidade == s.Matriz ? "Matriz" : "Filial");
        var opt = new DbContextOptionsBuilder<EstoqueDbContext>()
            .UseSqlServer(db.Cs)
            .AddInterceptors(new SessionContextInterceptor())
            .Options;
        return new EstoqueDbContext(opt, tenant);
    }

    private async Task<Cenario> CenarioComMovimentos()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        await Mov(c, s, "doacao_recebida", 10, 5m);              // entrada R$ 50
        await Mov(c, s, "compra", 2, 3m);                        // entrada R$ 6
        await Mov(c, s, "consumo_interno", 4, 2m);               // saída   R$ 8
        await Mov(c, s, "doacao_recebida", 7, 9m, status: "pendente");   // não processada: fica fora
        return s;
    }

    [Fact]
    public async Task Soma_so_processadas_do_periodo_e_separa_entradas_de_saidas()
    {
        var s = await CenarioComMovimentos();
        await using var ctx = Contexto(s, s.Matriz);

        var p = await new DashboardService(ctx).MontarAsync(30, hoje: DataMov.AddDays(14));

        Assert.Equal(1, p.DoacoesRecebidas);
        Assert.Equal(56m, p.ValorEntradas);
        Assert.Equal(8m, p.ValorSaidas);
        Assert.Equal(5, p.PorTipo.Count);
        Assert.Equal(1, p.PorTipo.Single(t => t.Tipo == TiposMovimentacao.Compra).Movimentacoes);
        Assert.Equal(0, p.PorTipo.Single(t => t.Tipo == TiposMovimentacao.Avaria).Movimentacoes);
    }

    [Fact]
    public async Task Serie_mensal_tem_12_meses_e_termina_no_mes_atual()
    {
        var s = await CenarioComMovimentos();
        await using var ctx = Contexto(s, s.Matriz);

        var p = await new DashboardService(ctx).MontarAsync(30, hoje: DataMov.AddDays(14));

        Assert.Equal(12, p.Meses.Count);
        Assert.Equal((2025, 11), (p.Meses[0].Ano, p.Meses[0].Mes));
        var outubro = p.Meses[^1];
        Assert.Equal((2026, 10), (outubro.Ano, outubro.Mes));
        Assert.Equal((56m, 8m), (outubro.Entradas, outubro.Saidas));
        Assert.All(p.Meses.Take(11), m => Assert.Equal((0m, 0m), (m.Entradas, m.Saidas)));
    }

    [Fact]
    public async Task Periodo_anterior_e_periodos_maiores_enxergam_o_que_saiu_da_janela()
    {
        var s = await CenarioComMovimentos();
        await using var ctx = Contexto(s, s.Matriz);
        var servico = new DashboardService(ctx);
        var hoje = DataMov.AddDays(45);   // 15/11: o dia 01/10 caiu no período anterior de 30 dias

        var trinta = await servico.MontarAsync(30, hoje);
        Assert.Equal(0, trinta.DoacoesRecebidas);
        Assert.Equal(1, trinta.DoacoesRecebidasAnterior);
        Assert.Equal(56m, trinta.ValorEntradasAnterior);

        var noventa = await servico.MontarAsync(90, hoje);
        Assert.Equal(1, noventa.DoacoesRecebidas);

        var invalido = await servico.MontarAsync(7, hoje);   // período fora da lista vira 30
        Assert.Equal(30, invalido.PeriodoDias);
    }

    [Fact]
    public async Task Retrato_do_estoque_classifica_itens_e_respeita_a_unidade()
    {
        var s = await CenarioComMovimentos();
        await using (var c = await db.AbrirAsync())
        {
            // Mínimo acima do saldo (8): o item passa a "estoque baixo"
            await Exec(c, "UPDATE estoque_unidade SET quantidade_minima = 20 WHERE unidade_id = @u AND item_id = @i",
                ("@u", s.Matriz), ("@i", s.Item));
        }

        await using (var ctx = Contexto(s, s.Matriz))
        {
            var p = await new DashboardService(ctx).MontarAsync(30, hoje: DataMov);
            Assert.Equal((0, 1, 0), (p.ItensNormais, p.ItensEstoqueBaixo, p.ItensSemEstoque));
            Assert.Equal(1, p.TotalAlertas);
            var categoria = Assert.Single(p.PorCategoria);
            Assert.Equal("Alimento", categoria.Categoria);
            Assert.Equal(p.ValorEstoque, categoria.Valor);
            Assert.True(p.ValorEstoque > 0);
        }

        await using var filial = Contexto(s, s.Filial);
        var vazio = await new DashboardService(filial).MontarAsync(30, hoje: DataMov);
        Assert.Equal(0m, vazio.ValorEntradas);
        Assert.Empty(vazio.PorCategoria);
        Assert.Equal(0, vazio.ItensNormais + vazio.ItensEstoqueBaixo + vazio.ItensSemEstoque);
    }
}
