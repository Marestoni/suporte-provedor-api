using System.Text.Json.Nodes;
using SuporteProvedor.Api.Application.Agent;

namespace SuporteProvedor.Tests;

public class OpenRouterAdaptadorTests
{
    [Fact]
    public void Traduz_historico_com_tool_use_e_tool_result()
    {
        var messages = JsonNode.Parse("""
        [
          {"role":"user","content":[{"type":"text","text":"minha net caiu"}]},
          {"role":"assistant","content":[{"type":"tool_use","id":"call_1","name":"status_conexao","input":{}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"{\"online\":false}"}]}
        ]
        """)!.AsArray();

        var r = OpenRouterModel.ToOpenAiMessages("SISTEMA", messages);

        Assert.Equal(4, r.Count);
        Assert.Equal("system", r[0]!["role"]!.GetValue<string>());
        Assert.Equal("user", r[1]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", r[2]!["role"]!.GetValue<string>());
        Assert.Equal("status_conexao", r[2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("tool", r[3]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", r[3]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public void Traduz_definicoes_de_ferramentas()
    {
        var r = OpenRouterModel.ToOpenAiTools(AgentTools.Definicoes());

        Assert.Equal(7, r.Count);
        Assert.Equal("function", r[0]!["type"]!.GetValue<string>());
        Assert.NotNull(r[0]!["function"]!["parameters"]);
    }

    [Fact]
    public void Resposta_com_tool_calls_vira_tool_use()
    {
        var msg = JsonNode.Parse("""
        {"role":"assistant","content":null,"tool_calls":[
          {"id":"c9","type":"function","function":{"name":"segunda_via","arguments":"{\"fatura_id\":3}"}}]}
        """)!.AsObject();

        var turno = OpenRouterModel.ToModelTurn(msg, "tool_calls");

        Assert.Equal("tool_use", turno.StopReason);
        var uso = turno.Content[0]!.AsObject();
        Assert.Equal("segunda_via", uso["name"]!.GetValue<string>());
        Assert.Equal(3, uso["input"]!["fatura_id"]!.GetValue<int>());
    }

    [Fact]
    public void Resposta_so_texto_vira_end_turn_e_argumentos_invalidos_nao_quebram()
    {
        var texto = OpenRouterModel.ToModelTurn(JsonNode.Parse("""{"content":"Olá!"}""")!.AsObject(), "stop");
        Assert.Equal("end_turn", texto.StopReason);
        Assert.Equal("Olá!", texto.Content[0]!["text"]!.GetValue<string>());

        var ruim = OpenRouterModel.ToModelTurn(JsonNode.Parse("""
        {"content":null,"tool_calls":[{"id":"x","function":{"name":"listar_faturas","arguments":"{quebrado"}}]}
        """)!.AsObject(), "tool_calls");
        Assert.Equal("tool_use", ruim.StopReason);
        Assert.Empty(ruim.Content[0]!["input"]!.AsObject());
    }
}
