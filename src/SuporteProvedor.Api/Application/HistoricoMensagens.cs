using System.Text.Json.Nodes;

namespace SuporteProvedor.Api.Application;

/// <summary>
/// Prepara o histórico da conversa para o modelo. Os provedores rejeitam (400) históricos malformados:
/// turno de assistente vazio, tool_use sem o tool_result correspondente, tool_result órfão ou conversa que
/// não começa por uma mensagem do usuário. Isso pode acontecer por falha no meio do loop, truncamento ou
/// dados antigos; aqui o histórico é saneado e limitado às últimas mensagens (custo e janela de contexto).
/// </summary>
public static class HistoricoMensagens
{
    public const int MaxMensagens = 40;

    public static JsonArray Sanear(JsonArray entrada, int max = MaxMensagens)
    {
        var msgs = entrada.OfType<JsonObject>().ToList();
        if (msgs.Count > max) msgs = msgs.Skip(msgs.Count - max).ToList();

        var saida = new List<JsonObject>();
        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (Papel(m) == "assistant")
            {
                var usos = IdsToolUse(m);
                if (usos.Count == 0 && !TemTexto(m)) continue; // turno vazio

                if (usos.Count > 0)
                {
                    var prox = i + 1 < msgs.Count ? msgs[i + 1] : null;
                    var resultados = prox is not null && Papel(prox) == "user" ? IdsToolResult(prox) : [];
                    if (!usos.All(resultados.Contains)) continue; // tool_use sem resultado
                }
                saida.Add(m);
            }
            else
            {
                if (IdsToolResult(m).Count > 0)
                {
                    var anterior = saida.LastOrDefault();
                    if (anterior is null || Papel(anterior) != "assistant" || !IdsToolResult(m).All(IdsToolUse(anterior).Contains))
                        continue; // tool_result órfão
                }
                saida.Add(m);
            }
        }

        // A conversa precisa começar por uma mensagem de texto do usuário.
        while (saida.Count > 0 && !(Papel(saida[0]) == "user" && IdsToolResult(saida[0]).Count == 0))
            saida.RemoveAt(0);

        return new JsonArray(saida.Select(m => (JsonNode)m.DeepClone()).ToArray());
    }

    private static string Papel(JsonObject m) => m["role"]?.GetValue<string>() ?? "";

    private static IEnumerable<JsonObject> Blocos(JsonObject m) =>
        (m["content"] as JsonArray ?? []).OfType<JsonObject>();

    private static bool TemTexto(JsonObject m) =>
        Blocos(m).Any(b => b["type"]?.GetValue<string>() == "text" && !string.IsNullOrWhiteSpace(b["text"]?.GetValue<string>()));

    private static HashSet<string> IdsToolUse(JsonObject m) =>
        Blocos(m).Where(b => b["type"]?.GetValue<string>() == "tool_use").Select(b => b["id"]?.GetValue<string>() ?? "").ToHashSet();

    private static HashSet<string> IdsToolResult(JsonObject m) =>
        Blocos(m).Where(b => b["type"]?.GetValue<string>() == "tool_result").Select(b => b["tool_use_id"]?.GetValue<string>() ?? "").ToHashSet();
}
