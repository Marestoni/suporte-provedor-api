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

    public async Task<List<MensagemRegistro>> ListarMensagensAsync(Guid conversaId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<MensagemRegistro>(new(
            "SELECT papel, conteudo, payload::text AS payload FROM mensagens WHERE conversa_id=@conversaId ORDER BY id",
            new { conversaId }, cancellationToken: ct));
        return rows.ToList();
    }

    // ---------- Clientes ----------
    public async Task<Cliente?> ObterClientePorCpfAsync(string cpf, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Cliente>(new(
            "SELECT id, nome, cpf, data_nascimento, plano, bairro, login_pppoe, situacao FROM clientes WHERE cpf=@cpf",
            new { cpf }, cancellationToken: ct));
    }

    public async Task<Cliente?> ObterClienteAsync(long id, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Cliente>(new(
            "SELECT id, nome, cpf, data_nascimento, plano, bairro, login_pppoe, situacao FROM clientes WHERE id=@id",
            new { id }, cancellationToken: ct));
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
    public async Task<DateTimeOffset?> UltimoDesbloqueioAsync(long clienteId, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        var ultimo = await c.ExecuteScalarAsync<DateTime?>(new(
            "SELECT max(criado_em) FROM desbloqueios_confianca WHERE cliente_id=@clienteId",
            new { clienteId }, cancellationToken: ct));
        return ultimo is null ? null : new DateTimeOffset(DateTime.SpecifyKind(ultimo.Value, DateTimeKind.Utc));
    }

    /// <summary>Registra o desbloqueio e reativa o cliente (no MVP o "ERP" é este banco).</summary>
    public async Task RegistrarDesbloqueioAsync(long clienteId, Guid conversaId, DateTimeOffset ate, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new(
            "INSERT INTO desbloqueios_confianca (cliente_id, conversa_id, liberado_ate) VALUES (@clienteId, @conversaId, @ate)",
            new { clienteId, conversaId, ate }, tx, cancellationToken: ct));
        await c.ExecuteAsync(new(
            "UPDATE clientes SET situacao='ativo' WHERE id=@clienteId", new { clienteId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<Chamado> CriarChamadoAsync(long clienteId, Guid conversaId, string tipo, string descricao, string? diagnostico, CancellationToken ct)
    {
        await using var c = await _ds.OpenConnectionAsync(ct);
        for (var tentativa = 0; ; tentativa++)
        {
            var protocolo = $"CH-{DateTime.UtcNow:yyMMdd}-{Random.Shared.Next(1000, 10000)}";
            try
            {
                return await c.QuerySingleAsync<Chamado>(new(
                    @"INSERT INTO chamados (protocolo, cliente_id, conversa_id, tipo, descricao, diagnostico)
                      VALUES (@protocolo, @clienteId, @conversaId, @tipo, @descricao, @diagnostico)
                      RETURNING id, protocolo, criado_em",
                    new { protocolo, clienteId, conversaId, tipo, descricao, diagnostico }, cancellationToken: ct));
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && tentativa < 5) { }
        }
    }
}

