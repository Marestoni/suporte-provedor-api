using System.Text;
using System.Text.Json.Nodes;

namespace SuporteProvedor.Api.Application.Agent;

/// <summary>
/// Modelo roteirizado para desenvolvimento/testes sem chave da Anthropic.
/// Reage a palavras-chave chamando uma ferramenta e depois resume o resultado.
/// NÃO é inteligente: serve para exercitar o fluxo ponta a ponta.
/// </summary>
public class FakeModel : IAgentModel
{
    public Task<ModelTurn> CompleteAsync(string system, JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        var last = (JsonObject)messages[^1]!;
        var content = (JsonArray)last["content"]!;

        // Último turno é resultado de ferramenta -> resume em texto.
        if (content[0] is JsonObject b && b["type"]?.GetValue<string>() == "tool_result")
        {
            var resultado = b["content"]?.GetValue<string>() ?? "";
            return Task.FromResult(Texto($"[modelo simulado] Resultado da consulta: {resultado}"));
        }

        var texto = (content[0]?["text"]?.GetValue<string>() ?? "").ToLowerInvariant();
        (string Nome, JsonObject Input)? tool = texto switch
        {
            var t when t.Contains("segunda via") || t.Contains("boleto") || t.Contains("fatura") || t.Contains("pix")
                => ("listar_faturas", new JsonObject()),
            var t when t.Contains("incidente") || t.Contains("queda") || t.Contains("regi")
                => ("verificar_incidente", new JsonObject()),
            var t when t.Contains("internet") || t.Contains("conex") || t.Contains("lent") || t.Contains("sem sinal")
                => ("status_conexao", new JsonObject()),
            var t when t.Contains("desbloque") || t.Contains("confian")
                => ("desbloqueio_confianca", new JsonObject()),
            var t when t.Contains("visita") || t.Contains("técnic") || t.Contains("tecnic") || t.Contains("chamado")
                => ("abrir_chamado", new JsonObject
                {
                    ["descricao"] = "Cliente solicita visita técnica (modelo simulado).",
                    ["diagnostico"] = "Sem diagnóstico adicional (modelo simulado).",
                }),
            var t when t.Contains("humano") || t.Contains("atendente")
                => ("transferir_humano", new JsonObject { ["motivo"] = "Cliente pediu atendente." }),
            _ => null,
        };

        if (tool is null)
            return Task.FromResult(Texto(
                "[modelo simulado] Posso ajudar com fatura/segunda via, conexão, incidentes, desbloqueio em confiança ou visita técnica."));

        var use = new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = "toolu_fake_" + Guid.NewGuid().ToString("N")[..8],
            ["name"] = tool.Value.Nome,
            ["input"] = tool.Value.Input,
        };
        return Task.FromResult(new ModelTurn(new JsonArray(use), "tool_use"));
    }

    private static ModelTurn Texto(string t) =>
        new(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = t }), "end_turn");
}
