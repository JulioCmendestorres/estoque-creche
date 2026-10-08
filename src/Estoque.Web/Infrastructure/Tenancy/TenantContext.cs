using System.Security.Claims;

namespace Estoque.Web.Infrastructure.Tenancy;

/// <summary>
/// Quem está operando e em qual tenant/unidade. Vem SEMPRE do usuário autenticado
/// (claims), nunca de URL ou formulário. Sem tenant, os filtros globais não retornam nada.
/// </summary>
public interface ITenantContext
{
    int? TenantId { get; }
    int? UnidadeId { get; }
    int? UsuarioId { get; }
    string? Papel { get; }
    string? Ip { get; }
}

/// <summary>Implementação mutável e por requisição (scoped).</summary>
public sealed class TenantContext : ITenantContext
{
    public int? TenantId { get; private set; }
    public int? UnidadeId { get; private set; }
    public int? UsuarioId { get; private set; }
    public string? Papel { get; private set; }
    public string? Ip { get; private set; }

    public void Definir(int? tenantId, int? unidadeId, int? usuarioId, string? papel, string? ip)
    {
        TenantId = tenantId;
        UnidadeId = unidadeId;
        UsuarioId = usuarioId;
        Papel = papel;
        Ip = ip;
    }

    public void DefinirAPartirDe(ClaimsPrincipal user, string? ip)
    {
        Definir(
            LerInt(user, ClaimsApp.TenantId),
            LerInt(user, ClaimsApp.UnidadeId),
            LerInt(user, ClaimTypes.NameIdentifier),
            user.FindFirstValue(ClaimTypes.Role),
            ip);
    }

    private static int? LerInt(ClaimsPrincipal user, string tipo) =>
        int.TryParse(user.FindFirstValue(tipo), out var v) ? v : null;
}

public static class ClaimsApp
{
    public const string TenantId = "tenant_id";
    public const string UnidadeId = "unidade_id";
    public const string UnidadeNome = "unidade_nome";
    public const string TrocarSenha = "trocar_senha";
}

public static class TenantContextMiddlewareExtensions
{
    /// <summary>Preenche o TenantContext a partir dos claims (depois de UseAuthentication).</summary>
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var ctx = http.RequestServices.GetRequiredService<TenantContext>();
            var ip = http.Connection.RemoteIpAddress?.ToString();
            if (http.User.Identity?.IsAuthenticated == true)
                ctx.DefinirAPartirDe(http.User, ip);
            else
                ctx.Definir(null, null, null, null, ip);
            await next();
        });
}
