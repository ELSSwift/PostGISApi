using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PostGISApi.Infrastructure;

namespace PostGISApi.Configuration;

/// <summary>
/// Loads and saves the configuration from/to JSON files in the config directory.
/// The current configuration is an immutable snapshot; changes are made via <see cref="Update"/>.
/// Files edited by hand are picked up automatically.
/// </summary>
public sealed partial class ConfigStore : IDisposable
{
    public const string ConnectionsFile = "connections.json";
    public const string LayersFile = "layers.json";
    public const string QueriesFile = "queries.json";
    public const string SecurityFile = "security.json";
    public const string SettingsFile = "settings.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<ConfigStore> _logger;
    private readonly Lock _sync = new();
    private readonly FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private DateTime _ignoreWatcherUntil = DateTime.MinValue;
    private volatile ConfigData _current = new();

    public ConfigStore(string directory, ILogger<ConfigStore> logger)
    {
        _logger = logger;
        Directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
        Reload(throwOnError: true);

        try
        {
            _watcher = new FileSystemWatcher(Directory, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File watcher for {Dir} could not be started; use POST /api/admin/reload after manual edits", Directory);
        }
    }

    public string Directory { get; }

    /// <summary>Current configuration. Must not be modified by callers.</summary>
    public ConfigData Current => _current;

    /// <summary>Raised after the configuration has changed (admin update or file reload).</summary>
    public event Action<ConfigData>? Changed;

    /// <summary>Applies a change to a copy of the configuration, validates, saves and activates it.</summary>
    public ConfigData Update(Action<ConfigData> mutate)
    {
        ConfigData updated;
        lock (_sync)
        {
            updated = Clone(_current);
            mutate(updated);
            Validate(updated);
            Save(updated);
            _current = updated;
        }
        Changed?.Invoke(updated);
        return updated;
    }

    /// <summary>Reloads all files from disk.</summary>
    public void Reload(bool throwOnError = false)
    {
        ConfigData loaded;
        lock (_sync)
        {
            try
            {
                loaded = new ConfigData
                {
                    Connections = ReadFile(ConnectionsFile, () => new List<ConnectionConfig>()),
                    Layers = ReadFile(LayersFile, () => new List<LayerConfig>()),
                    Queries = ReadFile(QueriesFile, () => new List<NamedQueryConfig>()),
                    Security = ReadFile(SecurityFile, () => new SecurityConfig()),
                    Settings = ReadFile(SettingsFile, () => new GeneralSettings()),
                };
                Validate(loaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Configuration in {Dir} is invalid, keeping previous configuration", Directory);
                if (throwOnError) throw;
                return;
            }
            _current = loaded;
        }
        _logger.LogInformation("Configuration loaded from {Dir}: {Connections} connections, {Layers} layers, {Queries} queries",
            Directory, loaded.Connections.Count, loaded.Layers.Count, loaded.Queries.Count);
        Changed?.Invoke(loaded);
    }

    public static ConfigData Clone(ConfigData data) =>
        JsonSerializer.Deserialize<ConfigData>(JsonSerializer.Serialize(data, JsonOptions), JsonOptions)!;

    [GeneratedRegex("^[A-Za-z0-9_\\-]{1,100}$")]
    public static partial Regex NameRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex ParameterNameRegex();

    private static readonly HashSet<string> ValidParameterTypes =
        ["string", "integer", "bigint", "number", "boolean", "date", "timestamp", "uuid", "json", "geojson"];

    private static readonly HashSet<string> ValidRoles = ["read", "write", "admin"];

    public static void Validate(ConfigData data)
    {
        CheckNames(data.Connections.Select(c => c.Name), "connection");
        CheckNames(data.Layers.Select(c => c.Name), "layer");
        CheckNames(data.Queries.Select(c => c.Name), "query");

        foreach (var c in data.Connections)
        {
            if (string.IsNullOrWhiteSpace(c.Host)) throw ApiException.BadRequest($"Connection '{c.Name}': host is required.");
            if (string.IsNullOrWhiteSpace(c.Database)) throw ApiException.BadRequest($"Connection '{c.Name}': database is required.");
            if (string.IsNullOrWhiteSpace(c.Username)) throw ApiException.BadRequest($"Connection '{c.Name}': username is required.");
            if (!Enum.TryParse<Npgsql.SslMode>(c.SslMode, true, out _)) throw ApiException.BadRequest($"Connection '{c.Name}': invalid sslMode '{c.SslMode}'.");
        }

        foreach (var l in data.Layers)
        {
            if (data.FindConnection(l.Connection) is null) throw ApiException.BadRequest($"Layer '{l.Name}': unknown connection '{l.Connection}'.");
            if (string.IsNullOrWhiteSpace(l.Table)) throw ApiException.BadRequest($"Layer '{l.Name}': table is required.");
            if (string.IsNullOrWhiteSpace(l.Schema)) throw ApiException.BadRequest($"Layer '{l.Name}': schema is required.");
            if (string.IsNullOrWhiteSpace(l.GeometryColumn)) throw ApiException.BadRequest($"Layer '{l.Name}': geometryColumn is required.");
            if (!l.ReadOnly && string.IsNullOrWhiteSpace(l.IdColumn)) throw ApiException.BadRequest($"Layer '{l.Name}': writable layers need an idColumn.");
            if (l.DefaultLimit < 1 || l.MaxFeatures < 1 || l.DefaultLimit > l.MaxFeatures)
                throw ApiException.BadRequest($"Layer '{l.Name}': 1 <= defaultLimit <= maxFeatures required.");
        }

        foreach (var q in data.Queries)
        {
            if (data.FindConnection(q.Connection) is null) throw ApiException.BadRequest($"Query '{q.Name}': unknown connection '{q.Connection}'.");
            if (string.IsNullOrWhiteSpace(q.Sql)) throw ApiException.BadRequest($"Query '{q.Name}': sql is required.");
            if (q.Format is not ("json" or "geojson")) throw ApiException.BadRequest($"Query '{q.Name}': format must be 'json' or 'geojson'.");
            if (q.Format == "geojson" && string.IsNullOrWhiteSpace(q.GeometryColumn))
                throw ApiException.BadRequest($"Query '{q.Name}': geometryColumn is required for format geojson.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in q.Parameters)
            {
                if (!ParameterNameRegex().IsMatch(p.Name)) throw ApiException.BadRequest($"Query '{q.Name}': invalid parameter name '{p.Name}'.");
                if (!seen.Add(p.Name)) throw ApiException.BadRequest($"Query '{q.Name}': duplicate parameter '{p.Name}'.");
                if (!ValidParameterTypes.Contains(p.Type)) throw ApiException.BadRequest($"Query '{q.Name}': invalid parameter type '{p.Type}'.");
            }
        }

        foreach (var k in data.Security.ApiKeys)
        {
            if (k.Roles.Any(r => !ValidRoles.Contains(r))) throw ApiException.BadRequest($"API key '{k.Name}': roles must be read, write or admin.");
            if (string.IsNullOrWhiteSpace(k.KeyHash)) throw ApiException.BadRequest($"API key '{k.Name}': keyHash missing.");
        }

        var oidc = data.Security.Oidc;
        if (oidc.Enabled && !Uri.TryCreate(oidc.Authority, UriKind.Absolute, out _))
            throw ApiException.BadRequest("OIDC: a valid authority URL is required when enabled.");
        foreach (var r in oidc.DefaultRoles)
            if (!ValidRoles.Contains(r)) throw ApiException.BadRequest("OIDC: defaultRoles must be read, write or admin.");

        if (!string.IsNullOrEmpty(data.Settings.DefaultConnection) && data.FindConnection(data.Settings.DefaultConnection) is null)
            throw ApiException.BadRequest($"Settings: unknown defaultConnection '{data.Settings.DefaultConnection}'.");
    }

    private static void CheckNames(IEnumerable<string> names, string kind)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (!NameRegex().IsMatch(n ?? "")) throw ApiException.BadRequest($"Invalid {kind} name '{n}' (allowed: letters, digits, '_', '-').");
            if (!set.Add(n!)) throw ApiException.BadRequest($"Duplicate {kind} name '{n}'.");
        }
    }

    private T ReadFile<T>(string file, Func<T> create) where T : class
    {
        var path = Path.Combine(Directory, file);
        if (!File.Exists(path))
        {
            var value = create();
            WriteFile(file, value);
            return value;
        }
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return create();
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? create();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{file}: {ex.Message}", ex);
        }
    }

    private void Save(ConfigData data)
    {
        _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(2);
        WriteFile(ConnectionsFile, data.Connections);
        WriteFile(LayersFile, data.Layers);
        WriteFile(QueriesFile, data.Queries);
        WriteFile(SecurityFile, data.Security);
        WriteFile(SettingsFile, data.Settings);
    }

    private void WriteFile<T>(string file, T value)
    {
        var path = Path.Combine(Directory, file);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (File.Exists(path) && File.ReadAllText(path) == json) return;
        _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(2);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name?.EndsWith(".tmp", StringComparison.Ordinal) == true) return;
        if (DateTime.UtcNow < _ignoreWatcherUntil) return;
        // debounce: editors write files in several steps
        _reloadTimer?.Dispose();
        _reloadTimer = new Timer(_ =>
        {
            _logger.LogInformation("Config file {File} changed on disk, reloading", e.Name);
            Reload();
        }, null, 750, Timeout.Infinite);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
    }
}
