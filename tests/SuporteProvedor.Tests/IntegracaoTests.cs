using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuporteProvedor.Api.Application;
using SuporteProvedor.Api.Application.Agent;
using SuporteProvedor.Api.Infrastructure;

namespace SuporteProvedor.Tests;

[Collection("banco")]
public class IntegracaoTests(BancoFixture banco)
{
    private const string CpfBruno = "52998224725";   // bloqueado por inadimplência
    private const string CpfCarla = "39053344705";   // online, sinal ruim

    private Db NovoDb() => new(banco.DataSource);

    // ---------- Desbloqueio em confiança ----------
    [FactBanco]
    public async Task Desbloqueio_simultaneo_so_libera_uma_vez()
    {
        var db = NovoDb();
        var cliente = (await db.ObterClientePorCpfAsync(CpfBruno, default))!;
        await using var c = await banco.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM desbloqueios_confianca WHERE cliente_id=@id", new { id = cliente.Id });

        var conversas = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => db.CriarConversaAsync("web", default)));
        var resultados = await Task.WhenAll(conversas.Select(cv =>
            db.TentarDesbloqueioAsync(cliente.Id, cv, DateTimeOffset.UtcNow, default)));

        Assert.Equal(1, resultados.Count(r => r.Liberado));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM desbloqueios_confianca WHERE cliente_id=@id", new { id = cliente.Id }));
    }

    [FactBanco]
    public async Task Situacao_efetiva_vira_liberada_e_volta_a_bloqueada_ao_expirar()
    {
        var db = NovoDb();
        var cliente = (await db.ObterClientePorCpfAsync(CpfBruno, default))!;
        await using var c = await banco.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM desbloqueios_confianca WHERE cliente_id=@id", new { id = cliente.Id });
        Assert.Equal("bloqueado_inadimplencia", (await db.ObterClienteAsync(cliente.Id, default))!.Situacao);

        var conversa = await db.CriarConversaAsync("web", default);
        Assert.True((await db.TentarDesbloqueioAsync(cliente.Id, conversa, DateTimeOffset.UtcNow, default)).Liberado);
        Assert.Equal("liberado_em_confianca", (await db.ObterClienteAsync(cliente.Id, default))!.Situacao);

        await c.ExecuteAsync("UPDATE desbloqueios_confianca SET liberado_ate = now() - interval '1 minute' WHERE cliente_id=@id", new { id = cliente.Id });
        Assert.Equal("bloqueado_inadimplencia", (await db.ObterClienteAsync(cliente.Id, default))!.Situacao);
        // clientes.situacao nunca é alterada pelo desbloqueio
        Assert.Equal("bloqueado_inadimplencia", await c.ExecuteScalarAsync<string>("SELECT situacao FROM clientes WHERE id=@id", new { id = cliente.Id }));
    }

    // ---------- Identificação: força bruta (achado da security-review) ----------
    [FactBanco]
    public async Task Identificacao_nao_zera_o_limite_ao_criar_conversa_nova_e_nao_trava_outro_cpf()
    {
        var db = NovoDb();
        await using var c = await banco.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("TRUNCATE tentativas_identificacao");
        var sessao = new SessaoService(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        const string ip = "203.0.113.7";
        var dataErrada = new DateOnly(2000, 1, 1);

        // 5 tentativas erradas no CPF da Ana, cada uma numa conversa NOVA
        for (var i = 0; i < Api.Domain.PoliticaIdentificacao.MaxPorCpfEIp; i++)
        {
            var conv = await db.CriarConversaAsync("web", default);
            Assert.Equal(ResultadoIdentificacao.Invalido, (await sessao.IdentificarAsync(conv, "11144477735", dataErrada, ip, default)).Item1);
        }

        // 6ª tentativa, conversa nova, com a data CORRETA: continua bloqueada
        var nova = await db.CriarConversaAsync("web", default);
        Assert.Equal(ResultadoIdentificacao.Bloqueado, (await sessao.IdentificarAsync(nova, "11144477735", new DateOnly(1990, 3, 15), ip, default)).Item1);

        // outro CPF, mesmo IP, data correta: funciona (limite por par CPF+IP)
        var outra = await db.CriarConversaAsync("web", default);
        Assert.Equal(ResultadoIdentificacao.Ok, (await sessao.IdentificarAsync(outra, CpfCarla, new DateOnly(1992, 11, 2), ip, default)).Item1);

        // o dono do CPF da Ana, vindo de OUTRO IP, ainda consegue entrar (um terceiro não o trava sozinho)
        var dono = await db.CriarConversaAsync("web", default);
        Assert.Equal(ResultadoIdentificacao.Ok, (await sessao.IdentificarAsync(dono, "11144477735", new DateOnly(1990, 3, 15), "198.51.100.9", default)).Item1);

        // tentativa bloqueada não prolonga o bloqueio: continua só com as 5 tentativas reais do par
        Assert.Equal(Api.Domain.PoliticaIdentificacao.MaxPorCpfEIp,
            await c.ExecuteScalarAsync<int>("SELECT count(*) FROM tentativas_identificacao WHERE ip = @ip::inet", new { ip }));
    }

    // ---------- Chamado ----------
    [FactBanco]
    public async Task Abrir_chamado_de_novo_inclusive_em_outra_conversa_devolve_o_mesmo_protocolo()
    {
        var db = NovoDb();
        var cliente = (await db.ObterClientePorCpfAsync(CpfCarla, default))!;
        await using var c = await banco.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM chamados WHERE cliente_id=@id", new { id = cliente.Id });
        var conversa = await db.CriarConversaAsync("web", default);
        var outraConversa = await db.CriarConversaAsync("web", default); // ex.: cliente recarregou a página

        var primeiro = await db.CriarChamadoAsync(cliente.Id, conversa, "visita_tecnica", "sem sinal", null, default);
        var segundo = await db.CriarChamadoAsync(cliente.Id, outraConversa, "visita_tecnica", "sem sinal de novo", null, default);

        Assert.False(primeiro.JaExistia);
        Assert.True(segundo.JaExistia);
        Assert.Equal(primeiro.Chamado.Protocolo, segundo.Chamado.Protocolo);
        // a informação da segunda conversa é anexada ao chamado existente, não perdida
        var diag = await c.ExecuteScalarAsync<string>("SELECT diagnostico FROM chamados WHERE id=@id", new { id = primeiro.Chamado.Id });
        Assert.Contains("sem sinal de novo", diag);

        // chamado já agendado também conta como ativo (não abre outro)
        await c.ExecuteAsync("UPDATE chamados SET status='agendado' WHERE id=@id", new { id = primeiro.Chamado.Id });
        var terceiro = await db.CriarChamadoAsync(cliente.Id, conversa, "visita_tecnica", "ainda sem sinal", null, default);
        Assert.True(terceiro.JaExistia);
        Assert.Equal(primeiro.Chamado.Protocolo, terceiro.Chamado.Protocolo);
    }

    [FactBanco]
    public async Task Chamados_simultaneos_de_conversas_diferentes_geram_um_so_chamado_ativo()
    {
        var db = NovoDb();
        var cliente = (await db.ObterClientePorCpfAsync(CpfCarla, default))!;
        await using var c = await banco.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM chamados WHERE cliente_id=@id", new { id = cliente.Id });

        var conversas = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => db.CriarConversaAsync("web", default)));
        var resultados = await Task.WhenAll(conversas.Select(cv =>
            db.CriarChamadoAsync(cliente.Id, cv, "visita_tecnica", "sem sinal", null, default)));

        Assert.Equal(1, resultados.Count(r => !r.JaExistia));
        Assert.Single(resultados.Select(r => r.Chamado.Protocolo).Distinct());
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM chamados WHERE cliente_id=@id", new { id = cliente.Id }));
    }

    [FactBanco]
    public async Task Mesma_nota_repetida_nao_e_anexada_duas_vezes()
    {
        var db = NovoDb();
        var cliente = (await db.ObterClientePorCpfAsync(CpfCarla, default))!;
        await using var c = await banco.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM chamados WHERE cliente_id=@id", new { id = cliente.Id });
        var inicial = await db.CriarConversaAsync("web", default);
        var outra = await db.CriarConversaAsync("web", default);
        var chamado = await db.CriarChamadoAsync(cliente.Id, inicial, "visita_tecnica", "sem sinal", "diag original", default);

        await db.CriarChamadoAsync(cliente.Id, outra, "visita_tecnica", "luz LOS vermelha", "ONU piscando", default);
        await db.CriarChamadoAsync(cliente.Id, outra, "visita_tecnica", "luz LOS vermelha", "ONU piscando", default);

        var diag = await c.ExecuteScalarAsync<string>("SELECT diagnostico FROM chamados WHERE id=@id", new { id = chamado.Chamado.Id });
        Assert.Contains("diag original", diag);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(diag!, "luz LOS vermelha"));
    }

    // ---------- Loop do agente com modelo roteirizado ----------
    private sealed class ModeloQueFalha : IAgentModel
    {
        public Task<ModelTurn> CompleteAsync(string system, JsonArray messages, JsonArray tools, CancellationToken ct) =>
            throw new AgentIndisponivelException("simulado");
    }

    private sealed class ModeloRoteirizado(params ModelTurn[] turnos) : IAgentModel
    {
        private int _i;
        public int Chamadas => _i;
        public Task<ModelTurn> CompleteAsync(string system, JsonArray messages, JsonArray tools, CancellationToken ct) =>
            Task.FromResult(turnos[Math.Min(_i++, turnos.Length - 1)]);
    }

    private static ModelTurn Texto(string t) =>
        new(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = t }), "end_turn");

    private static ModelTurn Vazio() => new(new JsonArray(), "end_turn");

    private static ModelTurn ToolUse(string nome, string stop = "tool_use", JsonObject? input = null) =>
        new(new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "call_" + Guid.NewGuid().ToString("N")[..6], ["name"] = nome, ["input"] = input ?? new JsonObject() }), stop);

    private static AgentService CriarAgente(Db db, IAgentModel modelo, int maxIteracoes = 4) =>
        new(db, modelo, new AgentTools(db, NullLogger<AgentTools>.Instance),
            Options.Create(new AgentOptions { MaxIteracoes = maxIteracoes }), NullLogger<AgentService>.Instance);

    private async Task<(AgentService Agente, Guid Conversa, Api.Domain.Cliente Cliente, Db Db)> Montar(IAgentModel modelo, int maxIteracoes = 4)
    {
        var db = NovoDb();
        var cliente = (await db.ObterClientePorCpfAsync(CpfCarla, default))!;
        return (CriarAgente(db, modelo, maxIteracoes), await db.CriarConversaAsync("web", default), cliente, db);
    }

    [FactBanco]
    public async Task Falha_do_modelo_nao_deixa_a_mensagem_do_usuario_no_historico()
    {
        var (agente, conversa, cliente, db) = await Montar(new ModeloQueFalha());

        await Assert.ThrowsAsync<AgentIndisponivelException>(() => agente.ResponderAsync(conversa, cliente, "oi", default));

        Assert.Empty(await db.ListarMensagensAsync(conversa, default));
        // o reenvio do mesmo texto funciona e não duplica nada
        await CriarAgente(db, new ModeloRoteirizado(Texto("Olá!"))).ResponderAsync(conversa, cliente, "oi", default);
        Assert.Equal(["user", "assistant"], (await db.ListarMensagensAsync(conversa, default)).Select(m => m.Papel).ToArray());
    }

    [FactBanco]
    public async Task Turno_vazio_nao_e_gravado_e_o_agente_tenta_de_novo()
    {
        var (agente, conversa, cliente, db) = await Montar(new ModeloRoteirizado(Vazio(), Texto("Olá!")));
        var r = await agente.ResponderAsync(conversa, cliente, "oi", default);

        Assert.Equal("Olá!", r.Texto);
        var msgs = await db.ListarMensagensAsync(conversa, default);
        Assert.Equal(["user", "assistant"], msgs.Select(m => m.Papel).ToArray());
    }

    [FactBanco]
    public async Task Tool_use_truncado_por_max_tokens_nao_executa_mas_fecha_o_par_com_erro()
    {
        var chamadoTruncado = ToolUse("abrir_chamado", stop: "max_tokens", input: new JsonObject { ["descricao"] = "texto cort" });
        var (agente, conversa, cliente, db) = await Montar(new ModeloRoteirizado(chamadoTruncado, Texto("Vou tentar de novo.")));
        await using var c = await banco.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM chamados WHERE cliente_id=@id", new { id = cliente.Id });

        var r = await agente.ResponderAsync(conversa, cliente, "quero visita", default);

        Assert.Equal("Vou tentar de novo.", r.Texto);
        var msgs = await db.ListarMensagensAsync(conversa, default);
        Assert.Equal(["user", "assistant", "tool", "assistant"], msgs.Select(m => m.Papel).ToArray());
        Assert.Contains("cortada", msgs[2].Payload);                                   // erro devolvido ao modelo
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM chamados WHERE cliente_id=@id", new { id = cliente.Id })); // não executou
    }

    [FactBanco]
    public async Task Tool_use_normal_e_executado_e_o_par_e_gravado()
    {
        var (agente, conversa, cliente, db) = await Montar(new ModeloRoteirizado(ToolUse("status_conexao"), Texto("Seu sinal está fraco.")));
        var r = await agente.ResponderAsync(conversa, cliente, "internet lenta", default);

        Assert.Equal("Seu sinal está fraco.", r.Texto);
        var msgs = await db.ListarMensagensAsync(conversa, default);
        Assert.Equal(["user", "assistant", "tool", "assistant"], msgs.Select(m => m.Papel).ToArray());
        Assert.Contains("sinal_onu_dbm", msgs[2].Payload);                             // resultado real da ferramenta
    }

    [FactBanco]
    public async Task Sem_texto_final_responde_sem_prometer_transferencia()
    {
        var (agente, conversa, cliente, db) = await Montar(new ModeloRoteirizado(Vazio()));
        var r = await agente.ResponderAsync(conversa, cliente, "oi", default);

        Assert.Equal(AgentService.TextoSemResposta, r.Texto);
        Assert.Equal("aberta", r.StatusConversa);
        Assert.Equal("aberta", (await db.ObterConversaAsync(conversa, default))!.Status);
    }

    [FactBanco]
    public async Task Limite_de_iteracoes_esgotado_devolve_o_texto_padrao_e_o_historico_continua_valido()
    {
        var modelo = new ModeloRoteirizado(ToolUse("status_conexao"));
        var (agente, conversa, cliente, db) = await Montar(modelo, maxIteracoes: 3);
        var r = await agente.ResponderAsync(conversa, cliente, "oi", default);

        Assert.Equal(AgentService.TextoSemResposta, r.Texto);
        Assert.Equal(3, modelo.Chamadas);

        // Uma nova mensagem precisa funcionar: o histórico não pode ter ficado quebrado.
        var agente2 = CriarAgente(db, new ModeloRoteirizado(Texto("Voltei!")));
        var r2 = await agente2.ResponderAsync(conversa, cliente, "e agora?", default);
        Assert.Equal("Voltei!", r2.Texto);
    }
}