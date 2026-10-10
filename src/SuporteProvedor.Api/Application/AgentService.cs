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
    public async Task<AgentReply> ResponderAsync(Guid conversaId, Cliente cliente, string textoUsuario, CancellationToken ct)
    {
        var historico = await db.ListarMensagensAsync(conversaId, ct);
        var mensagens = new JsonArray();
        foreach (var m in historico)
        {
            var content = m.Payload is not null
                ? JsonNode.Parse(m.Payload)!
                : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = m.Conteudo ?? "" });
            mensagens.Add(new JsonObject { ["role"] = m.Papel == "assistant" ? "assistant" : "user", ["content"] = content });
        }

        var blocoUsuario = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = textoUsuario });
        mensagens.Add(new JsonObject { ["role"] = "user", ["content"] = blocoUsuario.DeepClone() });
        await db.AdicionarMensagemAsync(conversaId, "user", textoUsuario, null, blocoUsuario.ToJsonString(), ct);

        var ctx = new ToolContext(conversaId, cliente);
        var definicoes = AgentTools.Definicoes();
        var textoFinal = new StringBuilder();

        for (var i = 0; i < options.Value.MaxIteracoes; i++)
        {
            var sistema = Prompts.Sistema(PrimeiroNome(ctx.Cliente.Nome), ctx.Cliente.Plano, ctx.Cliente.Situacao);
            var turno = await model.CompleteAsync(sistema, mensagens, definicoes, ct);

            var texto = string.Concat(turno.Content.OfType<JsonObject>()
                .Where(b => b["type"]?.GetValue<string>() == "text")
                .Select(b => b["text"]?.GetValue<string>()));
            var usos = turno.Content.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_use").ToList();

            mensagens.Add(new JsonObject { ["role"] = "assistant", ["content"] = turno.Content.DeepClone() });

            if (usos.Count == 0 || turno.StopReason != "tool_use")
            {
                await db.AdicionarMensagemAsync(conversaId, "assistant", texto, null, turno.Content.ToJsonString(), ct);
                textoFinal.Append(texto);
                break;
            }

            var resultados = new JsonArray();
            foreach (var uso in usos)
            {
                var nome = uso["name"]!.GetValue<string>();
                var input = uso["input"] as JsonObject ?? new JsonObject();
                var saida = await tools.ExecutarAsync(nome, input, ctx, ct);
                log.LogInformation("Ferramenta {Tool} executada na conversa {Conversa}", nome, conversaId);
                resultados.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = uso["id"]!.GetValue<string>(),
                    ["content"] = saida,
                });
            }

            // Assistente (com tool_use) e resultados são gravados juntos para o histórico nunca ficar incompleto.
            await db.AdicionarMensagemAsync(conversaId, "assistant", texto, usos[0]["name"]!.GetValue<string>(), turno.Content.ToJsonString(), ct);
            await db.AdicionarMensagemAsync(conversaId, "tool", null, null, resultados.ToJsonString(), ct);
            mensagens.Add(new JsonObject { ["role"] = "user", ["content"] = resultados });
        }

        if (textoFinal.Length == 0)
            textoFinal.Append("Não consegui concluir agora. Vou transferir você para um atendente.");

        var status = ctx.TransferidoParaHumano ? "transferida_humano" : "aberta";
        return new AgentReply(textoFinal.ToString(), status, ctx.ProtocoloChamado);
    }

    private static string PrimeiroNome(string nome) => nome.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? nome;
}
