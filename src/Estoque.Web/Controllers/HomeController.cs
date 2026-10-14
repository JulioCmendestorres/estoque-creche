using System.Diagnostics;
using Estoque.Web.Data;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Controllers;

public class HomeController(DashboardService dashboard) : Controller
{
    public async Task<IActionResult> Index() => View(await dashboard.MontarAsync());

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Erro() =>
        View("Error", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });

    [AllowAnonymous]
    public IActionResult Status(int codigo)
    {
        Response.StatusCode = codigo;
        return View("Status", codigo);
    }
}

public class UnidadeController(EstoqueDbContext db, TenantContext tenant, AuditoriaService auditoria) : Controller
{
    /// <summary>
    /// Troca a unidade atual. Só o master transita entre unidades; a unidade precisa estar ativa
    /// e ser do tenant (filtro global). Os demais usuários ficam na unidade do cadastro.
    /// </summary>
    [SomenteMaster, HttpPost]
    public async Task<IActionResult> Alternar(int unidadeId, string? returnUrl)
    {
        var unidade = await db.Unidades.FirstOrDefaultAsync(u => u.Id == unidadeId && u.Ativo);
        if (unidade is null) return Forbid();

        var usuario = await db.Usuarios.FirstAsync(u => u.Id == tenant.UsuarioId);
        var anterior = tenant.UnidadeId;
        await Autenticacao.EntrarAsync(HttpContext, Autenticacao.MontarPrincipal(usuario, unidade.Id, usuario.TrocarSenha));
        tenant.Definir(tenant.TenantId, unidade.Id, tenant.UsuarioId, tenant.Ip);
        await auditoria.RegistrarAsync("ALTERNAR_UNIDADE", tenant.TenantId, unidade.Id, tenant.UsuarioId,
            new { de = anterior, para = unidade.Id });

        TempData["Sucesso"] = $"Unidade alterada para {unidade.Nome}.";
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Home");
    }
}
