using System.ComponentModel;

namespace PostGISApi.Configuration;

/// <summary>A PostgreSQL/PostGIS database connection (connections.json).</summary>
public sealed class ConnectionConfig
{
    [Description("Unique name of the connection (letters, digits, '_' and '-').")]
    public string Name { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";

    [Description("Password in plain text. Prefer PasswordEnvVar for production.")]
    public string? Password { get; set; }

    [Description("Name of an environment variable that holds the password (takes precedence over Password).")]
    public string? PasswordEnvVar { get; set; }

    [Description("Disable, Allow, Prefer, Require, VerifyCA or VerifyFull.")]
    public string SslMode { get; set; } = "Prefer";
    public int MaxPoolSize { get; set; } = 20;
    public int CommandTimeoutSeconds { get; set; } = 30;
}

/// <summary>A table or view with a geometry column that is exposed as layer (layers.json).</summary>
public sealed class LayerConfig
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string Connection { get; set; } = "";
    public string Schema { get; set; } = "public";
    public string Table { get; set; } = "";
    public string GeometryColumn { get; set; } = "geom";

    [Description("Primary key column. Required for feature-by-id access and write operations.")]
    public string? IdColumn { get; set; } = "id";

    [Description("SRID of the geometry column. 0 = detect automatically.")]
    public int Srid { get; set; }

    [Description("Exposed attribute columns. Empty = all non-geometry columns.")]
    public List<string> Properties { get; set; } = [];

    [Description("If false, the layer can be modified via POST/PUT/DELETE (role 'write').")]
    public bool ReadOnly { get; set; } = true;

    [Description("If true, the layer can be read without authentication.")]
    public bool Public { get; set; }

    public int DefaultLimit { get; set; } = 100;
    public int MaxFeatures { get; set; } = 5000;
    public bool EnableTiles { get; set; } = true;
    public int MaxTileFeatures { get; set; } = 10000;
}

/// <summary>A parameter of a named query.</summary>
public sealed class QueryParameterConfig
{
    [Description("Parameter name, referenced as @name in the SQL.")]
    public string Name { get; set; } = "";

    [Description("string, integer, bigint, number, boolean, date, timestamp, uuid, json, geojson")]
    public string Type { get; set; } = "string";
    public bool Required { get; set; } = true;
    public string? Default { get; set; }
    public string? Description { get; set; }
}

/// <summary>A predefined, parameterised SQL query (queries.json).</summary>
public sealed class NamedQueryConfig
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string Connection { get; set; } = "";

    [Description("A single SELECT statement. Parameters are written as @name.")]
    public string Sql { get; set; } = "";
    public List<QueryParameterConfig> Parameters { get; set; } = [];

    [Description("json (array of rows) or geojson (FeatureCollection).")]
    public string Format { get; set; } = "json";

    [Description("Geometry column of the result (format geojson).")]
    public string? GeometryColumn { get; set; }

    [Description("Id column of the result (format geojson, optional).")]
    public string? IdColumn { get; set; }

    [Description("If false the query runs in a READ ONLY transaction.")]
    public bool AllowWrite { get; set; }

    public bool Public { get; set; }
}

public sealed class ApiKeyConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    [Description("SHA-256 (hex) of the key. The plain key is never stored.")]
    public string KeyHash { get; set; } = "";

    [Description("First characters of the key, for identification only.")]
    public string KeyPrefix { get; set; } = "";

    [Description("read, write and/or admin.")]
    public List<string> Roles { get; set; } = ["read"];

    [Description("Layers this key may access. Empty = all.")]
    public List<string> AllowedLayers { get; set; } = [];

    [Description("Named queries this key may execute. Empty = all.")]
    public List<string> AllowedQueries { get; set; } = [];

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresUtc { get; set; }
}

public sealed class OidcConfig
{
    public bool Enabled { get; set; }

    [Description("Issuer / authority URL, e.g. https://login.example.com/realms/gis")]
    public string? Authority { get; set; }

    [Description("Expected audience. Empty = audience is not validated.")]
    public string? Audience { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;

    [Description("Claim that contains roles. Dotted paths into JSON claims are supported, e.g. realm_access.roles")]
    public string RoleClaimType { get; set; } = "roles";

    [Description("Also evaluate the space separated 'scope' / 'scp' claim.")]
    public bool UseScopes { get; set; } = true;

    public List<string> ReadRoles { get; set; } = ["gis.read"];
    public List<string> WriteRoles { get; set; } = ["gis.write"];
    public List<string> AdminRoles { get; set; } = ["gis.admin"];

    [Description("Roles every valid token gets, e.g. [\"read\"].")]
    public List<string> DefaultRoles { get; set; } = [];
}

public sealed class SecurityConfig
{
    public List<ApiKeyConfig> ApiKeys { get; set; } = [];
    public OidcConfig Oidc { get; set; } = new();
}

public sealed class GeneralSettings
{
    [Description("Connection used for /geometry operations. Empty = first connection.")]
    public string? DefaultConnection { get; set; }

    [Description("Allowed CORS origins, '*' for any.")]
    public List<string> CorsOrigins { get; set; } = [];

    [Description("Allow ?api_key=... in the URL (needed by some map clients for tiles).")]
    public bool AllowApiKeyInQuery { get; set; }

    public bool EnableSwaggerUi { get; set; } = true;

    [Description("Return database error messages to clients.")]
    public bool DetailedErrors { get; set; }

    public int TileCacheSeconds { get; set; } = 300;
}

/// <summary>The complete configuration (all files).</summary>
public sealed class ConfigData
{
    public List<ConnectionConfig> Connections { get; set; } = [];
    public List<LayerConfig> Layers { get; set; } = [];
    public List<NamedQueryConfig> Queries { get; set; } = [];
    public SecurityConfig Security { get; set; } = new();
    public GeneralSettings Settings { get; set; } = new();

    public ConnectionConfig? FindConnection(string name) =>
        Connections.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public LayerConfig? FindLayer(string name) =>
        Layers.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public NamedQueryConfig? FindQuery(string name) =>
        Queries.FirstOrDefault(q => q.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
