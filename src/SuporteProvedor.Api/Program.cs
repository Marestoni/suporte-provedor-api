using System.Threading.RateLimiting;
using Npgsql;
using SuporteProvedor.Api;
using SuporteProvedor.Api.Application;
using SuporteProvedor.Api.Application.Agent;
using SuporteProvedor.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Segredos locais (connection string, chaves) também fora de Development, p.ex. quando o Visual Studio
// inicia sem ASPNETCORE_ENVIRONMENT. Em produção não existe esse arquivo; use variáveis de ambiente.
builder.Configuration.AddUserSecrets<Program>(optional: true);
// Reserva local (no .gitignore): appsettings.Local.json na pasta do projeto, para quando o user-secrets não carrega.
builder.Configuration.AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "appsettings.Local.json"), optional: true, reloadOnChange: false);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Default não configurada. Use dotnet user-secrets ou appsettings.Local.json. " +
        $"Ambiente={builder.Environment.EnvironmentName}; ContentRoot={builder.Environment.ContentRootPath}; " +
        $"APPDATA={Environment.GetEnvironmentVariable("APPDATA")}.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<AgentTools>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<SessaoService>();

builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection("OpenRouter"));
switch (builder.Configuration["Agent:Provider"]?.ToLowerInvariant())
{
    case "fake":
        builder.Services.AddSingleton<IAgentModel, FakeModel>();
        break;
    case "anthropic":
        builder.Services.AddHttpClient<IAgentModel, AnthropicModel>(c => c.Timeout = TimeSpan.FromSeconds(60));
        break;
    default: // openrouter
        builder.Services.AddHttpClient<IAgentModel, OpenRouterModel>(c => c.Timeout = TimeSpan.FromSeconds(60));
        break;
}

var origens = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origens).AllowAnyHeader().WithMethods("GET", "POST")));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
app.UseCors();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapConversasEndpoints();

app.Run();

public partial class Program { }
