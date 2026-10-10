using System.Security.Cryptography;
using System.Text;
using SuporteProvedor.Api.Domain;
using SuporteProvedor.Api.Infrastructure;

namespace SuporteProvedor.Api.Application;

public enum ResultadoIdentificacao { Ok, Invalido, Bloqueado, ConversaInvalida }

public record Identificado(string Token, string PrimeiroNome, string Situacao);

/// <summary>Identificação (CPF + data de nascimento) e validação do token de sessão da conversa.</summary>
public class SessaoService(Db db, IConfiguration config)
{
    public const int MaxTentativas = 3;

    // Sem Seguranca:ChaveHashCpf configurada, usa uma chave aleatória do processo (os contadores de tentativa
    // reiniciam a cada restart; aceitável em desenvolvimento, configure a chave em produção).
    private static readonly byte[] ChaveEfemera = RandomNumberGenerator.GetBytes(32);

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// HMAC-SHA256 do CPF com segredo do servidor. Um SHA-256 simples seria revertível por força bruta
    /// (só ~10^9 CPFs possíveis) por quem obtivesse a tabela de tentativas.
    /// </summary>
    private string HashCpf(string digitos)
    {
        var chave = config["Seguranca:ChaveHashCpf"]; // ausência é avisada uma vez na inicialização (Program.cs)
        var bytes = string.IsNullOrEmpty(chave) ? ChaveEfemera : Encoding.UTF8.GetBytes(chave);
        return Convert.ToHexString(HMACSHA256.HashData(bytes, Encoding.UTF8.GetBytes(digitos)));
    }

    public static string SomenteDigitos(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    public async Task<(ResultadoIdentificacao, Identificado?)> IdentificarAsync(
        Guid conversaId, string? cpf, DateOnly? nascimento, string? ip, CancellationToken ct)
    {
        var conversa = await db.ObterConversaAsync(conversaId, ct);
        if (conversa is null || conversa.Status != "aberta" || conversa.ClienteId is not null)
            return (ResultadoIdentificacao.ConversaInvalida, null);
        if (conversa.TentativasIdentificacao >= MaxTentativas)
            return (ResultadoIdentificacao.Bloqueado, null);

        // Limite por CPF e por IP (não é zerado ao criar outra conversa). Registrado ANTES de validar,
        // para que requisições paralelas não passem do limite. Vale também para CPFs inexistentes.
        var digitos = SomenteDigitos(cpf);
        var tentativa = await db.RegistrarTentativaIdentificacaoAsync(HashCpf(digitos), ip, ct);
        if (PoliticaIdentificacao.Bloqueado(tentativa.PorCpfEIp, tentativa.PorIp, tentativa.PorCpf))
        {
            // A tentativa bloqueada não é contada: senão cada nova tentativa prolongaria o próprio bloqueio
            // (janela deslizante) e ele nunca expiraria para quem insiste.
            await db.RemoverTentativaIdentificacaoAsync(tentativa.Id, ct);
            return (ResultadoIdentificacao.Bloqueado, null);
        }

        var cliente = nascimento is null ? null : await db.ObterClientePorCpfAsync(digitos, ct);
        if (cliente is null || cliente.DataNascimento != nascimento || cliente.Situacao == "cancelado")
        {
            var tentativas = await db.RegistrarTentativaAsync(conversaId, ct);
            if (tentativas >= MaxTentativas)
            {
                await db.EncerrarConversaAsync(conversaId, "encerrada", ct);
                return (ResultadoIdentificacao.Bloqueado, null);
            }
            return (ResultadoIdentificacao.Invalido, null);
        }

        await db.RemoverTentativaIdentificacaoAsync(tentativa.Id, ct); // acerto não conta como falha
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await db.VincularClienteAsync(conversaId, cliente.Id, HashToken(token), ct);
        var primeiro = cliente.Nome.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return (ResultadoIdentificacao.Ok, new Identificado(token, primeiro, cliente.Situacao));
    }

    /// <summary>Valida o token Bearer da conversa e devolve o cliente dono dela.</summary>
    public async Task<(ConversaInfo Conversa, Cliente Cliente)?> AutenticarAsync(Guid conversaId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var conversa = await db.ObterConversaAsync(conversaId, ct);
        if (conversa?.ClienteId is null || conversa.TokenHash is null) return null;

        var esperado = Encoding.UTF8.GetBytes(conversa.TokenHash);
        var recebido = Encoding.UTF8.GetBytes(HashToken(token));
        if (!CryptographicOperations.FixedTimeEquals(esperado, recebido)) return null;

        var cliente = await db.ObterClienteAsync(conversa.ClienteId.Value, ct);
        return cliente is null ? null : (conversa, cliente);
    }
}
