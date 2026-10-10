using Dapper;
using Npgsql;
using SuporteProvedor.Api.Domain;

namespace SuporteProvedor.Api.Infrastructure;

/// <summary>
/// Acesso ao Postgres. No MVP também faz o papel dos "sistemas do provedor" (ERP, Radius/ACS, NOC),
/// que na integração real serão substituídos por adapters.
/// </summary>
public class Db
{
    private readonly NpgsqlDataSource _ds;

    static Db() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public Db(NpgsqlDataSource ds) => _ds = ds;

    // ---------- Conversas ----------
    public async Task<Guid> CriarConversaAsync(string canal, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<Guid>(
            new("INSERT INTO conversas (canal) VALUES (@canal) RETURNING id", new { canal }, cancellationToken: ct));
    }

    public async Task<ConversaInfo?> ObterConversaAsync(Guid id, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ConversaInfo>(new(
            "SELECT id, cliente_id, status, tentativas_identificacao, token_hash FROM conversas WHERE id=@id",
            new { id }, cancellationToken: ct));
    }

    public async Task<int> RegistrarTentativaAsync(Guid id, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new(
            "UPDATE conversas SET tentativas_identificacao = tentativas_identificacao + 1 WHERE id=@id RETURNING tentativas_identificacao",
            new { id }, cancellationToken: ct));
    }

    /// <summary>Registra a tentativa e devolve quantas existem na janela (incluindo esta): por (CPF, IP), por IP e por CPF.</summary>
    public async Task<TentativaIdentificacao> RegistrarTentativaIdentificacaoAsync(string cpfHash, string? ip, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        var id = await c.ExecuteScalarAsync<long>(new(
            "INSERT INTO tentativas_identificacao (cpf_hash, ip) VALUES (@cpfHash, @ip::inet) RETURNING id",
            new { cpfHash, ip }, cancellationToken: ct));
        var (porCpfEIp, porIp, porCpf) = await c.QuerySingleAsync<(long, long, long)>(new(
            @"SELECT count(*) FILTER (WHERE cpf_hash = @cpfHash AND ip IS NOT DISTINCT FROM @ip::inet),
                     count(*) FILTER (WHERE ip IS NOT DISTINCT FROM @ip::inet),
                     count(*) FILTER (WHERE cpf_hash = @cpfHash)
              FROM tentativas_identificacao
              WHERE criado_em > now() - make_interval(mins => @janela)",
            new { cpfHash, ip, janela = PoliticaIdentificacao.JanelaMinutos }, cancellationToken: ct));
        return new TentativaIdentificacao(id, porCpfEIp, porIp, porCpf);
    }

    public async Task RemoverTentativaIdentificacaoAsync(long id, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new("DELETE FROM tentativas_identificacao WHERE id=@id", new { id }, cancellationToken: ct));
    }

    /// <summary>Retenção: remove tentativas fora da janela (com folga de 1 dia) e conversas nunca identificadas com mais de 1 dia.</summary>
    public async Task<(int Tentativas, int Conversas)> LimparAsync(CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        var tentativas = await c.ExecuteAsync(new(
            "DELETE FROM tentativas_identificacao WHERE criado_em < now() - interval '1 day'", cancellationToken: ct));
        var conversas = await c.ExecuteAsync(new(
            "DELETE FROM conversas WHERE cliente_id IS NULL AND criado_em < now() - interval '1 day'", cancellationToken: ct));
        return (tentativas, conversas);
    }

    public async Task EncerrarConversaAsync(Guid id, string status, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new(
            "UPDATE conversas SET status=@status, encerrado_em=now() WHERE id=@id", new { id, status }, cancellationToken: ct));
    }

    public async Task VincularClienteAsync(Guid id, long clienteId, string tokenHash, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new(
            "UPDATE conversas SET cliente_id=@clienteId, token_hash=@tokenHash WHERE id=@id",
            new { id, clienteId, tokenHash }, cancellationToken: ct));
    }

    public async Task AdicionarMensagemAsync(Guid conversaId, string papel, string? conteudo, string? ferramenta, string? payloadJson, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new(
            "INSERT INTO mensagens (conversa_id, papel, conteudo, ferramenta, payload) VALUES (@conversaId, @papel, @conteudo, @ferramenta, @payloadJson::jsonb)",
            new { conversaId, papel, conteudo, ferramenta, payloadJson }, cancellationToken: ct));
    }

    /// <summary>Grava várias mensagens numa transação (ex.: assistant com tool_use + tool_result nunca ficam pela metade).</summary>
    public async Task AdicionarMensagensAsync(Guid conversaId, IEnumerable<(string Papel, string? Conteudo, string? Ferramenta, string? PayloadJson)> mensagens, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        foreach (var (papel, conteudo, ferramenta, payloadJson) in mensagens)
            await c.ExecuteAsync(new(
                "INSERT INTO mensagens (conversa_id, papel, conteudo, ferramenta, payload) VALUES (@conversaId, @papel, @conteudo, @ferramenta, @payloadJson::jsonb)",
                new { conversaId, papel, conteudo, ferramenta, payloadJson }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<List<MensagemRegistro>> ListarMensagensAsync(Guid conversaId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<MensagemRegistro>(new(
            "SELECT papel, conteudo, payload::text AS payload FROM mensagens WHERE conversa_id=@conversaId ORDER BY id",
            new { conversaId }, cancellationToken: ct));
        return rows.ToList();
    }

    // ---------- Clientes ----------
    // A situação efetiva diferencia o bloqueado com desbloqueio em confiança ainda válido ('liberado_em_confianca');
    // ao expirar, volta sozinha a 'bloqueado_inadimplencia' (nada é alterado na tabela clientes).
    private const string SelectCliente = @"
        SELECT c.id, c.nome, c.cpf, c.data_nascimento, c.plano, c.bairro, c.login_pppoe,
               CASE WHEN c.situacao = 'bloqueado_inadimplencia'
                         AND EXISTS (SELECT 1 FROM desbloqueios_confianca d
                                     WHERE d.cliente_id = c.id AND d.liberado_ate > now())
                    THEN 'liberado_em_confianca' ELSE c.situacao END AS situacao
        FROM clientes c";

    public async Task<Cliente?> ObterClientePorCpfAsync(string cpf, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Cliente>(new(
            SelectCliente + " WHERE c.cpf=@cpf", new { cpf }, cancellationToken: ct));
    }

    public async Task<Cliente?> ObterClienteAsync(long id, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Cliente>(new(
            SelectCliente + " WHERE c.id=@id", new { id }, cancellationToken: ct));
    }

    // ---------- Faturas ----------
    public async Task<List<Fatura>> ListarFaturasAsync(long clienteId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<Fatura>(new(
            @"SELECT id, cliente_id, referencia, valor, vencimento, status, pix_copia_cola, linha_digitavel
              FROM faturas WHERE cliente_id=@clienteId ORDER BY vencimento DESC LIMIT 6",
            new { clienteId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Fatura?> ObterFaturaAsync(long clienteId, long faturaId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Fatura>(new(
            @"SELECT id, cliente_id, referencia, valor, vencimento, status, pix_copia_cola, linha_digitavel
              FROM faturas WHERE id=@faturaId AND cliente_id=@clienteId",
            new { clienteId, faturaId }, cancellationToken: ct));
    }

    // ---------- Diagnóstico / incidentes ----------
    public async Task<Conexao?> ObterConexaoAsync(long clienteId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Conexao>(new(
            @"SELECT cliente_id, online, ultimo_login, host(ip) AS ip, onu_status, sinal_onu_dbm
              FROM conexoes WHERE cliente_id=@clienteId",
            new { clienteId }, cancellationToken: ct));
    }

    public async Task<Incidente?> ObterIncidenteAbertoAsync(string bairro, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.QueryFirstOrDefaultAsync<Incidente>(new(
            @"SELECT id, bairro, titulo, descricao, iniciado_em, previsao_em
              FROM incidentes WHERE bairro=@bairro AND status='aberto' ORDER BY iniciado_em DESC LIMIT 1",
            new { bairro }, cancellationToken: ct));
    }

    // ---------- Ações ----------
    /// <summary>
    /// Avalia e registra o desbloqueio em confiança de forma atômica: o cliente fica travado (FOR UPDATE) enquanto
    /// a regra é checada e o registro inserido, então pedidos simultâneos não furam o limite de 1 por mês.
    /// A situação em clientes não muda; o desbloqueio vale enquanto liberado_ate &gt; agora (ver SelectCliente).
    /// </summary>
    public async Task<ResultadoDesbloqueio> TentarDesbloqueioAsync(long clienteId, Guid conversaId, DateTimeOffset agora, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var situacao = await c.ExecuteScalarAsync<string>(new(
            "SELECT situacao FROM clientes WHERE id=@clienteId FOR UPDATE", new { clienteId }, tx, cancellationToken: ct));
        var ultimoUtc = await c.ExecuteScalarAsync<DateTime?>(new(
            "SELECT max(criado_em) FROM desbloqueios_confianca WHERE cliente_id=@clienteId",
            new { clienteId }, tx, cancellationToken: ct));
        DateTimeOffset? ultimo = ultimoUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(ultimoUtc.Value, DateTimeKind.Utc));

        var r = RegrasDesbloqueio.Avaliar(situacao ?? "", ultimo, agora);
        if (r.Liberado)
            await c.ExecuteAsync(new(
                "INSERT INTO desbloqueios_confianca (cliente_id, conversa_id, liberado_ate) VALUES (@clienteId, @conversaId, @ate)",
                new { clienteId, conversaId, ate = r.LiberadoAte!.Value }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return r;
    }

    /// <summary>
    /// Abre chamado, ou devolve o já aberto do CLIENTE para o mesmo tipo (idempotente: o modelo pode repetir a chamada
    /// e o cliente pode reabrir o chat, já que o site perde a conversa ao recarregar).
    /// </summary>
    public async Task<ResultadoChamado> CriarChamadoAsync(long clienteId, Guid conversaId, string tipo, string descricao, string? diagnostico, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        for (var tentativa = 0; ; tentativa++)
        {
            var existente = await BuscarChamadoAtivoAsync(c, clienteId, tipo, ct);
            if (existente is not null)
            {
                await AnexarNotaAsync(c, existente.Id, conversaId, descricao, diagnostico, ct);
                return new ResultadoChamado(existente, true);
            }

            var protocolo = $"CH-{DateTime.UtcNow:yyMMdd}-{Random.Shared.Next(1000, 10000)}";
            try
            {
                var novo = await c.QuerySingleAsync<Chamado>(new(
                    @"INSERT INTO chamados (protocolo, cliente_id, conversa_id, tipo, descricao, diagnostico)
                      VALUES (@protocolo, @clienteId, @conversaId, @tipo, @descricao, @diagnostico)
                      RETURNING id, protocolo, criado_em",
                    // Mesmo formato das notas anexadas depois, para a deduplicação reconhecer uma repetição desta conversa.
                    new { protocolo, clienteId, conversaId, tipo, descricao, diagnostico = MontarNota(conversaId, descricao, diagnostico).Nota }, cancellationToken: ct));
                return new ResultadoChamado(novo, false);
            }
            // Outra conversa do mesmo cliente criou o chamado ativo entre o SELECT e o INSERT (índice único parcial,
            // migration 004): volta ao início do laço e devolve o existente. Colisão de protocolo também tenta de novo.
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && tentativa < 5) { }
        }
    }

    private static Task<Chamado?> BuscarChamadoAtivoAsync(NpgsqlConnection c, long clienteId, string tipo, CancellationToken ct) =>
        c.QueryFirstOrDefaultAsync<Chamado>(new(
            "SELECT id, protocolo, criado_em FROM chamados WHERE cliente_id=@clienteId AND tipo=@tipo AND status IN ('aberto','agendado') ORDER BY id LIMIT 1",
            new { clienteId, tipo }, cancellationToken: ct));

    /// <summary>Nota do que o agente levantou numa conversa: Conteudo é o trecho usado para detectar repetição.</summary>
    private static (string Nota, string Conteudo) MontarNota(Guid conversaId, string descricao, string? diagnostico)
    {
        var conteudo = descricao + (string.IsNullOrWhiteSpace(diagnostico) ? "" : $" | Diagnóstico: {diagnostico}");
        return ($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, conversa {conversaId}] {conteudo}", conteudo);
    }

    /// <summary>Anexa ao chamado existente o que o agente levantou nesta conversa (sem repetir a mesma nota).</summary>
    private static async Task AnexarNotaAsync(NpgsqlConnection c, long chamadoId, Guid conversaId, string descricao, string? diagnostico, CancellationToken ct)
    {
        var (nota, conteudo) = MontarNota(conversaId, descricao, diagnostico);
        await c.ExecuteAsync(new(
            @"UPDATE chamados SET diagnostico = concat_ws(E'\n---\n', diagnostico, @nota)
              WHERE id=@chamadoId AND (diagnostico IS NULL OR position(@conteudo IN diagnostico) = 0)",
            new { chamadoId, nota, conteudo }, cancellationToken: ct));
    }
}

