namespace SuporteProvedor.Api.Domain;

/// <summary>
/// Regra do desbloqueio em confiança. Fica no código (não no prompt):
/// só para bloqueado por inadimplência, 1 por mês, até 3 dias.
/// </summary>
public static class RegrasDesbloqueio
{
    public const int DiasLiberados = 3;
    public const int JanelaDias = 30;

    public static ResultadoDesbloqueio Avaliar(string situacao, DateTimeOffset? ultimoDesbloqueio, DateTimeOffset agora)
    {
        if (situacao != "bloqueado_inadimplencia")
            return new(false, "A conexão não está bloqueada por inadimplência.", null);

        if (ultimoDesbloqueio is { } ultimo && agora - ultimo < TimeSpan.FromDays(JanelaDias))
            return new(false, $"O desbloqueio em confiança já foi usado nos últimos {JanelaDias} dias.", null);

        return new(true, "Desbloqueio em confiança liberado.", agora.AddDays(DiasLiberados));
    }
}
