using System.Security.Claims;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Estoque.Web.Infrastructure.Security;

/// <summary>Policies da matriz de permissões (PLANO.md 7.3).</summary>
public static class Policies
{
    public const string PodeEditar = "PodeEditar";      // admin, operacional
    public const string SomenteAdmin = "SomenteAdmin";  // admin
}

public static class Autenticacao
{
    public static ClaimsPrincipal MontarPrincipal(Usuario u, Unidade unidade, bool trocarSenha)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Name, u.Nome),
            new(ClaimTypes.Email, u.Email),
            new(ClaimTypes.Role, u.Papel),
            new(ClaimsApp.TenantId, u.TenantId.ToString()),
            new(ClaimsApp.UnidadeId, unidade.Id.ToString()),
            new(ClaimsApp.UnidadeNome, unidade.Nome),
        };
        if (trocarSenha) claims.Add(new Claim(ClaimsApp.TrocarSenha, "1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static Task EntrarAsync(HttpContext http, ClaimsPrincipal principal) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });

    /// <summary>Enquanto trocar_senha estiver marcado, só a troca de senha e o logout ficam acessíveis.</summary>
    public static IApplicationBuilder UseTrocaSenhaObrigatoria(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (http.User.HasClaim(ClaimsApp.TrocarSenha, "1"))
            {
                var path = http.Request.Path;
                var liberado = path.StartsWithSegments("/Conta/AlterarSenha", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/Conta/Logout", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/healthz")
                    || Path.HasExtension(path.Value); // arquivos estáticos (css/js) da própria tela
                if (!liberado)
                {
                    http.Response.Redirect("/Conta/AlterarSenha");
                    return;
                }
            }
            await next();
        });
}
