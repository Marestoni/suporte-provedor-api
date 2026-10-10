using SuporteProvedor.Api.Domain;

namespace SuporteProvedor.Tests;

public class RegrasDesbloqueioTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Libera_por_3_dias_quando_bloqueado_e_sem_uso_recente()
    {
        var r = RegrasDesbloqueio.Avaliar("bloqueado_inadimplencia", null, Agora);
        Assert.True(r.Liberado);
        Assert.Equal(Agora.AddDays(3), r.LiberadoAte);
    }

    [Fact]
    public void Nega_quando_nao_esta_bloqueado()
    {
        Assert.False(RegrasDesbloqueio.Avaliar("ativo", null, Agora).Liberado);
    }

    [Fact]
    public void Nega_quando_usado_ha_menos_de_30_dias()
    {
        Assert.False(RegrasDesbloqueio.Avaliar("bloqueado_inadimplencia", Agora.AddDays(-10), Agora).Liberado);
    }

    [Fact]
    public void Libera_quando_ultimo_uso_foi_ha_mais_de_30_dias()
    {
        Assert.True(RegrasDesbloqueio.Avaliar("bloqueado_inadimplencia", Agora.AddDays(-31), Agora).Liberado);
    }
}
