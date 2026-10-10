using System.Text.Json.Nodes;
using SuporteProvedor.Api.Application;
using SuporteProvedor.Api.Application.Agent;
using SuporteProvedor.Api.Infrastructure;

namespace SuporteProvedor.Api;

public record IdentificarRequest(string? Cpf, DateOnly? DataNascimento);
public record MensagemRequest(string? Texto);

public static class ConversasEndpoints
{
    private const int MaxTextoMensagem = 1000;

    private static string? Bearer(HttpRequest r) =>
        r.Headers.Authorization.ToString() is { Length: > 7 } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : null;

    public static void MapConversasEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/conversas");

        g.MapPost("/", async (Db db, CancellationToken ct) =>
        {
            var id = await db.CriarConversaAsync("web", ct);
            return Results.Created($"/api/conversas/{id}", new { id });
        });

        g.MapPost("/{id:guid}/identificar", async (Guid id, IdentificarRequest req, SessaoService sessao, CancellationToken ct) =>
        {
            var (resultado, ident) = await sessao.IdentificarAsync(id, req.Cpf, req.DataNascimento, ct);
            return resultado switch
            {
                ResultadoIdentificacao.Ok => Results.Ok(new { token = ident!.Token, nome = ident.PrimeiroNome, situacao = ident.Situacao }),
                ResultadoIdentificacao.Bloqueado => Results.Json(
                    new { erro = "Muitas tentativas. Inicie um novo atendimento mais tarde ou fale com a central." },
                    statusCode: StatusCodes.Status429TooManyRequests),
                ResultadoIdentificacao.ConversaInvalida => Results.NotFound(),
                // Mensagem genérica: não revela se o CPF existe.
                _ => Results.Json(new { erro = "Não foi possível confirmar seus dados. Confira o CPF e a data de nascimento." },
                    statusCode: StatusCodes.Status401Unauthorized),
            };
        });

        g.MapGet("/{id:guid}/mensagens", async (Guid id, HttpRequest http, SessaoService sessao, Db db, CancellationToken ct) =>
        {
            if (await sessao.AutenticarAsync(id, Bearer(http), ct) is null) return Results.Unauthorized();
            var msgs = await db.ListarMensagensAsync(id, ct);
            var visiveis = msgs
                .Where(m => m.Papel is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Conteudo))
                .Select(m => new { papel = m.Papel, texto = m.Conteudo });
            return Results.Ok(visiveis);
        });

        g.MapPost("/{id:guid}/mensagens", async (Guid id, MensagemRequest req, HttpRequest http,
            SessaoService sessao, AgentService agente, ILogger<AgentService> log, CancellationToken ct) =>
        {
            var auth = await sessao.AutenticarAsync(id, Bearer(http), ct);
            if (auth is null) return Results.Unauthorized();
            var (conversa, cliente) = auth.Value;
            if (conversa.Status != "aberta")
                return Results.Json(new { erro = "Este atendimento foi encerrado." }, statusCode: StatusCodes.Status409Conflict);

            var texto = req.Texto?.Trim();
            if (string.IsNullOrEmpty(texto) || texto.Length > MaxTextoMensagem)
                return Results.BadRequest(new { erro = $"Mensagem vazia ou maior que {MaxTextoMensagem} caracteres." });

            try
            {
                var r = await agente.ResponderAsync(id, cliente, texto, ct);
                return Results.Ok(new { resposta = r.Texto, status = r.StatusConversa, protocolo = r.Protocolo });
            }
            catch (AgentIndisponivelException e)
            {
                log.LogWarning("Agente indisponível: {Motivo}", e.Message);
                return Results.Json(new { erro = "O assistente está indisponível no momento. Tente novamente em instantes." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
