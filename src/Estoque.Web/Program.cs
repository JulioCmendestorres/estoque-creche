using System.Globalization;
using System.Net;
using Estoque.Web.Data;
using Estoque.Web.Data.Interceptors;
using Estoque.Web.Data.Migrator;
using Estoque.Web.Data.Seed;
using Estoque.Web.Infrastructure;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Serilog;

// Modos do mesmo binário: web (padrão) | --migrate | --healthcheck
if (args.Contains("--healthcheck"))
    return await Healthcheck.ExecutarAsync(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args.Where(a => a != "--migrate").ToArray());
    // Configuração local de desenvolvimento (gitignored): connection strings e senhas de teste
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();

    builder.Host.UseSerilog((ctx, sp, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

    if (args.Contains("--migrate"))
        return await ExecutarMigracaoAsync(builder.Configuration, builder.Environment);

    ConfigurarServicos(builder);
    var app = builder.Build();
    ConfigurarPipeline(app);
    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Aplicação encerrada por erro.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<int> ExecutarMigracaoAsync(IConfiguration cfg, IHostEnvironment env)
{
    var logger = LoggerFactory.Create(b => b.AddSerilog(Log.Logger)).CreateLogger("Migrator");
    var admin = cfg.GetConnectionString("Admin");
    if (string.IsNullOrWhiteSpace(admin))
    {
        logger.LogError("ConnectionStrings:Admin não definida.");
        return 2;
    }

    var seedOpt = new SeedOptions
    {
        AdminEmail = cfg["SEED_ADMIN_EMAIL"],
        AdminPassword = cfg["SEED_ADMIN_PASSWORD"],
        DemoData = string.Equals(cfg["SEED_DEMO_DATA"], "true", StringComparison.OrdinalIgnoreCase),
        Producao = env.IsProduction(),
    };

    var migrator = new DatabaseMigrator(new MigratorOptions
    {
        AdminConnectionString = admin,
        DatabaseName = cfg["Database:Name"] ?? "EstoqueDoacoes",
        MigrationsPath = DatabaseMigrator.LocalizarPastaMigracoes(cfg["Migrator:Path"]),
        AppDbPassword = cfg["APP_DB_PASSWORD"],
        MaintDbPassword = cfg["MAINT_DB_PASSWORD"],
        TempoMaximoEspera = TimeSpan.FromSeconds(cfg.GetValue("Migrator:EsperaSegundos", 90)),
    }, logger);

    await migrator.ExecutarAsync();
    await new Seeder(migrator.DbConnectionString, seedOpt, logger).ExecutarAsync();
    logger.LogInformation("Migração concluída.");
    return 0;
}

static void ConfigurarServicos(WebApplicationBuilder builder)
{
    var cfg = builder.Configuration;
    var s = builder.Services;

    var cs = cfg.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(cs))
        throw new InvalidOperationException("ConnectionStrings:Default não definida.");

    s.AddHttpContextAccessor();
    s.AddScoped<TenantContext>();
    s.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
    s.AddSingleton<SessionContextInterceptor>();
    s.AddDbContext<EstoqueDbContext>((sp, o) => o
        .UseSqlServer(cs, sql => sql.EnableRetryOnFailure(3))
        .AddInterceptors(sp.GetRequiredService<SessionContextInterceptor>()));

    s.AddScoped<AuditoriaService>();
    s.AddScoped<MovimentacaoService>();
    s.AddScoped<DashboardService>();
    s.AddScoped<RelatorioService>();
    s.AddSingleton<IPasswordHasher<Usuario>, PasswordHasher<Usuario>>();

    var keysPath = cfg["DataProtection:KeysPath"];
    var dp = s.AddDataProtection().SetApplicationName("Estoque.Web");
    if (!string.IsNullOrWhiteSpace(keysPath)) dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

    s.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.LoginPath = "/Conta/Login";
            o.LogoutPath = "/Conta/Logout";
            o.AccessDeniedPath = "/Conta/AcessoNegado";
            o.Cookie.Name = "estoque.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = Enum.TryParse<CookieSecurePolicy>(cfg["Cookie:SecurePolicy"], true, out var p)
                ? p : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
        });

    s.AddAuthorization();
    s.AddSingleton<IAuthorizationPolicyProvider, PoliticaPermissaoProvider>();
    s.AddScoped<IAuthorizationHandler, PermissaoHandler>();

    s.AddAntiforgery(o =>
    {
        o.Cookie.Name = "estoque.af";
        o.Cookie.SecurePolicy = Enum.TryParse<CookieSecurePolicy>(cfg["Cookie:SecurePolicy"], true, out var p)
            ? p : CookieSecurePolicy.SameAsRequest;
    });

    s.AddControllersWithViews(o =>
    {
        // [Authorize] global: só login, erro e healthz são anônimos
        o.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
        o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        o.ModelBinderProviders.Insert(0, new DecimalFlexivelBinderProvider());
    });

    s.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        // Confia apenas na rede interna do compose (padrão: faixas privadas do Docker)
        foreach (var rede in (cfg["ForwardedHeaders:RedesConfiaveis"] ?? "172.16.0.0/12").Split(',', StringSplitOptions.RemoveEmptyEntries))
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(rede.Trim()));
    });

    s.AddHealthChecks();
}

static void ConfigurarPipeline(WebApplication app)
{
    var ptBr = new CultureInfo("pt-BR");
    app.UseRequestLocalization(new RequestLocalizationOptions
    {
        DefaultRequestCulture = new RequestCulture(ptBr),
        SupportedCultures = [ptBr],
        SupportedUICultures = [ptBr],
    });

    app.UseForwardedHeaders();

    if (!app.Environment.IsDevelopment())
        app.UseExceptionHandler("/Home/Erro");
    app.UseStatusCodePagesWithReExecute("/Home/Status", "?codigo={0}");

    app.UseSerilogRequestLogging();
    app.UseRouting();
    app.UseAuthentication();
    app.UseTenantContext();
    app.UseTrocaSenhaObrigatoria();
    app.UseAuthorization();

    app.MapHealthChecks("/healthz").AllowAnonymous();
    app.MapStaticAssets();
    app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();
}

/// <summary>--healthcheck: a imagem aspnet não tem curl/wget; o próprio binário consulta /healthz.</summary>
static class Healthcheck
{
    public static async Task<int> ExecutarAsync(string[] args)
    {
        var porta = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
        var url = args.SkipWhile(a => a != "--url").Skip(1).FirstOrDefault() ?? $"http://localhost:{porta}/healthz";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var resp = await http.GetAsync(url);
            Console.WriteLine($"{url} -> {(int)resp.StatusCode}");
            return resp.StatusCode == HttpStatusCode.OK ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{url} -> {ex.Message}");
            return 1;
        }
    }
}

public partial class Program;
