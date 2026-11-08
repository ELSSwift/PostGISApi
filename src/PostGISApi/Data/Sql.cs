using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace PostGISApi.Data;

/// <summary>SQL helpers. Identifiers are always quoted; values are always passed as parameters.</summary>
public static class Sql
{
    public static string Ident(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string Table(string schema, string table) => Ident(schema) + "." + Ident(table);

    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Builds a jsonb object from columns of alias <paramref name="alias"/> (chunked: max 100 function args).</summary>
    public static string JsonbObject(IEnumerable<string> columns, string alias = "t")
    {
        var parts = columns
            .Chunk(50)
            .Select(chunk => "jsonb_build_object(" +
                             string.Join(", ", chunk.Select(c => $"{Literal(c)}, {alias}.{Ident(c)}")) + ")")
            .ToList();
        return parts.Count == 0 ? "'{}'::jsonb" : string.Join(" || ", parts);
    }
}

/// <summary>Collects parameters while building a statement.</summary>
public sealed class SqlParams
{
    private readonly List<NpgsqlParameter> _parameters = [];

    public string Add(object? value, NpgsqlDbType? type = null)
    {
        var name = "p" + _parameters.Count.ToString(CultureInfo.InvariantCulture);
        var p = type is null ? new NpgsqlParameter(name, value ?? DBNull.Value) : new NpgsqlParameter(name, type.Value) { Value = value ?? DBNull.Value };
        _parameters.Add(p);
        return "@" + name;
    }

    /// <summary>Adds a JSON value as text parameter cast to the column type.</summary>
    public string AddTyped(JsonElement value, ColumnInfo column)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return $"CAST({Add(DBNull.Value, NpgsqlDbType.Text)} AS {column.SqlType})";

        if (value.ValueKind == JsonValueKind.Array && column.IsArray)
        {
            var items = value.EnumerateArray().Select(JsonToText).ToArray();
            return $"CAST({Add(items, NpgsqlDbType.Array | NpgsqlDbType.Text)} AS {column.SqlType})";
        }
        return $"CAST({Add(JsonToText(value), NpgsqlDbType.Text)} AS {column.SqlType})";
    }

    public string AddTyped(string? value, ColumnInfo column) =>
        $"CAST({Add((object?)value ?? DBNull.Value, NpgsqlDbType.Text)} AS {column.SqlType})";

    public static string? JsonToText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetRawText(),
    };

    public void ApplyTo(NpgsqlCommand cmd)
    {
        foreach (var p in _parameters) cmd.Parameters.Add(p);
    }
}
