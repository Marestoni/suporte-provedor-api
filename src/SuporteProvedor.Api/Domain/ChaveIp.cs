using System.Net;
using System.Net.Sockets;

namespace SuporteProvedor.Api.Domain;

/// <summary>
/// Chave de IP para os limites de identificação. IPv6 é agrupado no prefixo /64 (uma casa/assinante recebe um /64
/// inteiro e rotaciona o endereço à vontade); IPv4 e IPv4 mapeado em IPv6 ficam como estão.
/// </summary>
public static class ChaveIp
{
    public static string? De(IPAddress? ip)
    {
        if (ip is null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8); // zera os 64 bits finais (identificador de interface)
        return new IPAddress(bytes).ToString();
    }
}
