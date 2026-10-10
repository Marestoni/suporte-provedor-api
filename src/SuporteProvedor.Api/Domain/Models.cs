namespace SuporteProvedor.Api.Domain;

public record Cliente(
    long Id, string Nome, string Cpf, DateOnly DataNascimento, string Plano,
    string Bairro, string LoginPppoe, string Situacao);

public record Fatura(
    long Id, long ClienteId, string Referencia, decimal Valor, DateOnly Vencimento,
    string Status, string? PixCopiaCola, string? LinhaDigitavel);

public record Conexao(
    long ClienteId, bool Online, DateTime? UltimoLogin, string? Ip,
    string OnuStatus, decimal? SinalOnuDbm);

public record Incidente(
    long Id, string Bairro, string Titulo, string? Descricao, DateTime IniciadoEm,
    DateTime? PrevisaoEm);

public record Chamado(long Id, string Protocolo, DateTime CriadoEm);

public record ConversaInfo(
    Guid Id, long? ClienteId, string Status, int TentativasIdentificacao, string? TokenHash);

public record MensagemRegistro(string Papel, string? Conteudo, string? Payload);

/// <summary>Resultado do pedido de desbloqueio em confiança (regra decidida em código).</summary>
public record ResultadoDesbloqueio(bool Liberado, string Motivo, DateTimeOffset? LiberadoAte);

