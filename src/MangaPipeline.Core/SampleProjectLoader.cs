using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tomlyn;

namespace MangaPipeline.Core;

public static class SampleProjectLoader
{
    public static async Task<List<DocumentDefinition>> LoadAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Sample project directory '{fullPath}' does not exist.");
        }

        var documents = new List<DocumentDefinition>();
        foreach (var path in Directory.EnumerateFiles(fullPath, "*.toml").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            DocumentDefinition document;
            try
            {
                document = TomlDefinitionCodec.Deserialize(text);
            }
            catch (TomlException exception)
            {
                throw new WorkspaceException(
                    WorkspaceErrorCode.Validation,
                    $"Sample file '{Path.GetFileName(path)}' contains invalid TOML: {exception.Message}");
            }

            documents.Add(document);
        }

        if (documents.Count == 0)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "Sample project contains no definition files.");
        }

        DefinitionValidator.Validate(documents);
        return documents;
    }
}
