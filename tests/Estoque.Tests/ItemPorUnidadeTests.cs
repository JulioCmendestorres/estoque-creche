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

/// <summary>D-38 — item ativo por unidade e travas dos cadastros compartilhados entre matriz e filial.</summary>
[Collection(ColecaoBanco.Nome)]
public class ItemPorUnidadeTests(BancoFixture db)
{
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

    private static Task Desativar(SqlConnection c, int unidade, Cenario s) => Exec(c, """
        MERGE estoque_unidade AS e
        USING (SELECT @t AS tenant_id, @u AS unidade_id, @i AS item_id) AS x
           ON e.unidade_id = x.unidade_id AND e.item_id = x.item_id
        WHEN MATCHED THEN UPDATE SET ativo = 0
        WHEN NOT MATCHED THEN INSERT (tenant_id, unidade_id, item_id, ativo) VALUES (x.tenant_id, x.unidade_id, x.item_id, 0);
        """, ("@t", s.Tenant), ("@u", unidade), ("@i", s.Item));

    [Fact]
    public async Task Banco_recusa_movimentar_item_desativado_so_na_unidade_dele()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        await Desativar(c, s.Filial, s);

        var ex = await Assert.ThrowsAsync<SqlException>(() => Mov(c, s, "doacao_recebida", 5, 1m, unidade: s.Filial));
        Assert.Equal(50006, ex.Number);

        await Mov(c, s, "doacao_recebida", 5, 1m, unidade: s.Matriz);   // a outra unidade segue normal
        Assert.Equal((5, 5m), await Saldo(c, s.Matriz, s.Item));
    }

    [Fact]
    public async Task Servico_explica_que_item_esta_desativado_na_unidade()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            await Desativar(c, s.Filial, s);
        }

        await using var ctx = Contexto(s, s.Filial);
        var r = await new MovimentacaoService(ctx).RegistrarEntradaAsync(new EntradaInput
        {
            Origem = TiposMovimentacao.DoacaoRecebida, ItemId = s.Item, Quantidade = 1, Data = Tempo.Hoje,
        });
        Assert.False(r.Ok);
        Assert.Contains("desativado nesta unidade", r.Erro);
    }

    [Fact]
    public async Task Item_com_saldo_em_outra_unidade_nao_pode_ser_inativado_no_catalogo()
    {
        Cenario s;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            await Mov(c, s, "doacao_recebida", 12, 1m, unidade: s.Filial);
        }

        await using (var matriz = Contexto(s, s.Matriz))
        {
            var travas = new TravasCadastroService(matriz);
            var motivo = await travas.MotivoParaInativarItemAsync(s.Item);
            Assert.Contains("Filial ainda tem 12 pacote", motivo);
            Assert.Null(await travas.MotivoParaDesativarNaUnidadeAsync(s.Item));   // na matriz o saldo é zero
        }

        await using (var filial = Contexto(s, s.Filial))
            Assert.Contains("12 pacote nesta unidade", await new TravasCadastroService(filial).MotivoParaDesativarNaUnidadeAsync(s.Item));

        await using (var c = await db.AbrirAsync())
            await Mov(c, s, "consumo_interno", 12, 1m, unidade: s.Filial);
        await using (var matriz = Contexto(s, s.Matriz))
            Assert.Null(await new TravasCadastroService(matriz).MotivoParaInativarItemAsync(s.Item));
    }

    [Fact]
    public async Task Categoria_e_unidade_de_medida_em_uso_por_item_ativo_nao_podem_ser_inativadas()
    {
        Cenario s;
        int categoriaLivre;
        await using (var c = await db.AbrirAsync())
        {
            s = await NovoTenant(c);
            categoriaLivre = await Id(c, "INSERT INTO categorias_itens (tenant_id, nome) VALUES (@t, 'Livre')", ("@t", s.Tenant));
        }

        await using (var ctx = Contexto(s, s.Matriz))
        {
            var travas = new TravasCadastroService(ctx);
            Assert.Contains("Arroz", await travas.MotivoParaInativarCategoriaAsync(s.Categoria));
            Assert.Contains("Arroz", await travas.MotivoParaInativarUnidadeMedidaAsync("pacote"));
            Assert.Null(await travas.MotivoParaInativarCategoriaAsync(categoriaLivre));
            Assert.Null(await travas.MotivoParaInativarUnidadeMedidaAsync("kg"));
        }

        await using (var c = await db.AbrirAsync())
            await Exec(c, "UPDATE itens SET ativo = 0 WHERE id = @i", ("@i", s.Item));
        await using (var ctx = Contexto(s, s.Matriz))
            Assert.Null(await new TravasCadastroService(ctx).MotivoParaInativarCategoriaAsync(s.Categoria));
    }
}
