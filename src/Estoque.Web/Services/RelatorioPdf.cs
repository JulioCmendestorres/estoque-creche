using Estoque.Web.Infrastructure;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Estoque.Web.Services;

/// <summary>PDF do relatório de movimentações (QuestPDF, licença Community — D-29). A4 paisagem.</summary>
public static class RelatorioPdf
{
    private static readonly Color Azul = Color.FromHex("#1f4e8c");
    private static readonly Color Borda = Color.FromHex("#c9d1db");
    private static readonly Color Zebra = Color.FromHex("#f2f4f7");

    static RelatorioPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Gerar(RelatorioMovimentacoes r) =>
        Document.Create(doc => doc.Page(p =>
        {
            p.Size(PageSizes.A4.Landscape());
            p.Margin(1.2f, Unit.Centimetre);
            p.DefaultTextStyle(t => t.FontSize(8.5f));
            p.Header().Element(c => Cabecalho(c, r));
            p.Content().PaddingTop(6).Column(col =>
            {
                col.Spacing(8);
                col.Item().Element(c => Resumo(c, r));
                if (r.Truncado)
                    col.Item().Text($"Atenção: exibindo {r.Linhas.Count} de {r.TotalEncontrado} movimentações. Reduza o período para ver todas.")
                        .FontColor(Colors.Red.Darken2);
                col.Item().Element(c => Detalhe(c, r));
            });
            p.Footer().Row(row =>
            {
                row.RelativeItem().Text("Estoque & Doações").FontSize(7).FontColor(Colors.Grey.Darken1);
                row.RelativeItem().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.Span("Página ");
                    t.CurrentPageNumber();
                    t.Span(" de ");
                    t.TotalPages();
                });
            });
        })).GeneratePdf();

    private static void Cabecalho(IContainer c, RelatorioMovimentacoes r) => c.Column(col =>
    {
        col.Item().Row(row =>
        {
            row.RelativeItem().Column(t =>
            {
                t.Item().Text(r.NomeOrganizacao).FontSize(9).FontColor(Colors.Grey.Darken2);
                t.Item().Text("Relatório de movimentações").FontSize(15).Bold().FontColor(Azul);
            });
            row.ConstantItem(230).Column(t =>
            {
                t.Item().AlignRight().Text(r.Abrangencia).Bold();
                t.Item().AlignRight().Text($"Gerado em {Tempo.Formatar(r.GeradoEmUtc)} por {r.GeradoPor}")
                    .FontSize(7.5f).FontColor(Colors.Grey.Darken1);
            });
        });
        col.Item().PaddingTop(3).Text(string.Join("   ·   ", r.FiltrosDescritos)).FontSize(7.5f);
        col.Item().PaddingTop(4).LineHorizontal(0.75f).LineColor(Azul);
    });

    private static void Resumo(IContainer c, RelatorioMovimentacoes r) => c.Column(col =>
    {
        col.Item().Text("Resumo (somente movimentações processadas)").Bold();
        col.Item().PaddingTop(3).Width(360).Table(t =>
        {
            t.ColumnsDefinition(d =>
            {
                d.RelativeColumn(3);
                d.RelativeColumn(2);
                d.RelativeColumn(2);
            });
            t.Header(h =>
            {
                CelulaCabecalho(h.Cell(), "Tipo");
                CelulaCabecalho(h.Cell(), "Movimentações", direita: true);
                CelulaCabecalho(h.Cell(), "Valor total", direita: true);
            });
            if (r.Resumo.Count == 0)
                t.Cell().ColumnSpan(3).Element(Celula).Text("Nenhuma movimentação processada no filtro.").Italic();
            foreach (var x in r.Resumo)
            {
                t.Cell().Element(Celula).Text($"{(x.EhEntrada ? "Entrada" : "Saída")} · {TiposMovimentacao.Rotulo(x.Tipo)}");
                t.Cell().Element(Celula).AlignRight().Text(x.Movimentacoes.ToString());
                t.Cell().Element(Celula).AlignRight().Text(Formatos.Moeda(x.ValorTotal));
            }
            t.Cell().ColumnSpan(2).Element(Celula).Text("Total de entradas").Bold();
            t.Cell().Element(Celula).AlignRight().Text(Formatos.Moeda(r.TotalEntradas)).Bold();
            t.Cell().ColumnSpan(2).Element(Celula).Text("Total de saídas").Bold();
            t.Cell().Element(Celula).AlignRight().Text(Formatos.Moeda(r.TotalSaidas)).Bold();
        });
    });

    private static void Detalhe(IContainer c, RelatorioMovimentacoes r) => c.Table(t =>
    {
        var consolidado = r.Filtro.Consolidado;
        t.ColumnsDefinition(d =>
        {
            d.ConstantColumn(60);   // data
            d.ConstantColumn(78);   // número
            if (consolidado) d.RelativeColumn(1.1f);
            d.ConstantColumn(78);   // tipo
            d.RelativeColumn(2.6f); // item
            d.ConstantColumn(58);   // quantidade
            d.ConstantColumn(58);   // valor unitário
            d.ConstantColumn(64);   // valor total
            d.RelativeColumn(2);    // origem / destino
            d.RelativeColumn(1.4f); // responsável
            d.ConstantColumn(54);   // status
        });
        t.Header(h =>
        {
            CelulaCabecalho(h.Cell(), "Data");
            CelulaCabecalho(h.Cell(), "Número");
            if (consolidado) CelulaCabecalho(h.Cell(), "Unidade");
            CelulaCabecalho(h.Cell(), "Tipo");
            CelulaCabecalho(h.Cell(), "Item");
            CelulaCabecalho(h.Cell(), "Qtd.", direita: true);
            CelulaCabecalho(h.Cell(), "Valor unit.", direita: true);
            CelulaCabecalho(h.Cell(), "Valor total", direita: true);
            CelulaCabecalho(h.Cell(), "Fornecedor / beneficiário");
            CelulaCabecalho(h.Cell(), "Responsável");
            CelulaCabecalho(h.Cell(), "Status");
        });

        if (r.Linhas.Count == 0)
            t.Cell().ColumnSpan(consolidado ? 11u : 10u).Element(Celula).Text("Nenhuma movimentação encontrada.").Italic();

        var i = 0;
        foreach (var l in r.Linhas)
        {
            var fundo = i++ % 2 == 1 ? Zebra : Colors.White;
            var cor = l.Cancelada ? Colors.Grey.Medium : Colors.Black;
            IContainer C(IContainer x) => Celula(x.Background(fundo));

            t.Cell().Element(C).Text(Formatos.Data(l.Data)).FontColor(cor);
            t.Cell().Element(C).Text(l.Numero).FontSize(7.5f).FontColor(cor);
            if (consolidado) t.Cell().Element(C).Text(l.Unidade).FontColor(cor);
            t.Cell().Element(C).Text(TiposMovimentacao.Rotulo(l.Tipo)).FontColor(cor);
            t.Cell().Element(C).Text(txt =>
            {
                txt.Span(l.Item).FontColor(cor);
                txt.Span($"  {l.CodigoItem}").FontSize(7).FontColor(Colors.Grey.Darken1);
            });
            t.Cell().Element(C).AlignRight().Text($"{(l.EhEntrada ? "+" : "−")}{l.Quantidade} {l.UnidadeMedida}").FontColor(cor);
            t.Cell().Element(C).AlignRight().Text(Formatos.Moeda(l.ValorUnitario)).FontColor(cor);
            t.Cell().Element(C).AlignRight().Text(Formatos.Moeda(l.ValorTotal)).FontColor(cor);
            t.Cell().Element(C).Text(l.OrigemDestino).FontColor(cor);
            t.Cell().Element(C).Text(l.Responsavel).FontColor(cor);
            t.Cell().Element(C).Text(l.Status).FontColor(cor);
        }
    });

    private static IContainer Celula(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(Borda).PaddingVertical(2.5f).PaddingHorizontal(3);

    private static void CelulaCabecalho(IContainer c, string texto, bool direita = false)
    {
        var cel = c.Background(Azul).PaddingVertical(3).PaddingHorizontal(3);
        (direita ? cel.AlignRight() : cel).Text(texto).FontColor(Colors.White).Bold();
    }
}
