using System.Text.Json.Nodes;
using SuporteProvedor.Api.Application;

namespace SuporteProvedor.Tests;

public class HistoricoMensagensTests
{
    private static JsonObject Msg(string role, string json) =>
        new() { ["role"] = role, ["content"] = JsonNode.Parse(json) };

    private static JsonObject Texto(string role, string t) => Msg(role, """[{"type":"text","text":"@T"}]""".Replace("@T", t));
    private static JsonObject Uso(string id) => Msg("assistant", """[{"type":"tool_use","id":"@ID","name":"x","input":{}}]""".Replace("@ID", id));
    private static JsonObject Res(string id) => Msg("user", """[{"type":"tool_result","tool_use_id":"@ID","content":"{}"}]""".Replace("@ID", id));

    private static List<string> Papeis(JsonArray a) => a.Select(m => m!["role"]!.GetValue<string>()).ToList();

    [Fact]
    public void Mantem_historico_valido_intacto()
    {
        var h = new JsonArray(Texto("user", "oi"), Uso("a"), Res("a"), Texto("assistant", "pronto"));
        Assert.Equal(4, HistoricoMensagens.Sanear(h).Count);
    }

    [Fact]
    public void Descarta_turno_de_assistente_vazio()
    {
        var h = new JsonArray(Texto("user", "oi"), Msg("assistant", "[]"), Texto("user", "alo"));
        var r = HistoricoMensagens.Sanear(h);
        Assert.Equal(["user", "user"], Papeis(r));
    }

    [Fact]
    public void Descarta_tool_use_sem_resultado()
    {
        var h = new JsonArray(Texto("user", "oi"), Uso("a"), Texto("user", "e entao?"));
        var r = HistoricoMensagens.Sanear(h);
        Assert.Equal(["user", "user"], Papeis(r));
    }

    [Fact]
    public void Descarta_tool_result_orfao()
    {
        var h = new JsonArray(Texto("user", "oi"), Res("zzz"), Texto("assistant", "ok"));
        var r = HistoricoMensagens.Sanear(h);
        Assert.Equal(["user", "assistant"], Papeis(r));
    }

    [Fact]
    public void Truncamento_nao_deixa_par_pela_metade_nem_comecar_fora_de_user()
    {
        // limite 2 => sobram [assistant, user]; o assistant inicial cai e o histórico começa em user.
        var h = new JsonArray(Texto("user", "oi"), Uso("a"), Res("a"), Texto("assistant", "pronto"), Texto("user", "obrigado"));
        var r = HistoricoMensagens.Sanear(h, max: 2);
        Assert.Equal(["user"], Papeis(r));
    }
}