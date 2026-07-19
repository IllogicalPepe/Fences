using System.Text.Json;
using System.Text.Json.Serialization;

namespace FenceDesk.Services;

/// <summary>
/// PowerShell ConvertTo-Json turns single-element arrays into objects, and empty
/// arrays into {} sometimes. Accept array, single object, or empty object.
/// </summary>
public sealed class FlexibleListConverter<T> : JsonConverter<List<T>>
{
    public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<T>();
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return list;
            case JsonTokenType.StartArray:
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var item = JsonSerializer.Deserialize<T>(ref reader, options);
                    if (item is not null) list.Add(item);
                }
                return list;
            case JsonTokenType.StartObject:
                // Single object OR empty object {}
                using (var doc = JsonDocument.ParseValue(ref reader))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        // Empty object from PS empty array
                        if (!doc.RootElement.EnumerateObject().Any())
                            return list;
                        var item = doc.RootElement.Deserialize<T>(options);
                        if (item is not null) list.Add(item);
                    }
                }
                return list;
            default:
                reader.Skip();
                return list;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
    {
        // Do not call Serialize(List) — that re-enters this converter and can stack-overflow.
        writer.WriteStartArray();
        foreach (var item in value)
            JsonSerializer.Serialize(writer, item, options);
        writer.WriteEndArray();
    }
}
