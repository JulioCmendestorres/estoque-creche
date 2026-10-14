using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Estoque.Web.Data;
using Estoque.Web.Infrastructure;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Services;

/// <summary>
/// RF07 / E5.2: relatório de movimentações com filtros. Lê pelos filtros globais (tenant + unidade atual);
/// no consolidado (só admin) desliga o filtro de unidade e reaplica tenant + unidades vinculadas ao usuário.
/// </summary>
public sealed class RelatorioService(EstoqueDbContext db)
{
    public const int LimiteTela = 1000;
    public const int LimiteExportacao = 20000;

    public async Task<(RelatorioMovimentacoes? Relatorio, string? Erro)> GerarAsync(
        FiltroRelatorio f, int limite, string geradoPor)
    {
        if (f.De is null || f.Ate is null) return (null, "Informe a data inicial e a final.");
        if (f.De > f.Ate) return (null, "A data inicial deve ser anterior ou igual à data final.");
        if (!string.IsNullOrEmpty(f.Tipo) && f.Tipo is not (FiltroRelatorio.TodasEntradas or FiltroRelatorio.TodasSaidas)
            && !TiposMovimentacao.Rotulos.ContainsKey(f.Tipo))
            return (null, "Tipo de movimentação inválido.");

        var tenantId = db.Tenant.TenantId ?? throw new InvalidOperationException("Relatório sem tenant.");
        var usuarioId = db.Tenant.UsuarioId ?? throw new InvalidOperationException("Relatório sem usuário.");

        IQueryable<Movimentacao> q;
        if (f.Consolidado)
        {
            var unidadesDoUsuario = await db.UsuariosUnidades
                .Where(u => u.UsuarioId == usuarioId).Select(u => u.UnidadeId).ToListAsync();
            // Único ponto que desliga os filtros globais: o tenant é reaplicado aqui mesmo
            q = db.Movimentacoes.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && unidadesDoUsuario.Contains(m.UnidadeId));
        }
        else
        {
            q = db.Movimentacoes;
        }

        var de = f.De.Value;
        var ate = f.Ate.Value;
        q = q.Where(m => m.DataMovimentacao >= de && m.DataMovimentacao <= ate);
        q = f.Tipo switch
        {
            FiltroRelatorio.TodasEntradas => q.Where(m => m.TipoMovimentacao == TiposMovimentacao.DoacaoRecebida
                                                          || m.TipoMovimentacao == TiposMovimentacao.Compra),
            FiltroRelatorio.TodasSaidas => q.Where(m => m.TipoMovimentacao == TiposMovimentacao.ConsumoInterno
                                                        || m.TipoMovimentacao == TiposMovimentacao.DoacaoRealizada
                                                        || m.TipoMovimentacao == TiposMovimentacao.Avaria),
            null or "" => q,
            var tipo => q.Where(m => m.TipoMovimentacao == tipo),
        };
        if (f.CategoriaId is > 0) q = q.Where(m => m.Item!.CategoriaId == f.CategoriaId);
        if (f.ItemId is > 0) q = q.Where(m => m.ItemId == f.ItemId);
        if (f.ParceiroId is > 0) q = q.Where(m => m.ParceiroId == f.ParceiroId);
        if (!f.IncluirCanceladas) q = q.Where(m => m.Status != StatusMovimentacao.Cancelada);

        var total = await q.CountAsync();

        var resumo = await q.Where(m => m.Status == StatusMovimentacao.Processada)
            .GroupBy(m => m.TipoMovimentacao)
            .Select(g => new { Tipo = g.Key, Qtd = g.Count(), Valor = g.Sum(m => m.ValorTotal) })
            .ToListAsync();
        var ordemTipos = TiposMovimentacao.Rotulos.Keys.ToList();

        var brutas = await q
            .OrderBy(m => m.DataMovimentacao).ThenBy(m => m.Id)
            .Take(limite)
            .Select(m => new
            {
                m.NumeroMovimentacao, m.DataMovimentacao, m.TipoMovimentacao, m.UnidadeId,
                Item = m.Item!.Nome, Codigo = m.Item.CodigoInterno, Categoria = m.Item.Categoria!.Nome,
                m.Quantidade, m.Item.UnidadeMedida, m.ValorUnitario, m.ValorTotal,
                Parceiro = m.Parceiro != null ? m.Parceiro.Nome : null,
                m.Beneficiario, Responsavel = m.Responsavel!.Nome, m.Status, m.Observacoes,
            })
            .ToListAsync();

        var unidades = await db.Unidades.ToDictionaryAsync(u => u.Id, u => u.Nome);
        string NomeUnidade(int id) => unidades.TryGetValue(id, out var n) ? n : "?";

        var linhas = brutas.Select(m => new LinhaRelatorio(
            m.NumeroMovimentacao, m.DataMovimentacao, m.TipoMovimentacao, NomeUnidade(m.UnidadeId),
            m.Item, m.Codigo, m.Categoria, m.Quantidade, m.UnidadeMedida, m.ValorUnitario, m.ValorTotal,
            m.Parceiro, m.Beneficiario, m.Responsavel, m.Status, m.Observacoes)).ToList();

        return (new RelatorioMovimentacoes
        {
            Filtro = f,
            NomeOrganizacao = await db.Tenants.Select(t => t.Nome).FirstOrDefaultAsync() ?? "",
            Abrangencia = f.Consolidado ? "Todas as unidades" : NomeUnidade(db.Tenant.UnidadeId ?? 0),
            Linhas = linhas,
            Resumo = resumo.OrderBy(r => ordemTipos.IndexOf(r.Tipo))
                .Select(r => new ResumoTipo(r.Tipo, r.Qtd, r.Valor)).ToList(),
            TotalEncontrado = total,
            GeradoEmUtc = DateTime.UtcNow,
            GeradoPor = geradoPor,
            FiltrosDescritos = await DescreverAsync(f),
        }, null);
    }

    private async Task<IReadOnlyList<string>> DescreverAsync(FiltroRelatorio f)
    {
        var d = new List<string> { $"Período: {Formatos.Data(f.De!.Value)} a {Formatos.Data(f.Ate!.Value)}" };
        d.Add("Tipo: " + f.Tipo switch
        {
            FiltroRelatorio.TodasEntradas => "todas as entradas",
            FiltroRelatorio.TodasSaidas => "todas as saídas",
            null or "" => "todos",
            var t => TiposMovimentacao.Rotulo(t),
        });
        if (f.CategoriaId is > 0)
            d.Add("Categoria: " + await db.Categorias.Where(c => c.Id == f.CategoriaId).Select(c => c.Nome).FirstOrDefaultAsync());
        if (f.ItemId is > 0)
            d.Add("Item: " + await db.Itens.Where(i => i.Id == f.ItemId).Select(i => i.Nome).FirstOrDefaultAsync());
        if (f.ParceiroId is > 0)
            d.Add("Fornecedor: " + await db.Parceiros.Where(p => p.Id == f.ParceiroId).Select(p => p.Nome).FirstOrDefaultAsync());
        d.Add(f.IncluirCanceladas ? "Canceladas: incluídas" : "Canceladas: não incluídas");
        return d;
    }

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>
    /// CSV para Excel em pt-BR (";" e vírgula decimal, UTF-8 com BOM). Células que começam com
    /// = + - @ tab ou CR recebem apóstrofo (proteção contra CSV/formula injection, S24).
    /// </summary>
    public static byte[] GerarCsv(RelatorioMovimentacoes r)
    {
        var cfg = new CsvConfiguration(PtBr)
        {
            Delimiter = ";",
            InjectionOptions = InjectionOptions.Escape,
        };
        using var ms = new MemoryStream();
        using (var sw = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        using (var csv = new CsvWriter(sw, cfg))
        {
            string[] cabecalho =
            [
                "Número", "Data", "Unidade", "Movimento", "Tipo", "Código do item", "Item", "Categoria",
                "Quantidade", "Unidade de medida", "Valor unitário", "Valor total",
                "Fornecedor / doador", "Beneficiário", "Responsável", "Status", "Observações",
            ];
            foreach (var c in cabecalho) csv.WriteField(c);
            csv.NextRecord();

            foreach (var l in r.Linhas)
            {
                csv.WriteField(l.Numero);
                csv.WriteField(l.Data.ToString("dd/MM/yyyy", PtBr));
                csv.WriteField(l.Unidade);
                csv.WriteField(l.EhEntrada ? "Entrada" : "Saída");
                csv.WriteField(TiposMovimentacao.Rotulo(l.Tipo));
                csv.WriteField(l.CodigoItem);
                csv.WriteField(l.Item);
                csv.WriteField(l.Categoria);
                csv.WriteField(l.Quantidade.ToString(PtBr));
                csv.WriteField(l.UnidadeMedida);
                csv.WriteField(l.ValorUnitario.ToString("0.00", PtBr));
                csv.WriteField(l.ValorTotal.ToString("0.00", PtBr));
                csv.WriteField(l.EhEntrada ? l.Parceiro ?? "Anônimo" : "");
                csv.WriteField(l.Beneficiario ?? "");
                csv.WriteField(l.Responsavel);
                csv.WriteField(l.Status);
                csv.WriteField(l.Observacoes ?? "");
                csv.NextRecord();
            }
        }
        return ms.ToArray();
    }
}
