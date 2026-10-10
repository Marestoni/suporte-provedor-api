using SuporteProvedor.Api.Application;
using SuporteProvedor.Api.Application.Agent;
using SuporteProvedor.Api.Domain;
using SuporteProvedor.Api.Infrastructure;

namespace SuporteProvedor.Api;

public record IdentificarRequest(string? Cpf, DateOnly? DataNascimento);
public record MensagemRequest(string? Texto);

public static class ConversasEndpoints
{
    private const int MaxTextoMensagem = 1000;

    private static string? Bearer(HttpRequest r) =>
        r.Headers.Authorization.ToString() is { Length: > 7 } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : null;

    /// <summary>Erro em ProblemDetails (RFC 7807); a mensagem para o cliente vai em "detail".</summary>
    private static IResult Erro(int status, string detail) => Results.Problem(detail: detail, statusCode: status);

    public static void MapConversasEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/conversas");

        g.MapPost("/", async (Db db, CancellationToken ct) =>
        {
            var id = await db.CriarConversaAsync("web", ct);
            return Results.Created($"/api/conversas/{id}", new { id });
        });

        g.MapPost("/{id:guid}/identificar", async (Guid id, IdentificarRequest req, HttpContext http, SessaoService sessao, CancellationToken ct) =>
        {
            var ip = ChaveIp.De(http.Connection.RemoteIpAddress);
            var (resultado, ident) = await sessao.IdentificarAsync(id, req.Cpf, req.DataNascimento, ip, ct);
            return resultado switch
            {
                ResultadoIdentificacao.Ok => Results.Ok(new { token = ident!.Token, nome = ident.PrimeiroNome, situacao = ident.Situacao }),
                ResultadoIdentificacao.Bloqueado => Erro(StatusCodes.Status429TooManyRequests,
                    "Muitas tentativas. Inicie um novo atendimento mais tarde ou fale com a central."),
                ResultadoIdentificacao.ConversaInvalida => Results.NotFound(),
                // Mensagem genérica: não revela se o CPF existe.
                _ => Erro(StatusCodes.Status401Unauthorized,
                    "Não foi possível confirmar seus dados. Confira o CPF e a data de nascimento."),
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
            SessaoService sessao, AgentService agente, ConversaLock trava, ILogger<AgentService> log, CancellationToken ct) =>
        {
            var texto = req.Texto?.Trim();
            if (string.IsNullOrEmpty(texto) || texto.Length > MaxTextoMensagem)
                return Erro(StatusCodes.Status400BadRequest, $"Mensagem vazia ou maior que {MaxTextoMensagem} caracteres.");

            // Uma requisição por vez por conversa (evita intercalar turnos/tool_use no histórico).
            // O lock vem ANTES da leitura/autenticação: assim a conversa é lida uma única vez, já com o status
            // atualizado pela requisição anterior (que pode ter transferido/encerrado). O id é um UUID não adivinhável.
            // 429 = "ocupado, tente já já"; 409 fica reservado para "atendimento encerrado".
            using var guarda = trava.TentarEntrar(id);
            if (guarda is null)
                return Erro(StatusCodes.Status429TooManyRequests, "Aguarde a resposta anterior antes de enviar outra mensagem.");

            var auth = await sessao.AutenticarAsync(id, Bearer(http), ct);
            if (auth is null) return Results.Unauthorized();
            var (conversa, cliente) = auth.Value;
            if (conversa.Status != "aberta")
                return Erro(StatusCodes.Status409Conflict, "Este atendimento foi encerrado.");

            try
            {
                var r = await agente.ResponderAsync(id, cliente, texto, ct);
                return Results.Ok(new { resposta = r.Texto, status = r.StatusConversa, protocolo = r.Protocolo });
            }
            catch (AgentIndisponivelException e)
            {
                log.LogWarning("Agente indisponível: {Motivo}", e.Message);
                return Erro(StatusCodes.Status503ServiceUnavailable,
                    "O assistente está indisponível no momento. Tente novamente em instantes.");
            }
        });
    }
}
