using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SuporteProvedor.Api.Application.Agent;
using SuporteProvedor.Api.Domain;
using SuporteProvedor.Api.Infrastructure;

namespace SuporteProvedor.Api.Application;

public record AgentReply(string Texto, string StatusConversa, string? Protocolo);

/// <summary>Loop de tool use: modelo -> ferramentas (com o cliente da sessão) -> modelo, até resposta final.</summary>
public class AgentService(Db db, IAgentModel model, AgentTools tools, IOptions<AgentOptions> options, ILogger<AgentService> log)
{
    public const string TextoSemResposta = "Não consegui responder agora. Pode reformular a pergunta ou pedir para falar com um atendente?";

    public async Task<AgentReply> ResponderAsync(Guid conversaId, Cliente cliente, string textoUsuario, CancellationToken ct)
    {
        var mensagens = HistoricoMensagens.Sanear(await CarregarHistoricoAsync(conversaId, ct));

        var blocoUsuario = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = textoUsuario });
        mensagens.Add(new JsonObject { ["role"] = "user", ["content"] = blocoUsuario.DeepClone() });
        // A mensagem do usuário só é gravada junto com o primeiro turno concluído: se o modelo falhar (503), nada fica
        // no histórico e o reenvio do mesmo texto não vira duas mensagens iguais seguidas.
        var pendentes = new List<(string Papel, string? Conteudo, string? Ferramenta, string? PayloadJson)>
        {
            ("user", textoUsuario, null, blocoUsuario.ToJsonString()),
        };

        var ctx = new ToolContext(conversaId, cliente);
        var definicoes = AgentTools.Definicoes();
        var textoFinal = "";
        var turnosVazios = 0;

        for (var i = 0; i < options.Value.MaxIteracoes; i++)
        {
            var sistema = Prompts.Sistema(PrimeiroNome(ctx.Cliente.Nome), ctx.Cliente.Plano, ctx.Cliente.Situacao);
            var turno = await model.CompleteAsync(sistema, mensagens, definicoes, ct);

            var texto = string.Concat(turno.Content.OfType<JsonObject>()
                .Where(b => b["type"]?.GetValue<string>() == "text")
                .Select(b => b["text"]?.GetValue<string>()));
            var usos = turno.Content.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_use").ToList();

            // Há tool_use => SEMPRE fecha o par com um tool_result (um tool_use sem resultado deixaria o histórico
            // inválido e a conversa quebrada). Se a resposta foi truncada (stop_reason != tool_use, ex.: max_tokens)
            // o input da ferramenta pode estar incompleto: não executa, devolve erro para o modelo refazer a chamada.
            var truncado = turno.StopReason != "tool_use";
            if (usos.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(texto))
                {
                    // Turno vazio (ex.: modelo de raciocínio sem texto): não persiste e tenta mais uma vez.
                    log.LogWarning("Turno vazio do modelo na conversa {Conversa} (stop={Stop})", conversaId, turno.StopReason);
                    if (++turnosVazios > 1) break;
                    continue;
                }
                pendentes.Add(("assistant", texto, null, turno.Content.ToJsonString()));
                await db.AdicionarMensagensAsync(conversaId, pendentes, ct);
                pendentes.Clear();
                textoFinal = texto;
                break;
            }

            mensagens.Add(new JsonObject { ["role"] = "assistant", ["content"] = turno.Content.DeepClone() });

            // Ferramentas têm efeito colateral (desbloqueio, chamado, transferência) e o resultado precisa ser
            // gravado junto: depois que o modelo decidiu agir, o passo termina mesmo que o cliente feche a aba
            // (CancellationToken.None). Só a chamada ao modelo respeita o cancelamento da requisição.
            var resultados = new JsonArray();
            foreach (var uso in usos)
            {
                var nome = uso["name"]!.GetValue<string>();
                var input = uso["input"] as JsonObject ?? new JsonObject();
                string saida;
                if (truncado)
                {
                    log.LogWarning("tool_use {Tool} truncado (stop={Stop}) na conversa {Conversa}: não executado", nome, turno.StopReason, conversaId);
                    saida = "{\"erro\":\"Sua resposta foi cortada antes de terminar a chamada da ferramenta. Chame a ferramenta novamente.\"}";
                }
                else
                {
                    saida = await tools.ExecutarAsync(nome, input, ctx, CancellationToken.None);
                    log.LogInformation("Ferramenta {Tool} executada na conversa {Conversa}", nome, conversaId);
                }
                resultados.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = uso["id"]!.GetValue<string>(),
                    ["content"] = saida,
                });
            }

            // Assistente (com tool_use) e resultados gravados na MESMA transação: o histórico nunca fica incompleto.
            pendentes.Add(("assistant", texto, usos[0]["name"]!.GetValue<string>(), turno.Content.ToJsonString()));
            pendentes.Add(("tool", null, null, resultados.ToJsonString()));
            await db.AdicionarMensagensAsync(conversaId, pendentes, CancellationToken.None);
            pendentes.Clear();
            mensagens.Add(new JsonObject { ["role"] = "user", ["content"] = resultados });
        }

        // Terminou sem nenhum turno gravado (turnos vazios): registra ao menos a mensagem do usuário, para auditoria.
        if (pendentes.Count > 0) await db.AdicionarMensagensAsync(conversaId, pendentes, CancellationToken.None);

        // Sem texto final (limite de iterações ou turnos vazios): não promete nada que não aconteceu.
        if (textoFinal.Length == 0) textoFinal = TextoSemResposta;

        var status = ctx.TransferidoParaHumano ? "transferida_humano" : "aberta";
        return new AgentReply(textoFinal, status, ctx.ProtocoloChamado);
    }

    private async Task<JsonArray> CarregarHistoricoAsync(Guid conversaId, CancellationToken ct)
    {
        var mensagens = new JsonArray();
        foreach (var m in await db.ListarMensagensAsync(conversaId, ct))
        {
            var content = m.Payload is not null
                ? JsonNode.Parse(m.Payload)!
                : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = m.Conteudo ?? "" });
            mensagens.Add(new JsonObject { ["role"] = m.Papel == "assistant" ? "assistant" : "user", ["content"] = content });
        }
        return mensagens;
    }

    private static string PrimeiroNome(string nome) => nome.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? nome;
}
