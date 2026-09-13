using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TreasureMaps.Api;

/// <summary>
/// Reads a value that may be either a comma-separated <b>string</b> or a JSON <b>array</b>
/// (of strings, or of objects with a <c>name</c> property) into a list of strings. Real Treasure-Maps
/// responses vary between these shapes for fields like <c>genres</c>, so this keeps deserialization
/// from failing the whole response.
/// </summary>
public sealed class StringOrArrayConverter : JsonConverter<List<string>?>
{
    /// <inheritdoc />
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement;
        var list = new List<string>();

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                var raw = element.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        list.Add(part);
                    }
                }

                return list;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            list.Add(s.Trim());
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name))
                    {
                        var s = name.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            list.Add(s.Trim());
                        }
                    }
                }

                return list;
            default:
                return list;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var v in value)
        {
            writer.WriteStringValue(v);
        }

        writer.WriteEndArray();
    }
}
