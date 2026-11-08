using System.Collections.Concurrent;
using Npgsql;
using PostGISApi.Configuration;
using PostGISApi.Infrastructure;

namespace PostGISApi.Data;

/// <summary>Keeps one pooled <see cref="NpgsqlDataSource"/> per configured connection.</summary>
public sealed class DataSourceRegistry : IDisposable
{
    private readonly ConfigStore _store;
    private readonly ConcurrentDictionary<string, (string ConnectionString, NpgsqlDataSource DataSource)> _sources =
        new(StringComparer.OrdinalIgnoreCase);

    public DataSourceRegistry(ConfigStore store)
    {
        _store = store;
        _store.Changed += OnConfigChanged;
    }

    public NpgsqlDataSource Get(string connectionName)
    {
        var config = _store.Current.FindConnection(connectionName)
            ?? throw ApiException.NotFound($"Connection '{connectionName}' is not configured.");
        var cs = BuildConnectionString(config);
        if (_sources.TryGetValue(config.Name, out var existing) && existing.ConnectionString == cs)
            return existing.DataSource;

        var created = NpgsqlDataSource.Create(cs);
        _sources[config.Name] = (cs, created);
        if (existing.DataSource is not null) DisposeLater(existing.DataSource);
        return created;
    }

    public async Task<NpgsqlConnection> OpenAsync(string connectionName, CancellationToken ct) =>
        await Get(connectionName).OpenConnectionAsync(ct);

    public static string BuildConnectionString(ConnectionConfig c)
    {
        var password = !string.IsNullOrEmpty(c.PasswordEnvVar)
            ? Environment.GetEnvironmentVariable(c.PasswordEnvVar)
            : c.Password;
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = c.Host,
            Port = c.Port,
            Database = c.Database,
            Username = c.Username,
            Password = password,
            SslMode = Enum.Parse<SslMode>(c.SslMode, true),
            MaxPoolSize = Math.Max(1, c.MaxPoolSize),
            CommandTimeout = Math.Max(1, c.CommandTimeoutSeconds),
            ApplicationName = "PostGISApi",
            Timeout = 15,
        };
        return b.ConnectionString;
    }

    private void OnConfigChanged(ConfigData data)
    {
        foreach (var name in _sources.Keys)
        {
            if (data.FindConnection(name) is null && _sources.TryRemove(name, out var removed))
                DisposeLater(removed.DataSource);
        }
    }

    private static void DisposeLater(NpgsqlDataSource ds) =>
        // give in-flight requests time to finish before the pool is closed
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => ds.DisposeAsync().AsTask());

    public void Dispose()
    {
        _store.Changed -= OnConfigChanged;
        foreach (var s in _sources.Values) s.DataSource.Dispose();
        _sources.Clear();
    }
}
