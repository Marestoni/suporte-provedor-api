using SuporteProvedor.Api.Domain;

namespace SuporteProvedor.Tests;

public class PoliticaIdentificacaoTests
{
    [Fact]
    public void Permite_ate_o_limite()
    {
        Assert.False(PoliticaIdentificacao.Bloqueado(
            PoliticaIdentificacao.MaxPorCpfEIp, PoliticaIdentificacao.MaxPorIp, PoliticaIdentificacao.MaxPorCpf));
    }

    [Fact]
    public void Bloqueia_um_ip_que_martela_o_mesmo_cpf()
    {
        Assert.True(PoliticaIdentificacao.Bloqueado(PoliticaIdentificacao.MaxPorCpfEIp + 1, 6, 6));
    }

    [Fact]
    public void Bloqueia_um_ip_que_varre_varios_cpfs()
    {
        Assert.True(PoliticaIdentificacao.Bloqueado(1, PoliticaIdentificacao.MaxPorIp + 1, 1));
    }

    [Fact]
    public void Terceiro_com_poucas_tentativas_nao_trava_a_vitima()
    {
        // Atacante gastou 5 tentativas no CPF a partir do IP dele; a vítima, em outro IP, tem 1 no par (CPF, IP)
        // e o total por CPF (5) está longe do teto.
        Assert.False(PoliticaIdentificacao.Bloqueado(porCpfEIp: 1, porIp: 1, porCpf: 5));
    }

    [Fact]
    public void Ataque_distribuido_estoura_o_teto_por_cpf()
    {
        Assert.True(PoliticaIdentificacao.Bloqueado(1, 1, PoliticaIdentificacao.MaxPorCpf + 1));
    }
}