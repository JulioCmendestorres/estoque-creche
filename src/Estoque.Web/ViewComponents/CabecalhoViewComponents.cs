using Estoque.Web.Data;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.ViewComponents;

/// <summary>Seletor Matriz/Filial do cabeçalho: só as unidades do usuário (usuarios_unidades).</summary>
public class SeletorUnidadeViewComponent(EstoqueDbContext db, ITenantContext tenant) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var unidades = await db.UsuariosUnidades
            .Where(uu => uu.UsuarioId == tenant.UsuarioId && uu.Unidade!.Ativo)
            .Select(uu => new UnidadeOpcao { Id = uu.Unidade!.Id, Nome = uu.Unidade.Nome, Tipo = uu.Unidade.Tipo })
            .OrderBy(u => u.Tipo == "matriz" ? 0 : 1).ThenBy(u => u.Nome)
            .ToListAsync();
        ViewBag.Atual = tenant.UnidadeId;
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
