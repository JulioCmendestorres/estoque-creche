using Estoque.Web.Data;
using Estoque.Web.Infrastructure;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Controllers;

/// <summary>RF07 / E5.2: relatório de movimentações em tela, impressão, PDF e CSV. Exige a permissão de ver relatórios; consolidado só com a de visão consolidada (ou master).</summary>
[Permissao(Permissoes.RelatoriosVer)]
public class RelatoriosController(EstoqueDbContext db, RelatorioService relatorios, AuditoriaService auditoria) : Controller
{
    public async Task<IActionResult> Index([FromQuery] FiltroRelatorio filtro)
    {
        Normalizar(filtro);
        var (relatorio, erro) = await relatorios.GerarAsync(filtro, RelatorioService.LimiteTela, User.Identity?.Name ?? "");
        return View(await MontarAsync(filtro, relatorio, erro));
    }

    [Permissao(Permissoes.RelatoriosExportar)]
    public async Task<IActionResult> Exportar(string formato, [FromQuery] FiltroRelatorio filtro)
    {
        formato = (formato ?? "").ToLowerInvariant();
        if (formato is not ("pdf" or "csv")) return BadRequest();

        Normalizar(filtro);
        var (r, erro) = await relatorios.GerarAsync(filtro, RelatorioService.LimiteExportacao, User.Identity?.Name ?? "");
        if (r is null || r.Truncado)
        {
            TempData["Erro"] = erro ?? $"O filtro encontrou {r!.TotalEncontrado} movimentações; o limite para exportar é {RelatorioService.LimiteExportacao}. Reduza o período.";
            return RedirectToAction(nameof(Index), RotaDoFiltro(filtro));
        }

        var bytes = formato == "pdf" ? RelatorioPdf.Gerar(r) : RelatorioService.GerarCsv(r);
        await auditoria.RegistrarAsync("EXPORTAR_RELATORIO", db.Tenant.TenantId, db.Tenant.UnidadeId, db.Tenant.UsuarioId, new
        {
            relatorio = "movimentacoes",
            formato,
            de = filtro.De,
            ate = filtro.Ate,
            filtro.Tipo,
            filtro.CategoriaId,
            filtro.ItemId,
            filtro.ParceiroId,
            filtro.IncluirCanceladas,
            filtro.Consolidado,
            linhas = r.Linhas.Count,
        });

        var nome = $"movimentacoes_{filtro.De:yyyyMMdd}_{filtro.Ate:yyyyMMdd}.{formato}";
        return File(bytes, formato == "pdf" ? "application/pdf" : "text/csv; charset=utf-8", nome);
    }

    /// <summary>Período padrão: mês corrente. Consolidado é ignorado sem a permissão de visão consolidada.</summary>
    private void Normalizar(FiltroRelatorio f)
    {
        var hoje = Tempo.Hoje;
        f.De ??= new DateOnly(hoje.Year, hoje.Month, 1);
        f.Ate ??= hoje;
        f.Consolidado = f.Consolidado && db.Tenant.Tem(Permissoes.VisaoConsolidada);
    }

    private static object RotaDoFiltro(FiltroRelatorio f) => new
    {
        De = f.De?.ToString("yyyy-MM-dd"),
        Ate = f.Ate?.ToString("yyyy-MM-dd"),
        f.Tipo,
        f.CategoriaId,
        f.ItemId,
        f.ParceiroId,
        f.IncluirCanceladas,
        f.Consolidado,
    };

    private async Task<RelatorioIndexViewModel> MontarAsync(FiltroRelatorio filtro, RelatorioMovimentacoes? r, string? erro) => new()
    {
        Filtro = filtro,
        Relatorio = r,
        Erro = erro,
        PodeConsolidar = db.Tenant.Tem(Permissoes.VisaoConsolidada),
        ItemFiltro = filtro.ItemId is > 0 ? await db.Itens.FirstOrDefaultAsync(i => i.Id == filtro.ItemId) : null,
        Categorias = await db.Categorias.OrderBy(c => c.Nome).ToListAsync(),
        Parceiros = await db.Parceiros.OrderBy(p => p.Nome).ToListAsync(),
    };
}
