using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Estoque.Web.Infrastructure;

/// <summary>Banco grava UTC; exibição em America/Sao_Paulo.</summary>
public static class Tempo
{
    private static readonly TimeZoneInfo Fuso = CarregarFuso();

    private static TimeZoneInfo CarregarFuso()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília");
    }

    public static DateTime Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Fuso);

    public static DateOnly Hoje => DateOnly.FromDateTime(Local(DateTime.UtcNow));

    public static string Formatar(DateTime? utc) => utc is null ? "—" : Local(utc.Value).ToString("dd/MM/yyyy HH:mm");
}

public static class Formatos
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    public static string Moeda(decimal v) => v.ToString("C", PtBr);
    public static string Numero(decimal v) => v.ToString("N2", PtBr);
    public static string Data(DateOnly d) => d.ToString("dd/MM/yyyy", PtBr);
}

/// <summary>
/// Aceita "10,50" (pt-BR) e "10.50" (input type=number / invariante). Sem isso, em pt-BR
/// "10.50" seria lido como 1050 (ponto é separador de milhar).
/// </summary>
public sealed class DecimalFlexivelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext ctx)
    {
        var valor = ctx.ValueProvider.GetValue(ctx.ModelName);
        if (valor == ValueProviderResult.None) return Task.CompletedTask;
        ctx.ModelState.SetModelValue(ctx.ModelName, valor);

        var texto = valor.FirstValue?.Trim().Replace("R$", "").Replace(" ", "");
        if (string.IsNullOrEmpty(texto))
        {
            if (Nullable.GetUnderlyingType(ctx.ModelType) is null)
                ctx.ModelState.TryAddModelError(ctx.ModelName, "Informe um valor.");
            return Task.CompletedTask;
        }

        // Com vírgula: formato brasileiro (pontos = milhar). Sem vírgula: ponto é decimal.
        var normalizado = texto.Contains(',') ? texto.Replace(".", "").Replace(',', '.') : texto;
        if (decimal.TryParse(normalizado, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            ctx.Result = ModelBindingResult.Success(d);
        else
            ctx.ModelState.TryAddModelError(ctx.ModelName, "Valor numérico inválido.");
        return Task.CompletedTask;
    }
}

public sealed class DecimalFlexivelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context) =>
        context.Metadata.ModelType == typeof(decimal) || context.Metadata.ModelType == typeof(decimal?)
            ? new DecimalFlexivelBinder()
            : null;
}

public static class Documentos
{
    public static string SomenteDigitos(string? s) => new((s ?? "").Where(char.IsAsciiDigit).ToArray());

    public static bool CnpjValido(string digitos)
    {
        if (digitos.Length != 14 || digitos.Distinct().Count() == 1) return false;
        int[] p1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] p2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        return Digito(digitos, p1) == digitos[12] - '0' && Digito(digitos, p2) == digitos[13] - '0';

        static int Digito(string d, int[] pesos)
        {
            var soma = 0;
            for (var i = 0; i < pesos.Length; i++) soma += (d[i] - '0') * pesos[i];
            var r = soma % 11;
            return r < 2 ? 0 : 11 - r;
        }
    }

    public static string Formatar(string? digitos) => digitos switch
    {
        { Length: 14 } d => $"{d[..2]}.{d[2..5]}.{d[5..8]}/{d[8..12]}-{d[12..]}",
        null or "" => "—",
        var d => d,
    };
}

public static class Senhas
{
    // Sem caracteres ambíguos (0/O, 1/l/I): a senha é ditada ou copiada de papel
    private const string Alfabeto = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Senha temporária no formato xxxx-xxxx-xxxx-xxxx (19 caracteres, ~92 bits).</summary>
    public static string GerarTemporaria() =>
        string.Join('-', Enumerable.Range(0, 4).Select(_ =>
            System.Security.Cryptography.RandomNumberGenerator.GetString(Alfabeto, 4)));
}
