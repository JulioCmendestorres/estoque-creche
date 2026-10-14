using System.Text;
using Estoque.Web.Data;
using Estoque.Web.Data.Interceptors;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.EntityFrameworkCore;
using static Estoque.Tests.Sql;

namespace Estoque.Tests;

/// <summary>E5.2 — relatório de movimentações: filtros, consolidado, isolamento, CSV e PDF.</summary>
[Collection(ColecaoBanco.Nome)]
public class RelatorioTests(BancoFixture db)
{
    private static readonly DateOnly Dia = new(2026, 10, 1);

    private EstoqueDbContext Contexto(Cenario s, int unidade)
    {
        var tenant = new TenantContext();
        tenant.Definir(s.Tenant, unidade, s.Usuario, Papeis.Admin, "127.0.0.1");
        var opt = new DbContextOptionsBuilder<EstoqueDbContext>()
            .UseSqlServer(db.Cs)
            .AddInterceptors(new SessionContextInterceptor())
            .Options;
        return new EstoqueDbContext(opt, tenant);
    }

    private static FiltroRelatorio Filtro(Action<FiltroRelatorio>? ajuste = null)
    {
        var f = new FiltroRelatorio { De = Dia, Ate = Dia };
        ajuste?.Invoke(f);
        return f;
    }

    private async Task<RelatorioMovimentacoes> Gerar(Cenario s, int unidade, FiltroRelatorio f)
    {
        await using var ctx = Contexto(s, unidade);
        var (r, erro) = await new RelatorioService(ctx).GerarAsync(f, RelatorioService.LimiteTela, "Teste");
        Assert.Null(erro);
        return r!;
    }

    [Fact]
    public async Task Filtra_por_tipo_grupo_e_resume_so_processadas()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            await Mov(c, s, "doacao_recebida", 10, 2m);
            await Mov(c, s, "compra", 5, 3m, parceiro: await Id(c,
                "INSERT INTO parceiros (tenant_id, nome, tipo) VALUES (@t, 'Mercado', 'empresa')", ("@t", s.Tenant)));
            await Mov(c, s, "consumo_interno", 4, 2m);
            var cancelada = await Mov(c, s, "avaria", 1, 2m);
            await Exec(c, "UPDATE movimentacoes SET status = 'cancelada' WHERE id = @id", ("@id", cancelada));
        }

        var tudo = await Gerar(s, s.Matriz, Filtro(f => f.IncluirCanceladas = true));
        Assert.Equal(4, tudo.Linhas.Count);
        Assert.Equal(35m, tudo.TotalEntradas);             // 10*2 + 5*3
        Assert.Equal(8m, tudo.TotalSaidas);                // cancelada não soma
        Assert.DoesNotContain(tudo.Resumo, x => x.Tipo == "avaria");

        Assert.Equal(3, (await Gerar(s, s.Matriz, Filtro())).Linhas.Count); // canceladas fora por padrão
        Assert.Equal(2, (await Gerar(s, s.Matriz, Filtro(f => f.Tipo = FiltroRelatorio.TodasEntradas))).Linhas.Count);
        Assert.Single((await Gerar(s, s.Matriz, Filtro(f => f.Tipo = FiltroRelatorio.TodasSaidas))).Linhas);
        Assert.Equal("Mercado", (await Gerar(s, s.Matriz, Filtro(f => f.Tipo = "compra"))).Linhas.Single().OrigemDestino);
        Assert.Empty((await Gerar(s, s.Matriz, Filtro(f => { f.De = Dia.AddDays(1); f.Ate = Dia.AddDays(2); }))).Linhas);
    }

    [Fact]
    public async Task Sem_consolidado_le_so_a_unidade_atual_e_consolidado_le_todas()
    {
        Cenario s, outro;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            outro = await NovoTenant(c);
            await Mov(c, s, "doacao_recebida", 2, 1m, unidade: s.Matriz);
            await Mov(c, s, "doacao_recebida", 3, 1m, unidade: s.Filial);
            await Mov(c, outro, "doacao_recebida", 99, 1m);
        }

        var filial = await Gerar(s, s.Filial, Filtro());
        Assert.Equal("Filial", filial.Abrangencia);
        Assert.Equal(3, filial.Linhas.Single().Quantidade);

        var consolidado = await Gerar(s, s.Filial, Filtro(f => f.Consolidado = true));
        Assert.Equal("Todas as unidades", consolidado.Abrangencia);
        Assert.Equal([2, 3], consolidado.Linhas.Select(l => l.Quantidade).Order());
        Assert.Equal(["Filial", "Matriz"], consolidado.Linhas.Select(l => l.Unidade).Order());
    }

    [Fact]
    public async Task Consolidado_respeita_unidades_vinculadas_ao_usuario()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            await Mov(c, s, "doacao_recebida", 2, 1m, unidade: s.Matriz);
            await Mov(c, s, "doacao_recebida", 3, 1m, unidade: s.Filial);
            await Exec(c, "DELETE FROM usuarios_unidades WHERE usuario_id = @u AND unidade_id = @f", ("@u", s.Usuario), ("@f", s.Filial));
        }

        var r = await Gerar(s, s.Matriz, Filtro(f => f.Consolidado = true));
        Assert.Equal(2, r.Linhas.Single().Quantidade);
    }

    [Fact]
    public async Task Periodo_invertido_e_tipo_desconhecido_sao_recusados()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync()) s = await NovoTenant(c);
        await using var ctx = Contexto(s, s.Matriz);
        var svc = new RelatorioService(ctx);

        Assert.NotNull((await svc.GerarAsync(Filtro(f => f.De = Dia.AddDays(1)), 10, "x")).Erro);
        Assert.NotNull((await svc.GerarAsync(Filtro(f => f.Tipo = "'; DROP TABLE x --"), 10, "x")).Erro);
    }

    [Fact]
    public async Task Csv_protege_contra_formula_injection_e_usa_padrao_ptBR()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            await Mov(c, s, "doacao_recebida", 10, 1m);
            await Mov(c, s, "doacao_realizada", 1, 1.5m, beneficiario: "=HYPERLINK(\"http://x\")");
            await Mov(c, s, "doacao_realizada", 1, 1m, beneficiario: "+5511 Família; \"Silva\"");
            await Mov(c, s, "doacao_realizada", 1, 1m, beneficiario: "@SUM(A1)");
            await Mov(c, s, "doacao_realizada", 1, 1m, beneficiario: "-2+3");
        }

        var csv = RelatorioService.GerarCsv(await Gerar(s, s.Matriz, Filtro()));
        Assert.Equal([0xEF, 0xBB, 0xBF], csv[..3]);           // BOM: Excel abre os acentos certo
        var texto = Encoding.UTF8.GetString(csv[3..]);
        Assert.StartsWith("Número;Data;Unidade;", texto);
        Assert.Contains("1,50", texto);                        // vírgula decimal
        Assert.DoesNotContain(";=HYPERLINK", texto);
        Assert.DoesNotContain(";\"=", texto);
        Assert.Contains("'=HYPERLINK", texto);
        Assert.Contains("'+5511", texto);
        Assert.Contains("'@SUM", texto);
        Assert.Contains("'-2+3", texto);
    }

    [Fact]
    public async Task Pdf_e_gerado()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            for (var i = 0; i < 60; i++) await Mov(c, s, "doacao_recebida", 1 + i, 1m);
        }

        var pdf = RelatorioPdf.Gerar(await Gerar(s, s.Matriz, Filtro(f => f.Consolidado = true)));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(pdf.Length > 2000);
    }
}
