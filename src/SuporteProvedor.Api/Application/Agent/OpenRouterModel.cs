using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SuporteProvedor.Api.Application.Agent;

public class OpenRouterOptions
{
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = "anthropic/claude-haiku-5.5";
}

/// <summary>
/// Adaptador do OpenRouter (API compatível com OpenAI chat/completions) para o formato interno
/// de blocos da Messages API. Traduz mensagens/ferramentas na ida e a resposta na volta.
/// </summary>
public class OpenRouterModel(HttpClient http, IOptions<OpenRouterOptions> opt, IOptions<AgentOptions> agent) : IAgentModel
{
    public async Task<ModelTurn> CompleteAsync(string system, JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        var apiKey = opt.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AgentIndisponivelException("Chave do OpenRouter não configurada.");

        var body = new JsonObject
        {
            ["model"] = opt.Value.Model,
            ["max_tokens"] = agent.Value.MaxTokens,
            ["messages"] = ToOpenAiMessages(system, messages),
            ["tools"] = ToOpenAiTools(tools),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{opt.Value.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new AgentIndisponivelException($"OpenRouter respondeu {(int)res.StatusCode}: {text[..Math.Min(text.Length, 300)]}");

        var choice = JsonNode.Parse(text)?["choices"]?[0]
            ?? throw new AgentIndisponivelException("Resposta do OpenRouter sem 'choices'.");
        return ToModelTurn(choice["message"]!.AsObject(), choice["finish_reason"]?.GetValue<string>());
    }

    // ---------- Anthropic blocks -> OpenAI ----------
    internal static JsonArray ToOpenAiMessages(string system, JsonArray messages)
    {
        var result = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system });

        foreach (var m in messages.OfType<JsonObject>())
        {
            var role = m["role"]!.GetValue<string>();
            var blocks = m["content"] as JsonArray ?? [];

            if (role == "assistant")
            {
                var text = string.Concat(blocks.OfType<JsonObject>()
                    .Where(b => b["type"]?.GetValue<string>() == "text").Select(b => b["text"]?.GetValue<string>()));
                var calls = new JsonArray();
                foreach (var b in blocks.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_use"))
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = b["id"]!.GetValue<string>(),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = b["name"]!.GetValue<string>(),
                            ["arguments"] = (b["input"] ?? new JsonObject()).ToJsonString(),
                        },
                    });
                }
                var msg = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text : null };
                if (calls.Count > 0) msg["tool_calls"] = calls;
                result.Add(msg);
            }
            else
            {
                // tool_result -> mensagens role=tool; texto do usuário -> role=user
                foreach (var b in blocks.OfType<JsonObject>())
                {
                    if (b["type"]?.GetValue<string>() == "tool_result")
                        result.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = b["tool_use_id"]!.GetValue<string>(),
                            ["content"] = b["content"]?.GetValue<string>() ?? "",
                        });
                    else
                        result.Add(new JsonObject { ["role"] = "user", ["content"] = b["text"]?.GetValue<string>() ?? "" });
                }
            }
        }
        return result;
    }

    internal static JsonArray ToOpenAiTools(JsonArray tools)
    {
        var result = new JsonArray();
        foreach (var t in tools.OfType<JsonObject>())
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t["name"]!.GetValue<string>(),
                    ["description"] = t["description"]!.GetValue<string>(),
                    ["parameters"] = t["input_schema"]!.DeepClone(),
                },
            });
        return result;
    }

    // ---------- OpenAI -> Anthropic blocks ----------
    internal static ModelTurn ToModelTurn(JsonObject message, string? finishReason)
    {
        var content = new JsonArray();
        if (message["content"]?.GetValue<string>() is { Length: > 0 } text)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        var temCalls = false;
        if (message["tool_calls"] is JsonArray calls)
            foreach (var c in calls.OfType<JsonObject>())
            {
                temCalls = true;
                var args = c["function"]?["arguments"]?.GetValue<string>();
                JsonNode? input = null;
                try { input = string.IsNullOrWhiteSpace(args) ? null : JsonNode.Parse(args); } catch (System.Text.Json.JsonException) { }
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = c["id"]!.GetValue<string>(),
                    ["name"] = c["function"]!["name"]!.GetValue<string>(),
                    ["input"] = input as JsonObject ?? new JsonObject(),
                });
            }

        // finish_reason "length" = cortado por max_tokens (inclusive no meio de tool_calls): não tratar como tool_use válido.
        var stop = finishReason == "length" ? "max_tokens" : temCalls ? "tool_use" : "end_turn";
        return new ModelTurn(content, stop);
    }
}
