using Estoque.Web.Data;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.ViewComponents;

/// <summary>
/// Unidade no cabeçalho: o master escolhe entre todas as unidades ativas; os demais
/// só veem a unidade em que estão cadastrados.
/// </summary>
public class SeletorUnidadeViewComponent(EstoqueDbContext db, ITenantContext tenant) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var unidades = tenant.Master
            ? await db.Unidades.Where(u => u.Ativo)
                .Select(u => new UnidadeOpcao { Id = u.Id, Nome = u.Nome, Tipo = u.Tipo })
                .OrderBy(u => u.Tipo == "matriz" ? 0 : 1).ThenBy(u => u.Nome)
                .ToListAsync()
            : [];
        ViewBag.Atual = tenant.UnidadeId;
        ViewBag.NomeAtual = tenant.UnidadeNome;
        return View(unidades);
    }
}

/// <summary>Sino de alertas (RF12): itens abaixo do mínimo na unidade atual.</summary>
public class SinoAlertasViewComponent(DashboardService dashboard) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var total = await dashboard.ContarAlertasAsync();
        var lista = total == 0 ? [] : await dashboard.ConsultarAlertas().Take(8).ToListAsync();
        ViewBag.Total = total;
        return View(lista);
    }
}
