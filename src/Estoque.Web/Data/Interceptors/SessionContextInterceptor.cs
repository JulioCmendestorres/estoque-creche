using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Estoque.Web.Data.Interceptors;

/// <summary>
/// A cada abertura de conexão, grava usuario_id, tenant_id, unidade_id e ip no
/// SESSION_CONTEXT do SQL Server. Os triggers de auditoria leem esses valores.
/// Sempre sobrescreve (inclusive com NULL): nunca assume que o pool limpou a sessão.
/// </summary>
public sealed class SessionContextInterceptor : DbConnectionInterceptor
{
    private const string Sql = """
        EXEC sp_set_session_context @key = N'usuario_id', @value = @usuario_id;
        EXEC sp_set_session_context @key = N'tenant_id',  @value = @tenant_id;
        EXEC sp_set_session_context @key = N'unidade_id', @value = @unidade_id;
        EXEC sp_set_session_context @key = N'ip',         @value = @ip;
        """;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = Montar(connection, eventData);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = Montar(connection, eventData);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand Montar(DbConnection connection, ConnectionEndEventData eventData)
    {
        var tenant = (eventData.Context as EstoqueDbContext)?.Tenant;
        var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        Adicionar(cmd, "@usuario_id", SqlDbType.Int, tenant?.UsuarioId);
        Adicionar(cmd, "@tenant_id", SqlDbType.Int, tenant?.TenantId);
        Adicionar(cmd, "@unidade_id", SqlDbType.Int, tenant?.UnidadeId);
        Adicionar(cmd, "@ip", SqlDbType.VarChar, Truncar(tenant?.Ip, 45));
        return cmd;
    }

    private static void Adicionar(DbCommand cmd, string nome, SqlDbType tipo, object? valor)
    {
        var p = new SqlParameter(nome, tipo) { Value = valor ?? DBNull.Value };
        if (tipo == SqlDbType.VarChar) p.Size = 45;
        cmd.Parameters.Add(p);
    }

    private static string? Truncar(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;
}
