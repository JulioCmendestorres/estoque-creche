using System.Security.Claims;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Estoque.Web.Infrastructure.Security;

/// <summary>
/// Políticas dinâmicas: "perm:&lt;código&gt;" exige a permissão do catálogo; "master" exige o usuário master.
/// Resolvidas pelo <see cref="PoliticaPermissaoProvider"/> contra o <see cref="ITenantContext"/> (lido do banco).
/// </summary>
public static class Politicas
{
    public const string Master = "master";
    public const string Prefixo = "perm:";
    public static string De(string permissao) => Prefixo + permissao;
}

/// <summary>[Permissao(Permissoes.X)]: atalho para [Authorize(Policy = "perm:X")].</summary>
public sealed class PermissaoAttribute(string permissao) : AuthorizeAttribute(Politicas.De(permissao));

/// <summary>[SomenteMaster]: usuários, perfis e troca de unidade.</summary>
public sealed class SomenteMasterAttribute() : AuthorizeAttribute(Politicas.Master);

/// <param name="Permissao">Nulo = exige master.</param>
public sealed record PermissaoRequirement(string? Permissao) : IAuthorizationRequirement;

public sealed class PermissaoHandler(ITenantContext acesso) : AuthorizationHandler<PermissaoRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissaoRequirement req)
    {
        if (req.Permissao is null ? acesso.Master : acesso.Tem(req.Permissao))
            context.Succeed(req);
        return Task.CompletedTask;
    }
}

public sealed class PoliticaPermissaoProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string nome)
    {
        if (nome == Politicas.Master)
            return Construir(null);
        if (nome.StartsWith(Politicas.Prefixo, StringComparison.Ordinal))
        {
            var permissao = nome[Politicas.Prefixo.Length..];
            if (!Permissoes.Existe(permissao))
                throw new InvalidOperationException($"Permissão desconhecida: {permissao}");
            return Construir(permissao);
        }
        return await base.GetPolicyAsync(nome);
    }

    private static AuthorizationPolicy Construir(string? permissao) =>
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new PermissaoRequirement(permissao)).Build();
}

public static class Autenticacao
{
    /// <summary>O cookie guarda só identidade e a unidade escolhida; permissões são relidas do banco a cada requisição.</summary>
    public static ClaimsPrincipal MontarPrincipal(Usuario u, int unidadeId, bool trocarSenha)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Name, u.Nome),
            new(ClaimTypes.Email, u.Email),
            new(ClaimsApp.TenantId, u.TenantId.ToString()),
            new(ClaimsApp.UnidadeId, unidadeId.ToString()),
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
