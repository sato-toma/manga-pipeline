using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MangaPipeline.Core;

public sealed class GraphExecutionService(
    LocalProjectStore projects,
    SqliteExecutionIndex executionIndex)
{
    private const string BackendVersion = "mock-v1";

    public async Task<GenerationRunReport> ExecuteAsync(
        GenerationJob job,
        CancellationToken cancellationToken = default)
    {
        var startedAt = job.StartedAt ?? DateTimeOffset.UtcNow;
        var graphTimer = Stopwatch.StartNew();
        var graphDocument = await projects.GetDocumentAsync(
            job.ProjectId,
            job.RevisionId,
            "graph",
            cancellationToken);
        var graphBytes = await projects.GetDocumentStorageSizeAsync(
            job.ProjectId,
            job.RevisionId,
            "graph",
            cancellationToken);
        var graph = graphDocument.Content.Deserialize<GenerationGraph>(JsonSupport.Options)
            ?? throw new WorkspaceException(WorkspaceErrorCode.Validation, "Generation graph is empty.");
        var orderedNodes = OrderRequiredNodes(graph, job.TargetNodeIds);
        graphTimer.Stop();

        var nodeResults = new Dictionary<string, GenerationNodeResult>(StringComparer.Ordinal);
        var loadedDocuments = new Dictionary<string, (string Hash, long SizeBytes)>(StringComparer.Ordinal);
        var totalInputBytes = 0L;
        var totalPreparationMilliseconds = graphTimer.ElapsedMilliseconds;
        var totalGenerationMilliseconds = 0L;
        var totalPersistenceMilliseconds = 0L;

        foreach (var node in orderedNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preparationTimer = Stopwatch.StartNew();
            var inputHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var inputBytes = 0L;
            foreach (var documentId in node.InputDocumentIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!loadedDocuments.TryGetValue(documentId, out var loaded))
                {
                    var document = await projects.GetDocumentAsync(job.ProjectId, job.RevisionId, documentId, cancellationToken);
                    var sizeBytes = await projects.GetDocumentStorageSizeAsync(
                        job.ProjectId,
                        job.RevisionId,
                        documentId,
                        cancellationToken);
                    loaded = (JsonSupport.Hash(document), sizeBytes);
                    loadedDocuments.Add(documentId, loaded);
                }

                inputBytes += loaded.SizeBytes;
                inputHashes.Add(documentId, loaded.Hash);
            }

            var dependencyHashes = node.DependsOnNodeIds
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToDictionary(
                    dependencyId => dependencyId,
                    dependencyId => nodeResults[dependencyId].ContentHash,
                    StringComparer.Ordinal);
            var cacheKey = JsonSupport.Hash(new
            {
                job.ProjectId,
                Node = node,
                BackendVersion,
                InputHashes = inputHashes,
                DependencyHashes = dependencyHashes
            });
            var cached = await executionIndex.GetCacheEntryAsync(job.ProjectId, cacheKey, cancellationToken);
            if (cached is not null)
            {
                byte[] cachedBytes;
                try
                {
                    cachedBytes = await projects.ReadArtifactAsync(job.ProjectId, cached.RelativePath, cancellationToken);
                }
                catch (WorkspaceException exception) when (exception.Code == WorkspaceErrorCode.NotFound)
                {
                    await executionIndex.RemoveCacheEntryAsync(job.ProjectId, cacheKey, cancellationToken);
                    cached = null;
                    cachedBytes = [];
                }

                if (cached is not null)
                {
                    var actualHash = HashBytes(cachedBytes);
                    if (!StringComparer.Ordinal.Equals(actualHash, cached.ContentHash))
                    {
                        throw new WorkspaceException(
                            WorkspaceErrorCode.Integrity,
                            $"Cached artifact '{cached.Id}' failed its content hash check.");
                    }

                    preparationTimer.Stop();
                    totalInputBytes += inputBytes;
                    totalPreparationMilliseconds += preparationTimer.ElapsedMilliseconds;
                    nodeResults.Add(
                        node.Id,
                        new GenerationNodeResult(
                            node.Id,
                            cacheKey,
                            cached.Id,
                            cached.ContentHash,
                            inputBytes,
                            true,
                            preparationTimer.ElapsedMilliseconds,
                            0,
                            0));
                    continue;
                }
            }

            preparationTimer.Stop();
            totalInputBytes += inputBytes;
            totalPreparationMilliseconds += preparationTimer.ElapsedMilliseconds;
            var generationTimer = Stopwatch.StartNew();
            var output = JsonSerializer.SerializeToUtf8Bytes(
                new MockGenerationArtifact(
                    node.Id,
                    node.Kind,
                    BackendVersion,
                    inputHashes,
                    dependencyHashes,
                    $"Mock preview for {node.Kind} node '{node.Id}'."),
                JsonSupport.Options);
            generationTimer.Stop();
            totalGenerationMilliseconds += generationTimer.ElapsedMilliseconds;

            var persistenceTimer = Stopwatch.StartNew();
            var contentHash = HashBytes(output);
            var artifactId = Guid.NewGuid().ToString("N");
            var relativePath = projects.GetArtifactRelativePath(artifactId);
            await projects.WriteArtifactAsync(job.ProjectId, artifactId, output, cancellationToken);
            var artifact = new ArtifactMetadata(
                artifactId,
                job.ProjectId,
                job.RevisionId,
                node.Id,
                cacheKey,
                contentHash,
                relativePath,
                output.LongLength,
                false,
                DateTimeOffset.UtcNow);
            await executionIndex.StoreArtifactAsync(artifact, cancellationToken);
            persistenceTimer.Stop();
            totalPersistenceMilliseconds += persistenceTimer.ElapsedMilliseconds;
            nodeResults.Add(
                node.Id,
                new GenerationNodeResult(
                    node.Id,
                    cacheKey,
                    artifactId,
                    contentHash,
                    inputBytes,
                    false,
                    preparationTimer.ElapsedMilliseconds,
                    generationTimer.ElapsedMilliseconds,
                    persistenceTimer.ElapsedMilliseconds));
        }

        var completedAt = DateTimeOffset.UtcNow;
        return new GenerationRunReport(
            job.Id,
            job.ProjectId,
            job.RevisionId,
            job.BranchId,
            "Succeeded",
            nodeResults.Values.ToList(),
            graphBytes,
            totalInputBytes,
            totalPreparationMilliseconds,
            totalGenerationMilliseconds,
            totalPersistenceMilliseconds,
            startedAt,
            completedAt);
    }

    private static List<GenerationNodeDefinition> OrderRequiredNodes(
        GenerationGraph graph,
        IReadOnlyCollection<string> requestedTargets)
    {
        var nodes = new Dictionary<string, GenerationNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !nodes.TryAdd(node.Id, node))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Duplicate or empty graph node id '{node.Id}'.");
            }
        }

        var targets = requestedTargets.Count == 0 ? nodes.Keys.ToList() : requestedTargets;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<GenerationNodeDefinition>();

        void Visit(string nodeId)
        {
            if (visited.Contains(nodeId))
            {
                return;
            }

            if (!nodes.TryGetValue(nodeId, out var node))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Graph node '{nodeId}' does not exist.");
            }

            if (!visiting.Add(nodeId))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Generation graph contains a cycle at '{nodeId}'.");
            }

            if (node.Version < 1 || string.IsNullOrWhiteSpace(node.Kind))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Graph node '{nodeId}' has invalid metadata.");
            }

            foreach (var dependencyId in node.DependsOnNodeIds.Order(StringComparer.Ordinal))
            {
                Visit(dependencyId);
            }

            visiting.Remove(nodeId);
            visited.Add(nodeId);
            ordered.Add(node);
        }

        foreach (var target in targets.Order(StringComparer.Ordinal))
        {
            Visit(target);
        }

        return ordered;
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record MockGenerationArtifact(
        string NodeId,
        string NodeKind,
        string BackendVersion,
        Dictionary<string, string> InputHashes,
        Dictionary<string, string> DependencyHashes,
        string Preview);
}
