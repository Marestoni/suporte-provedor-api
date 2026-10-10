using Npgsql;

namespace SuporteProvedor.Tests;

/// <summary>
/// Testes de integração contra um PostgreSQL real. Defina SUPORTE_TEST_PG com uma connection string administrativa
/// (ex.: "Host=localhost;Username=postgres;Password=...;Database=postgres"). Sem a variável, os testes são pulados.
/// Cada execução cria um banco temporário, aplica db/*.sql e o apaga no final.
/// </summary>
public class BancoFixture : IAsyncLifetime
{
    public static string? ConexaoAdmin => Environment.GetEnvironmentVariable("SUPORTE_TEST_PG");

    private string? _nomeBanco;
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (ConexaoAdmin is null) return;

        _nomeBanco = "suporte_test_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = new NpgsqlDataSourceBuilder(ConexaoAdmin).Build())
            await using (var cmd = admin.CreateCommand($"CREATE DATABASE {_nomeBanco} ENCODING 'UTF8'"))
                await cmd.ExecuteNonQueryAsync();

        var cs = new NpgsqlConnectionStringBuilder(ConexaoAdmin) { Database = _nomeBanco }.ConnectionString;
        DataSource = NpgsqlDataSource.Create(cs);

        foreach (var script in Directory.GetFiles(PastaDb(), "*.sql").OrderBy(f => f))
        {
            await using var cmd = DataSource.CreateCommand(await File.ReadAllTextAsync(script));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_nomeBanco is null) return;
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlDataSourceBuilder(ConexaoAdmin!).Build();
        await using var cmd = admin.CreateCommand($"DROP DATABASE IF EXISTS {_nomeBanco} WITH (FORCE)");
        await cmd.ExecuteNonQueryAsync();
    }

    private static string PastaDb()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "db", "001_schema.sql")))
                return Path.Combine(dir.FullName, "db");
        throw new DirectoryNotFoundException("Pasta db/ não encontrada acima de " + AppContext.BaseDirectory);
    }
}

/// <summary>[Fact] que é pulado quando SUPORTE_TEST_PG não está definido.</summary>
public sealed class FactBancoAttribute : FactAttribute
{
    public FactBancoAttribute()
    {
        if (BancoFixture.ConexaoAdmin is null) Skip = "Defina SUPORTE_TEST_PG para rodar os testes de integração.";
    }
}

[CollectionDefinition("banco")]
public class BancoCollection : ICollectionFixture<BancoFixture>;