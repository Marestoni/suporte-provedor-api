using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SuporteProvedor.Api.Application.Agent;

public class AgentOptions
{
    public string Model { get; set; } = "claude-haiku-5-5";
    public int MaxTokens { get; set; } = 1024;
    public int MaxIteracoes { get; set; } = 6;
    /// <summary>"openrouter" (padrão), "anthropic" ou "fake" (roteirizado, só para desenvolvimento/testes).</summary>
    public string Provider { get; set; } = "openrouter";
}

public class AnthropicOptions
{
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
}

/// <summary>Cliente mínimo da Messages API (HTTP direto, sem SDK).</summary>
public class AnthropicModel(HttpClient http, IOptions<AnthropicOptions> anthropic, IOptions<AgentOptions> agent) : IAgentModel
{
    public async Task<ModelTurn> CompleteAsync(string system, JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        var apiKey = anthropic.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AgentIndisponivelException("Chave da Anthropic não configurada.");

        var body = new JsonObject
        {
            ["model"] = agent.Value.Model,
            ["max_tokens"] = agent.Value.MaxTokens,
            ["system"] = system,
            ["messages"] = messages.DeepClone(),
            ["tools"] = tools.DeepClone(),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{anthropic.Value.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new AgentIndisponivelException($"Anthropic respondeu {(int)res.StatusCode}.");

        var json = JsonNode.Parse(text)!;
        return new ModelTurn(
            (JsonArray)json["content"]!.DeepClone(),
            json["stop_reason"]?.GetValue<string>() ?? "end_turn");
    }
}

public class AgentIndisponivelException(string message) : Exception(message);
