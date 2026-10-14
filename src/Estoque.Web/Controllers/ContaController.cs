using System.Security.Claims;
using Estoque.Web.Data;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Controllers;

public class ContaController(
    EstoqueDbContext db,
    TenantContext tenant,
    AuditoriaService auditoria,
    IPasswordHasher<Usuario> hasher,
    ILogger<ContaController> logger) : Controller
{
    private const string ErroLoginGenerico = "E-mail ou senha inválidos.";

    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        return View(new LoginInput { ReturnUrl = returnUrl });
    }

    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> Login(LoginInput input)
    {
        if (!ModelState.IsValid) return View(input);

        var email = input.Email.Trim().ToLowerInvariant();
        var usuario = await db.Usuarios.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email);
        var tenantAtivo = usuario is not null &&
            await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == usuario.TenantId && t.Ativo);

        var senhaOk = PasswordVerificationResult.Failed;
        if (usuario is not null)
            senhaOk = hasher.VerifyHashedPassword(usuario, usuario.SenhaHash, input.Senha);
        else
            hasher.HashPassword(null!, input.Senha); // custo equivalente: não revela se o e-mail existe

        if (usuario is null || !usuario.Ativo || !tenantAtivo || senhaOk == PasswordVerificationResult.Failed)
        {
            if (usuario is not null)
            {
                tenant.Definir(usuario.TenantId, null, null, tenant.Ip);
                usuario.TentativasFalhasLogin++;
                await db.SaveChangesAsync();
            }
            await auditoria.RegistrarAsync("LOGIN_FALHA", usuario?.TenantId, null, null, new { email });
            logger.LogWarning("Falha de login para {Email}", email);
            ModelState.AddModelError(string.Empty, ErroLoginGenerico);
            return View(input);
        }

        tenant.Definir(usuario.TenantId, null, usuario.Id, tenant.Ip);
        // Usuário comum entra sempre na unidade do cadastro; o master, nela ou (se inativa) na primeira ativa
        var unidade = await db.Unidades.FirstOrDefaultAsync(u => u.Id == usuario.UnidadeId && u.Ativo);
        if (unidade is null && usuario.Master)
            unidade = await db.Unidades.Where(u => u.Ativo)
                .OrderBy(u => u.Tipo == TiposUnidade.Matriz ? 0 : 1).ThenBy(u => u.Nome).FirstOrDefaultAsync();
        if (unidade is null)
        {
            ModelState.AddModelError(string.Empty, "A unidade do seu cadastro está inativa. Procure o responsável pelo sistema.");
            return View(input);
        }

        if (senhaOk == PasswordVerificationResult.SuccessRehashNeeded)
            usuario.SenhaHash = hasher.HashPassword(usuario, input.Senha);
        usuario.UltimoLoginEm = DateTime.UtcNow;
        usuario.TentativasFalhasLogin = 0;
        tenant.Definir(usuario.TenantId, unidade.Id, usuario.Id, tenant.Ip);
        await db.SaveChangesAsync();

        await Autenticacao.EntrarAsync(HttpContext, Autenticacao.MontarPrincipal(usuario, unidade.Id, usuario.TrocarSenha));
        await auditoria.RegistrarAsync("LOGIN", usuario.TenantId, unidade.Id, usuario.Id);

        if (usuario.TrocarSenha) return RedirectToAction(nameof(AlterarSenha));
        return Url.IsLocalUrl(input.ReturnUrl) ? LocalRedirect(input.ReturnUrl) : RedirectToAction("Index", "Home");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await auditoria.RegistrarAsync("LOGOUT", tenant.TenantId, tenant.UnidadeId, tenant.UsuarioId);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult AlterarSenha() => View(new AlterarSenhaInput());

    [HttpPost]
    public async Task<IActionResult> AlterarSenha(AlterarSenhaInput input)
    {
        if (!ModelState.IsValid) return View(input);

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == tenant.UsuarioId);
        if (usuario is null) return NotFound();

        if (hasher.VerifyHashedPassword(usuario, usuario.SenhaHash, input.SenhaAtual) == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError(nameof(input.SenhaAtual), "Senha atual incorreta.");
            return View(input);
        }
        if (input.NovaSenha == input.SenhaAtual)
        {
            ModelState.AddModelError(nameof(input.NovaSenha), "A nova senha deve ser diferente da atual.");
            return View(input);
        }

        usuario.SenhaHash = hasher.HashPassword(usuario, input.NovaSenha);
        usuario.TrocarSenha = false;
        await db.SaveChangesAsync();
        await auditoria.RegistrarAsync("ALTERAR_SENHA", tenant.TenantId, tenant.UnidadeId, tenant.UsuarioId);

        await Autenticacao.EntrarAsync(HttpContext, Autenticacao.MontarPrincipal(usuario, tenant.UnidadeId!.Value, trocarSenha: false));
        TempData["Sucesso"] = "Senha alterada com sucesso.";
        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous]
    public IActionResult AcessoNegado() => View();

    // Usado pelo layout (nome exibido no avatar)
    public static string Iniciais(ClaimsPrincipal user)
    {
        var nome = user.Identity?.Name ?? "?";
        var partes = nome.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(partes.Take(2).Select(p => char.ToUpperInvariant(p[0])));
    }
}
