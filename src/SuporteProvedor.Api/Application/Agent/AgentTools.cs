using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuporteProvedor.Api.Domain;
using SuporteProvedor.Api.Infrastructure;

namespace SuporteProvedor.Api.Application.Agent;

/// <summary>Contexto da conversa autenticada. O cliente vem daqui, NUNCA dos argumentos do modelo.</summary>
public class ToolContext(Guid conversaId, Cliente cliente)
{
    public Guid ConversaId { get; } = conversaId;
    public Cliente Cliente { get; set; } = cliente;
    public string? ProtocoloChamado { get; set; }
    public bool TransferidoParaHumano { get; set; }
}

public class AgentTools(Db db, ILogger<AgentTools> log)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static JsonObject Schema(JsonObject? props = null, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = props ?? new JsonObject(),
        ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
    };

    private static JsonObject Tool(string nome, string descricao, JsonObject schema) => new()
    {
        ["name"] = nome,
        ["description"] = descricao,
        ["input_schema"] = schema,
    };

    public static JsonArray Definicoes() => new(
        Tool("listar_faturas", "Lista as últimas faturas do cliente (valor, vencimento, status).", Schema()),
        Tool("segunda_via",
            "Devolve PIX copia e cola e linha digitável de uma fatura em aberto do cliente.",
            Schema(new JsonObject { ["fatura_id"] = new JsonObject { ["type"] = "integer", ["description"] = "id da fatura (de listar_faturas)" } }, "fatura_id")),
        Tool("status_conexao", "Consulta o estado técnico da conexão do cliente: online, último login, status e sinal da ONU.", Schema()),
        Tool("verificar_incidente", "Verifica se há falha geral aberta na região (bairro) do cliente.", Schema()),
        Tool("desbloqueio_confianca",
            "Solicita desbloqueio temporário para cliente bloqueado por inadimplência. O sistema decide se libera.", Schema()),
        Tool("abrir_chamado",
            "Abre chamado de visita técnica. Inclua no diagnóstico o que já foi verificado e tentado.",
            Schema(new JsonObject
            {
                ["descricao"] = new JsonObject { ["type"] = "string", ["description"] = "problema relatado pelo cliente" },
                ["diagnostico"] = new JsonObject { ["type"] = "string", ["description"] = "resumo do que foi verificado/tentado" },
            }, "descricao", "diagnostico")),
        Tool("transferir_humano",
            "Transfere a conversa para um atendente humano.",
            Schema(new JsonObject { ["motivo"] = new JsonObject { ["type"] = "string" } }, "motivo")));

    /// <summary>Executa a ferramenta e devolve o resultado como JSON (string) para o modelo.</summary>
    public async Task<string> ExecutarAsync(string nome, JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            object resultado = nome switch
            {
                "listar_faturas" => await ListarFaturas(ctx, ct),
                "segunda_via" => await SegundaVia(input, ctx, ct),
                "status_conexao" => await StatusConexao(ctx, ct),
                "verificar_incidente" => await VerificarIncidente(ctx, ct),
                "desbloqueio_confianca" => await Desbloqueio(ctx, ct),
                "abrir_chamado" => await AbrirChamado(input, ctx, ct),
                "transferir_humano" => await Transferir(ctx, ct),
                _ => new { erro = $"Ferramenta desconhecida: {nome}" },
            };
            return JsonSerializer.Serialize(resultado, Json);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Falha na ferramenta {Tool}", nome);
            return JsonSerializer.Serialize(new { erro = "Falha ao consultar o sistema. Tente novamente ou transfira para um atendente." }, Json);
        }
    }

    private async Task<object> ListarFaturas(ToolContext ctx, CancellationToken ct)
    {
        var faturas = await db.ListarFaturasAsync(ctx.Cliente.Id, ct);
        var hoje = DateOnly.FromDateTime(DateTime.Now);
        return new
        {
            faturas = faturas.Select(f => new
            {
                id = f.Id, referencia = f.Referencia, valor = f.Valor.ToString("F2", CultureInfo.InvariantCulture),
                vencimento = f.Vencimento.ToString("dd/MM/yyyy"), status = f.Status,
                vencida = f.Status == "aberta" && f.Vencimento < hoje,
            }),
        };
    }

    private async Task<object> SegundaVia(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        if (!input.TryGetPropertyValue("fatura_id", out var n) || n is null || !long.TryParse(n.ToString(), out var id))
            return new { erro = "fatura_id inválido." };

        var f = await db.ObterFaturaAsync(ctx.Cliente.Id, id, ct); // sempre filtrado pelo cliente da sessão
        if (f is null) return new { erro = "Fatura não encontrada para este cliente." };
        if (f.Status != "aberta") return new { erro = $"Esta fatura está '{f.Status}'; não há segunda via a emitir." };

        return new
        {
            referencia = f.Referencia, valor = f.Valor.ToString("F2", CultureInfo.InvariantCulture),
            vencimento = f.Vencimento.ToString("dd/MM/yyyy"),
            pix_copia_cola = f.PixCopiaCola, linha_digitavel = f.LinhaDigitavel,
        };
    }

    private async Task<object> StatusConexao(ToolContext ctx, CancellationToken ct)
    {
        var c = await db.ObterConexaoAsync(ctx.Cliente.Id, ct);
        if (c is null) return new { erro = "Sem dados técnicos para este cliente." };
        return new
        {
            situacao_contrato = ctx.Cliente.Situacao, online = c.Online,
            ultimo_login = c.UltimoLogin is { } ul ? FusoBrasil.Formatar(ul, "dd/MM/yyyy HH:mm") : null,
            onu_status = c.OnuStatus, sinal_onu_dbm = c.SinalOnuDbm,
        };
    }

    private async Task<object> VerificarIncidente(ToolContext ctx, CancellationToken ct)
    {
        var i = await db.ObterIncidenteAbertoAsync(ctx.Cliente.Bairro, ct);
        if (i is null) return new { incidente_aberto = false };
        return new
        {
            incidente_aberto = true, titulo = i.Titulo, descricao = i.Descricao,
            previsao = i.PrevisaoEm is { } pv ? FusoBrasil.Formatar(pv, "dd/MM/yyyy HH:mm") : null,
        };
    }

    private async Task<object> Desbloqueio(ToolContext ctx, CancellationToken ct)
    {
        // Regra + registro numa transação (db), para pedidos simultâneos não furarem o limite.
        var r = await db.TentarDesbloqueioAsync(ctx.Cliente.Id, ctx.ConversaId, DateTimeOffset.UtcNow, ct);
        if (r.Liberado)
            ctx.Cliente = ctx.Cliente with { Situacao = "liberado_em_confianca" };
        return new { liberado = r.Liberado, motivo = r.Motivo, liberado_ate = r.LiberadoAte is { } ate ? FusoBrasil.Formatar(ate, "dd/MM/yyyy") : null };
    }

    private async Task<object> AbrirChamado(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var descricao = input["descricao"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(descricao)) return new { erro = "descricao é obrigatória." };
        var r = await db.CriarChamadoAsync(ctx.Cliente.Id, ctx.ConversaId, "visita_tecnica",
            descricao, input["diagnostico"]?.GetValue<string>(), ct);
        ctx.ProtocoloChamado = r.Chamado.Protocolo;
        return new { aberto = true, protocolo = r.Chamado.Protocolo, ja_existia = r.JaExistia };
    }

    private async Task<object> Transferir(ToolContext ctx, CancellationToken ct)
    {
        await db.EncerrarConversaAsync(ctx.ConversaId, "transferida_humano", ct);
        ctx.TransferidoParaHumano = true;
        return new { transferido = true };
    }
}
