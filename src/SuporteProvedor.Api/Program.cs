using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
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
// Variáveis de ambiente e linha de comando continuam com a última palavra (as fontes acima entraram depois delas).
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

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
builder.Services.AddSingleton<ConversaLock>();

builder.Services.AddHostedService<LimpezaService>();

// Atrás de proxy/PaaS o RemoteIpAddress é o do proxy (todos os clientes dividiriam o mesmo limite por IP).
// Preferido: Proxy:ProxiesConhecidos = IPs do proxy; só X-Forwarded-For vindo deles é aceito (não dá para forjar o IP).
// Alternativa: Proxy:ConfiarEmQualquerOrigem=true aceita o cabeçalho de QUALQUER origem. Só use se a API for inalcançável
// sem passar pelo proxy; do contrário o cliente forja o IP e anula os limites por IP.
var proxiesConhecidos = builder.Configuration.GetSection("Proxy:ProxiesConhecidos").Get<string[]>() ?? [];
var confiaEmQualquerOrigem = builder.Configuration.GetValue<bool>("Proxy:ConfiarEmQualquerOrigem");
var usaForwardedHeaders = proxiesConhecidos.Length > 0 || confiaEmQualquerOrigem;
if (usaForwardedHeaders)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var entrada in proxiesConhecidos)
        {
            // Aceita IP único ("10.0.0.5") ou faixa CIDR ("10.0.0.0/8"); valor inválido falha na subida com mensagem clara.
            if (entrada.Contains('/') && System.Net.IPNetwork.TryParse(entrada, out var rede)) o.KnownIPNetworks.Add(rede);
            else if (System.Net.IPAddress.TryParse(entrada, out var ip)) o.KnownProxies.Add(ip);
            else throw new InvalidOperationException($"Proxy:ProxiesConhecidos contém valor inválido: '{entrada}' (use IP ou CIDR).");
        }
    });

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

if (string.IsNullOrEmpty(app.Configuration["Seguranca:ChaveHashCpf"]))
    app.Logger.LogWarning("Seguranca:ChaveHashCpf não configurada: usando chave efêmera para o hash de CPF " +
                          "(os contadores de tentativa zeram a cada reinício e não são compartilhados entre instâncias).");

if (usaForwardedHeaders) app.UseForwardedHeaders();
// Corpo/JSON inválido do cliente (BadHttpRequestException) é 400, não 500; o resto segue como 500 em ProblemDetails.
app.UseExceptionHandler(erros => erros.Run(async ctx =>
{
    var erro = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = erro is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
    var detail = status == StatusCodes.Status400BadRequest ? "Requisição inválida. Confira os dados enviados." : null;
    await Results.Problem(detail: detail, statusCode: status).ExecuteAsync(ctx);
}));
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
