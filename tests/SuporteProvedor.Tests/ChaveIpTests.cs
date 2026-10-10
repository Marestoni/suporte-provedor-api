using System.Net;
using SuporteProvedor.Api.Domain;

namespace SuporteProvedor.Tests;

public class ChaveIpTests
{
    [Fact]
    public void Ipv4_fica_como_esta() => Assert.Equal("200.10.20.30", ChaveIp.De(IPAddress.Parse("200.10.20.30")));

    [Fact]
    public void Ipv4_mapeado_em_ipv6_vira_ipv4() => Assert.Equal("200.10.20.30", ChaveIp.De(IPAddress.Parse("::ffff:200.10.20.30")));

    [Fact]
    public void Ipv6_do_mesmo_64_gera_a_mesma_chave()
    {
        var a = ChaveIp.De(IPAddress.Parse("2804:1234:5678:9abc:1111:2222:3333:4444"));
        var b = ChaveIp.De(IPAddress.Parse("2804:1234:5678:9abc:aaaa:bbbb:cccc:dddd"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Ipv6_de_outro_64_gera_chave_diferente()
    {
        Assert.NotEqual(
            ChaveIp.De(IPAddress.Parse("2804:1234:5678:9abc::1")),
            ChaveIp.De(IPAddress.Parse("2804:1234:5678:9abd::1")));
    }

    [Fact]
    public void Nulo_continua_nulo() => Assert.Null(ChaveIp.De(null));
}