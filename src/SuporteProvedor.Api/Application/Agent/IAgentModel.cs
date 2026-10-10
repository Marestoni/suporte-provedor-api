using System.Text.Json.Nodes;

namespace SuporteProvedor.Api.Application.Agent;

/// <summary>Resposta de um turno do modelo: blocos de conteúdo no formato da Messages API.</summary>
public record ModelTurn(JsonArray Content, string StopReason);

public interface IAgentModel
{
    /// <param name="messages">Array de mensagens no formato da Messages API ({role, content}).</param>
    /// <param name="tools">Definições de ferramentas ({name, description, input_schema}).</param>
    Task<ModelTurn> CompleteAsync(string system, JsonArray messages, JsonArray tools, CancellationToken ct);
}
