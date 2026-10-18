using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;

namespace MangaPipeline.Core;

internal static class TomlDefinitionCodec
{
    public static (byte[] Bytes, DocumentDefinition Document, string Hash) Prepare(DocumentDefinition document)
    {
        var bytes = Serialize(document);
        var normalized = Deserialize(Encoding.UTF8.GetString(bytes));
        return (bytes, normalized, JsonSupport.Hash(normalized));
    }

    public static DocumentDefinition Deserialize(string text)
    {
        var root = TomlSerializer.Deserialize<TomlTable>(text)
            ?? throw new WorkspaceException(WorkspaceErrorCode.Validation, "TOML definition is empty.");
        var content = root.TryGetValue("content", out var contentValue) && contentValue is TomlTable contentTable
            ? ToJsonObject(contentTable)
            : throw new WorkspaceException(WorkspaceErrorCode.Validation, "TOML definition must contain a [content] table.");
        var references = root.TryGetValue("references", out var referencesValue) && referencesValue is TomlArray referencesArray
            ? referencesArray.Select(value => value as string
                ?? throw new WorkspaceException(WorkspaceErrorCode.Validation, "TOML definition references must be strings.")).ToList()
            : throw new WorkspaceException(WorkspaceErrorCode.Validation, "TOML definition must contain a references array.");

        return new DocumentDefinition(
            ReadString(root, "id"),
            ReadString(root, "kind"),
            ReadString(root, "scope"),
            ReadInt32(root, "schemaVersion"),
            content,
            references);
    }

    private static byte[] Serialize(DocumentDefinition document)
    {
        var root = new TomlTable
        {
            ["id"] = document.Id,
            ["kind"] = document.Kind,
            ["scope"] = document.Scope,
            ["schemaVersion"] = (long)document.SchemaVersion,
            ["content"] = ToTomlTable(document.Content),
            ["references"] = new TomlArray()
        };
        var references = (TomlArray)root["references"]!;
        foreach (var reference in document.References)
        {
            references.Add(reference);
        }

        return Encoding.UTF8.GetBytes(TomlSerializer.Serialize(root));
    }

    private static string ReadString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string text
            ? text
            : throw new WorkspaceException(WorkspaceErrorCode.Validation, $"TOML definition field '{key}' must be a string.");

    private static int ReadInt32(TomlTable table, string key)
    {
        if (table.TryGetValue(key, out var value) &&
            value is long integer &&
            integer is >= int.MinValue and <= int.MaxValue)
        {
            return (int)integer;
        }

        throw new WorkspaceException(WorkspaceErrorCode.Validation, $"TOML definition field '{key}' must be an integer.");
    }

    private static TomlTable ToTomlTable(JsonObject value)
    {
        var table = new TomlTable();
        foreach (var (key, node) in value)
        {
            table.Add(key, ToTomlValue(node));
        }

        return table;
    }

    private static object ToTomlValue(JsonNode? node)
    {
        if (node is null)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "TOML definitions cannot contain null values.");
        }

        if (node is JsonObject table)
        {
            return ToTomlTable(table);
        }

        if (node is JsonArray array)
        {
            if (array.Count > 0 && array.All(item => item is JsonObject))
            {
                var tableArray = new TomlTableArray();
                foreach (var item in array)
                {
                    tableArray.Add(ToTomlTable((JsonObject)item!));
                }

                return tableArray;
            }

            var tomlArray = new TomlArray();
            foreach (var item in array)
            {
                tomlArray.Add(ToTomlValue(item));
            }

            return tomlArray;
        }

        var element = JsonSerializer.SerializeToElement(node);
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when double.IsFinite(element.GetDouble()) => element.GetDouble(),
            _ => throw new WorkspaceException(WorkspaceErrorCode.Validation, "TOML definitions contain an unsupported value.")
        };
    }

    private static JsonObject ToJsonObject(TomlTable table)
    {
        var result = new JsonObject();
        foreach (var (key, value) in table)
        {
            result.Add(key, ToJsonNode(value));
        }

        return result;
    }

    private static JsonNode ToJsonNode(object? value)
    {
        switch (value)
        {
            case TomlTable table:
                return ToJsonObject(table);
            case TomlTableArray tableArray:
            {
                var result = new JsonArray();
                foreach (var table in tableArray)
                {
                    result.Add(ToJsonObject(table));
                }

                return result;
            }
            case TomlArray array:
            {
                var result = new JsonArray();
                foreach (var item in array)
                {
                    result.Add(ToJsonNode(item));
                }

                return result;
            }
            case string or bool or long or int or double or decimal or DateTime or DateTimeOffset or DateOnly or TimeOnly:
                return JsonSerializer.SerializeToNode(value)
                    ?? throw new WorkspaceException(WorkspaceErrorCode.Validation, "TOML definition contains an unsupported value.");
            default:
                throw new WorkspaceException(WorkspaceErrorCode.Validation, "TOML definition contains an unsupported value.");
        }
    }
}
