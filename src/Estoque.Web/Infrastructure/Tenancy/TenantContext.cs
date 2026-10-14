using System.Security.Claims;
using Estoque.Web.Data;
using Estoque.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Infrastructure.Tenancy;

/// <summary>
/// Quem está operando, em qual tenant/unidade e com quais permissões. Tenant e usuário vêm do cookie
/// (claims); unidade, master e permissões são relidos do banco a cada requisição, então inativar um
/// usuário, trocar o perfil ou mudar a unidade vale na hora. Nunca vem de URL ou formulário.
/// </summary>
public interface ITenantContext
{
    int? TenantId { get; }
    int? UnidadeId { get; }
    int? UsuarioId { get; }
    string? Ip { get; }
    bool Master { get; }
    string? UnidadeNome { get; }
    /// <summary>"Master" ou o nome do perfil, para exibição.</summary>
    string? PerfilNome { get; }
    IReadOnlySet<string> Permissoes { get; }
    bool Tem(string permissao);
}

/// <summary>Implementação mutável e por requisição (scoped).</summary>
public sealed class TenantContext : ITenantContext
{
    private static readonly IReadOnlySet<string> Nenhuma = new HashSet<string>();
    private static readonly IReadOnlySet<string> TodasPermissoes =
        Security.Permissoes.Todas.Select(p => p.Codigo).ToHashSet();

    public int? TenantId { get; private set; }
    public int? UnidadeId { get; private set; }
    public int? UsuarioId { get; private set; }
    public string? Ip { get; private set; }
    public bool Master { get; private set; }
    public string? UnidadeNome { get; private set; }
    public string? PerfilNome { get; private set; }
    public IReadOnlySet<string> Permissoes { get; private set; } = Nenhuma;

    public bool Tem(string permissao) => Master || Permissoes.Contains(permissao);

    public void Definir(int? tenantId, int? unidadeId, int? usuarioId, string? ip)
    {
        TenantId = tenantId;
        UnidadeId = unidadeId;
        UsuarioId = usuarioId;
        Ip = ip;
    }

    public void DefinirAcesso(bool master, IEnumerable<string> permissoes, string? perfilNome, string? unidadeNome)
    {
        Master = master;
        Permissoes = master ? TodasPermissoes : permissoes.Where(Security.Permissoes.Existe).ToHashSet();
        PerfilNome = master ? "Master" : perfilNome;
        UnidadeNome = unidadeNome;
    }

    internal static int? LerInt(ClaimsPrincipal user, string tipo) =>
        int.TryParse(user.FindFirstValue(tipo), out var v) ? v : null;
}

public static class ClaimsApp
{
    public const string TenantId = "tenant_id";
    /// <summary>Unidade escolhida pelo master no seletor. Para os demais vale sempre usuarios.unidade_id.</summary>
    public const string UnidadeId = "unidade_id";
    public const string TrocarSenha = "trocar_senha";
}

public static class TenantContextMiddlewareExtensions
{
    /// <summary>
    /// Preenche o TenantContext (depois de UseAuthentication). Usuário inativo, removido ou de tenant
    /// inativo tem a sessão encerrada.
    /// </summary>
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var ctx = http.RequestServices.GetRequiredService<TenantContext>();
            var ip = http.Connection.RemoteIpAddress?.ToString();
            var user = http.User;
            if (user.Identity?.IsAuthenticated != true || Path.HasExtension(http.Request.Path.Value))
            {
                ctx.Definir(null, null, null, ip);
                await next();
                return;
            }

            ctx.Definir(TenantContext.LerInt(user, ClaimsApp.TenantId), null, TenantContext.LerInt(user, ClaimTypes.NameIdentifier), ip);
            var db = http.RequestServices.GetRequiredService<EstoqueDbContext>();
            var u = await db.Usuarios
                .Where(x => x.Id == ctx.UsuarioId && x.Ativo)
                .Select(x => new
                {
                    x.Master,
                    x.UnidadeId,
                    Perfil = x.Perfil != null ? x.Perfil.Nome : null,
                    Permissoes = x.Perfil != null && x.Perfil.Ativo
                        ? x.Perfil.Permissoes.Select(p => p.Permissao).ToList()
                        : new List<string>(),
                })
                .FirstOrDefaultAsync();
            var tenantAtivo = u is not null && await db.Tenants.AnyAsync(t => t.Ativo);
            if (u is null || !tenantAtivo)
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                http.Response.Redirect("/Conta/Login");
                return;
            }

            // Só o master escolhe a unidade (claim do seletor); os demais ficam na unidade do cadastro
            var escolhida = u.Master ? TenantContext.LerInt(user, ClaimsApp.UnidadeId) : null;
            var unidade = escolhida is null ? null
                : await db.Unidades.Where(x => x.Id == escolhida && x.Ativo).Select(x => new { x.Id, x.Nome }).FirstOrDefaultAsync();
            unidade ??= await db.Unidades.Where(x => x.Id == u.UnidadeId).Select(x => new { x.Id, x.Nome }).FirstAsync();

            ctx.Definir(ctx.TenantId, unidade.Id, ctx.UsuarioId, ip);
            ctx.DefinirAcesso(u.Master, u.Permissoes, u.Perfil, unidade.Nome);
            await next();
        });
}
