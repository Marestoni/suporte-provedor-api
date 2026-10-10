namespace SuporteProvedor.Api.Domain;

/// <summary>
/// Limite de tentativas de identificação, em janela de tempo (não por conversa: criar conversas novas
/// não zera o contador). As contagens já incluem a tentativa atual, registrada ANTES de validar para que
/// requisições paralelas não escapem do limite; tentativas bem-sucedidas são removidas.
///
/// Três limites combinados:
/// - por (CPF, IP): impede um mesmo IP de martelar um CPF e NÃO afeta o dono do CPF em outro IP
///   (um terceiro não consegue travar a vítima sozinho);
/// - por IP: impede varrer vários CPFs de um mesmo IP;
/// - por CPF (teto alto): limita ataque distribuído, às custas de só travar a vítima com tentativas
///   vindas de vários IPs.
/// </summary>
public static class PoliticaIdentificacao
{
    public const int JanelaMinutos = 15;
    public const int MaxPorCpfEIp = 5;
    // Alto de propósito: provedores usam CGNAT e muitos assinantes dividem um IP público. A proteção contra
    // força bruta de um CPF vem dos limites por (CPF, IP) e por CPF; este só barra varredura de muitos CPFs.
    public const int MaxPorIp = 60;
    public const int MaxPorCpf = 30;

    public static bool Bloqueado(long porCpfEIp, long porIp, long porCpf) =>
        porCpfEIp > MaxPorCpfEIp || porIp > MaxPorIp || porCpf > MaxPorCpf;
}
