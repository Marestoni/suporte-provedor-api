namespace SuporteProvedor.Api.Domain;

/// <summary>Datas mostradas ao cliente no horário de Brasília, independente do fuso do servidor (em produção costuma ser UTC).</summary>
public static class FusoBrasil
{
    private static readonly TimeZoneInfo Fuso = Obter();

    private static TimeZoneInfo Obter()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília");
    }

    public static string Formatar(DateTime utc, string formato) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Fuso)
            .ToString(formato, System.Globalization.CultureInfo.InvariantCulture); // '/' literal, independente da cultura do servidor

    public static string Formatar(DateTimeOffset momento, string formato) => Formatar(momento.UtcDateTime, formato);
}
