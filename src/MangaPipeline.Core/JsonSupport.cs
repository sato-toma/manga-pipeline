using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MangaPipeline.Core;

internal static class JsonSupport
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static byte[] SerializeCanonical<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(node, writer);
        }

        return stream.ToArray();
    }

    public static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(SerializeCanonical(value))).ToLowerInvariant();

    private static void WriteCanonical(JsonNode? node, Utf8JsonWriter writer)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var value in array)
                {
                    WriteCanonical(value, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer, Options);
                break;
        }
    }
}
