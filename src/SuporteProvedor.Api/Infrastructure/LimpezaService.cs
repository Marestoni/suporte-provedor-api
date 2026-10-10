namespace SuporteProvedor.Api.Infrastructure;

/// <summary>Retenção: apaga periodicamente tentativas de identificação antigas e conversas anônimas abandonadas.</summary>
public class LimpezaService(Db db, ILogger<LimpezaService> log) : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Intervalo);
        do
        {
            try
            {
                var (tentativas, conversas) = await db.LimparAsync(stoppingToken);
                if (tentativas + conversas > 0)
                    log.LogInformation("Limpeza: {Tentativas} tentativas e {Conversas} conversas anônimas removidas", tentativas, conversas);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Falha na limpeza periódica");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
