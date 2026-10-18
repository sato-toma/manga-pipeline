using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MangaPipeline.Core;

public static class DefinitionValidator
{
    public static void Validate(IReadOnlyCollection<DocumentDefinition> documents)
    {
        if (documents is null || documents.Count == 0 || documents.Any(document => document is null))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "No definition documents were supplied.");
        }

        var byId = new Dictionary<string, DocumentDefinition>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            ValidateDocument(document);
            if (!byId.TryAdd(document.Id, document))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Duplicate document id '{document.Id}'.");
            }
        }

        foreach (var document in documents)
        {
            foreach (var reference in document.References)
            {
                if (!byId.ContainsKey(reference))
                {
                    throw new WorkspaceException(
                        WorkspaceErrorCode.Validation,
                        $"Document '{document.Id}' references missing document '{reference}'.");
                }
            }
        }

        if (!byId.TryGetValue("graph", out var graphDocument))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "A 'graph' definition document is required.");
        }

        ValidateGraph(graphDocument, byId.Keys);
    }

    public static void ValidateDocument(DocumentDefinition document)
    {
        if (document is null ||
            string.IsNullOrWhiteSpace(document.Id) ||
            document.Id is "." or ".." ||
            document.Id.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-') ||
            string.IsNullOrWhiteSpace(document.Kind) ||
            string.IsNullOrWhiteSpace(document.Scope) ||
            document.SchemaVersion != 1 ||
            document.Content is null ||
            document.References is null ||
            document.References.Any(string.IsNullOrWhiteSpace))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "Document has invalid metadata.");
        }
    }

    public static GenerationGraph ValidateGraph(
        DocumentDefinition graphDocument,
        IEnumerable<string> availableDocumentIds)
    {
        if (graphDocument.Id != "graph" || graphDocument.Kind != "generation-graph")
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "The graph document must have id 'graph' and kind 'generation-graph'.");
        }

        var graph = graphDocument.Content.Deserialize<GenerationGraph>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new WorkspaceException(WorkspaceErrorCode.Validation, "Generation graph is empty.");
        var availableIds = new HashSet<string>(availableDocumentIds, StringComparer.Ordinal);
        var nodes = new Dictionary<string, GenerationNodeDefinition>(StringComparer.Ordinal);
        if (graph.Nodes is null || graph.Nodes.Count == 0)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "Generation graph must contain at least one node.");
        }

        foreach (var node in graph.Nodes)
        {
            if (node is null ||
                string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Kind) ||
                node.Version < 1 ||
                node.InputDocumentIds is null ||
                node.DependsOnNodeIds is null ||
                node.InputDocumentIds.Any(string.IsNullOrWhiteSpace) ||
                node.DependsOnNodeIds.Any(string.IsNullOrWhiteSpace) ||
                !nodes.TryAdd(node.Id, node))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Validation, "Graph node has invalid or duplicate metadata.");
            }

            foreach (var inputDocumentId in node.InputDocumentIds)
            {
                if (!availableIds.Contains(inputDocumentId))
                {
                    throw new WorkspaceException(
                        WorkspaceErrorCode.Validation,
                        $"Graph node '{node.Id}' references missing document '{inputDocumentId}'.");
                }
            }
        }

        foreach (var node in graph.Nodes)
        {
            foreach (var dependencyId in node.DependsOnNodeIds)
            {
                if (!nodes.ContainsKey(dependencyId))
                {
                    throw new WorkspaceException(
                        WorkspaceErrorCode.Validation,
                        $"Graph node '{node.Id}' depends on missing node '{dependencyId}'.");
                }
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Generation graph contains a cycle at '{id}'.");
            }

            foreach (var dependencyId in nodes[id].DependsOnNodeIds)
            {
                Visit(dependencyId);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var id in nodes.Keys)
        {
            Visit(id);
        }

        return graph;
    }
}
