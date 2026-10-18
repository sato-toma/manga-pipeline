using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Tomlyn;

namespace MangaPipeline.Core;

public sealed class LocalProjectStore
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectLocks = new(StringComparer.Ordinal);

    public LocalProjectStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task<ProjectManifest> CreateSampleProjectAsync(
        string name,
        IReadOnlyCollection<DocumentDefinition> documents,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || documents is null || documents.Count == 0)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "Project name and at least one definition document are required.");
        }

        var projectId = Guid.NewGuid().ToString("N");
        var projectRoot = ProjectPath(projectId);
        Directory.CreateDirectory(projectRoot);

        DefinitionValidator.Validate(documents.ToList());
        var documentHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            ValidateDocument(document);
            var prepared = TomlDefinitionCodec.Prepare(document);
            var hash = prepared.Hash;
            if (!documentHashes.TryAdd(document.Id, hash))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Duplicate document id '{document.Id}'.");
            }

            var path = DocumentPath(projectId, document.Id, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteBytesImmutableAsync(path, prepared.Bytes, cancellationToken);
        }

        var revisionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var revision = new RevisionSnapshot(
            revisionId,
            projectId,
            "main",
            null,
            null,
            "author",
            "Initial sample project",
            now,
            documentHashes,
            JsonSupport.Hash(documentHashes));
        await WriteMutableAsync(RevisionPath(projectId, revisionId), revision, cancellationToken);
        await WriteMutableAsync(
            BranchPath(projectId, "main"),
            new StoryBranch("main", "Main story", revisionId, now),
            cancellationToken);

        var manifest = new ProjectManifest(
            projectId,
            name.Trim(),
            "main",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["main"] = revisionId },
            now);
        await WriteMutableAsync(ManifestPath(projectId), manifest, cancellationToken);
        return manifest;
    }

    public async Task<List<ProjectManifest>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = new List<ProjectManifest>();
        var projectsRoot = Path.Combine(_root, "projects");
        if (!Directory.Exists(projectsRoot))
        {
            return projects;
        }

        foreach (var directory in Directory.EnumerateDirectories(projectsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "project.json");
            if (File.Exists(path))
            {
                projects.Add(await ReadAsync<ProjectManifest>(path, cancellationToken));
            }
        }

        return projects.OrderBy(project => project.CreatedAt).ToList();
    }

    public Task<ProjectManifest> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
        ReadAsync<ProjectManifest>(ManifestPath(projectId), cancellationToken);

    public Task<RevisionSnapshot> GetRevisionAsync(
        string projectId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        ReadRevisionAsync(projectId, revisionId, cancellationToken);

    public Task<StoryBranch> GetBranchAsync(
        string projectId,
        string branchId,
        CancellationToken cancellationToken = default) =>
        ReadAsync<StoryBranch>(BranchPath(projectId, branchId), cancellationToken);

    public async Task<long> GetDocumentStorageSizeAsync(
        string projectId,
        string revisionId,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        var revision = await GetRevisionAsync(projectId, revisionId, cancellationToken);
        if (!revision.DocumentHashes.TryGetValue(documentId, out var hash) || !IsSha256(hash))
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Document '{documentId}' is not in revision '{revisionId}'.");
        }

        var path = DocumentPath(projectId, documentId, hash);
        try
        {
            return new FileInfo(path).Length;
        }
        catch (FileNotFoundException)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Document '{documentId}' is missing.");
        }
    }

    public async Task<List<DocumentFileMetadata>> ListDocumentMetadataAsync(
        string projectId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        var revision = await GetRevisionAsync(projectId, revisionId, cancellationToken);
        var metadata = new List<DocumentFileMetadata>();
        foreach (var (documentId, hash) in revision.DocumentHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSha256(hash))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Document '{documentId}' has an invalid stored hash.");
            }

            var path = DocumentPath(projectId, documentId, hash);
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Document '{documentId}' is missing.");
            }

            metadata.Add(new DocumentFileMetadata(documentId, hash, file.Length));
        }

        return metadata;
    }

    public async Task<List<RevisionSnapshot>> ListRevisionsAsync(
        string projectId,
        string branchId,
        CancellationToken cancellationToken = default)
    {
        var revisions = new List<RevisionSnapshot>();
        var directory = Path.Combine(ProjectPath(projectId), "revisions");
        if (!Directory.Exists(directory))
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Project '{projectId}' does not exist.");
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = await ReadAsync<RevisionSnapshot>(path, cancellationToken);
            if (revision.BranchId == branchId)
            {
                revisions.Add(revision);
            }
        }

        return revisions.OrderBy(revision => revision.CreatedAt).ToList();
    }

    public async Task<DocumentDefinition> GetDocumentAsync(
        string projectId,
        string revisionId,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        var revision = await GetRevisionAsync(projectId, revisionId, cancellationToken);
        if (!revision.DocumentHashes.TryGetValue(documentId, out var expectedHash))
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Document '{documentId}' is not in revision '{revisionId}'.");
        }

        if (!IsSha256(expectedHash))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Document '{documentId}' has an invalid stored hash.");
        }

        var document = await ReadDocumentAsync(
            DocumentPath(projectId, documentId, expectedHash),
            documentId,
            cancellationToken);
        if (!StringComparer.Ordinal.Equals(JsonSupport.Hash(document), expectedHash))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Document '{documentId}' failed its content hash check.");
        }

        return document;
    }

    public async Task<RevisionSnapshot> CreateRevisionAsync(
        string projectId,
        string branchId,
        string baseRevisionId,
        IReadOnlyDictionary<string, DocumentDefinition> changedDocuments,
        string createdBy,
        string reason,
        string? changeSetId,
        CancellationToken cancellationToken = default)
    {
        var gate = _projectLocks.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await GetProjectAsync(projectId, cancellationToken);
            if (!manifest.BranchHeads.TryGetValue(branchId, out var headRevisionId))
            {
                throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Branch '{branchId}' does not exist.");
            }

            if (!StringComparer.Ordinal.Equals(headRevisionId, baseRevisionId))
            {
                throw new WorkspaceException(
                    WorkspaceErrorCode.Conflict,
                    $"Branch '{branchId}' advanced from '{baseRevisionId}' to '{headRevisionId}'.");
            }

            var baseRevision = await GetRevisionAsync(projectId, baseRevisionId, cancellationToken);
            var documentHashes = new Dictionary<string, string>(baseRevision.DocumentHashes, StringComparer.Ordinal);
            foreach (var (documentId, document) in changedDocuments)
            {
                ValidateDocument(document);
                if (!StringComparer.Ordinal.Equals(documentId, document.Id) ||
                    !documentHashes.ContainsKey(documentId))
                {
                    throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Invalid changed document '{documentId}'.");
                }

                ValidateDocumentReferences(document, documentHashes.Keys);
                if (documentId == "graph")
                {
                    _ = DefinitionValidator.ValidateGraph(document, documentHashes.Keys);
                }

                var prepared = TomlDefinitionCodec.Prepare(document);
                var hash = prepared.Hash;
                var path = DocumentPath(projectId, documentId, hash);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await WriteBytesImmutableAsync(path, prepared.Bytes, cancellationToken);
                documentHashes[documentId] = hash;
            }

            var revisionId = Guid.NewGuid().ToString("N");
            var revision = new RevisionSnapshot(
                revisionId,
                projectId,
                branchId,
                baseRevisionId,
                changeSetId,
                string.IsNullOrWhiteSpace(createdBy) ? "unknown" : createdBy.Trim(),
                string.IsNullOrWhiteSpace(reason) ? "Accepted changes" : reason.Trim(),
                DateTimeOffset.UtcNow,
                documentHashes,
                JsonSupport.Hash(documentHashes));
            await WriteMutableAsync(RevisionPath(projectId, revisionId), revision, cancellationToken);

            var updatedHeads = new Dictionary<string, string>(manifest.BranchHeads, StringComparer.Ordinal)
            {
                [branchId] = revisionId
            };
            await WriteMutableAsync(
                ManifestPath(projectId),
                manifest with { BranchHeads = updatedHeads },
                cancellationToken);
            return revision;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RevisionSnapshot> RestoreRevisionAsync(
        string projectId,
        string branchId,
        string targetRevisionId,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        var gate = _projectLocks.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await GetProjectAsync(projectId, cancellationToken);
            if (!manifest.BranchHeads.TryGetValue(branchId, out var headRevisionId))
            {
                throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Branch '{branchId}' does not exist.");
            }

            var current = await GetRevisionAsync(projectId, headRevisionId, cancellationToken);
            var target = await GetRevisionAsync(projectId, targetRevisionId, cancellationToken);
            var revisionId = Guid.NewGuid().ToString("N");
            var revision = new RevisionSnapshot(
                revisionId,
                projectId,
                branchId,
                current.Id,
                null,
                string.IsNullOrWhiteSpace(createdBy) ? "unknown" : createdBy.Trim(),
                $"Restore revision {targetRevisionId}",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>(target.DocumentHashes, StringComparer.Ordinal),
                JsonSupport.Hash(target.DocumentHashes));
            await WriteMutableAsync(RevisionPath(projectId, revisionId), revision, cancellationToken);
            var updatedHeads = new Dictionary<string, string>(manifest.BranchHeads, StringComparer.Ordinal)
            {
                [branchId] = revisionId
            };
            await WriteMutableAsync(
                ManifestPath(projectId),
                manifest with { BranchHeads = updatedHeads },
                cancellationToken);
            return revision;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoryBranch> CreateBranchAsync(
        string projectId,
        string branchId,
        string name,
        string parentRevisionId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(branchId, "branch id");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "Branch name is required.");
        }

        var gate = _projectLocks.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await GetProjectAsync(projectId, cancellationToken);
            if (manifest.BranchHeads.ContainsKey(branchId))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Conflict, $"Branch '{branchId}' already exists.");
            }

            _ = await GetRevisionAsync(projectId, parentRevisionId, cancellationToken);
            var branch = new StoryBranch(branchId, name.Trim(), parentRevisionId, DateTimeOffset.UtcNow);
            await WriteMutableAsync(BranchPath(projectId, branchId), branch, cancellationToken);
            var heads = new Dictionary<string, string>(manifest.BranchHeads, StringComparer.Ordinal)
            {
                [branchId] = parentRevisionId
            };
            await WriteMutableAsync(ManifestPath(projectId), manifest with { BranchHeads = heads }, cancellationToken);
            return branch;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Dictionary<string, string>> GetDocumentHashesAsync(
        string projectId,
        string revisionId,
        IEnumerable<string> documentIds,
        CancellationToken cancellationToken = default)
    {
        var revision = await GetRevisionAsync(projectId, revisionId, cancellationToken);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var documentId in documentIds.Distinct(StringComparer.Ordinal))
        {
            if (!revision.DocumentHashes.TryGetValue(documentId, out var hash))
            {
                throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Document '{documentId}' is not in revision '{revisionId}'.");
            }

            hashes.Add(documentId, hash);
        }

        return hashes;
    }

    public async Task<List<string>> GetChangedDocumentIdsAsync(
        string projectId,
        string fromRevisionId,
        string toRevisionId,
        CancellationToken cancellationToken = default)
    {
        var from = await GetRevisionAsync(projectId, fromRevisionId, cancellationToken);
        var to = await GetRevisionAsync(projectId, toRevisionId, cancellationToken);
        return from.DocumentHashes.Keys
            .Union(to.DocumentHashes.Keys, StringComparer.Ordinal)
            .Where(id =>
                !from.DocumentHashes.TryGetValue(id, out var oldHash) ||
                !to.DocumentHashes.TryGetValue(id, out var newHash) ||
                !StringComparer.Ordinal.Equals(oldHash, newHash))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public async Task WriteChangeSetAsync(ChangeSet changeSet, CancellationToken cancellationToken = default)
    {
        var revision = await GetRevisionAsync(changeSet.ProjectId, changeSet.BaseRevisionId, cancellationToken);
        ValidateChanges(changeSet.Changes);
        ValidateChangeTargets(revision, changeSet.Changes);
        ValidateChangedReferences(revision, changeSet.Changes);
        await WriteMutableAsync(ChangeSetPath(changeSet.ProjectId, changeSet.Id), changeSet, cancellationToken);
    }

    public Task<ChangeSet> GetChangeSetAsync(
        string projectId,
        string changeSetId,
        CancellationToken cancellationToken = default) =>
        ReadAsync<ChangeSet>(ChangeSetPath(projectId, changeSetId), cancellationToken);

    public async Task WriteWorkUnitAsync(WorkUnit workUnit, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(workUnit.Id, "work unit id");
        var revision = await GetRevisionAsync(workUnit.ProjectId, workUnit.BaseRevisionId, cancellationToken);
        ValidateChanges(workUnit.DraftChanges);
        ValidateChangeTargets(revision, workUnit.DraftChanges);
        ValidateChangedReferences(revision, workUnit.DraftChanges);
        await WriteMutableAsync(WorkUnitPath(workUnit.ProjectId, workUnit.Id), workUnit, cancellationToken);
    }

    public Task<WorkUnit> GetWorkUnitAsync(
        string projectId,
        string workUnitId,
        CancellationToken cancellationToken = default) =>
        ReadAsync<WorkUnit>(WorkUnitPath(projectId, workUnitId), cancellationToken);

    public async Task<List<WorkUnit>> ListWorkUnitsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(ProjectPath(projectId), "work-units");
        if (!Directory.Exists(directory))
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Project '{projectId}' does not exist.");
        }

        var workUnits = new List<WorkUnit>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            workUnits.Add(await ReadAsync<WorkUnit>(path, cancellationToken));
        }

        return workUnits.OrderBy(unit => unit.CreatedAt).ToList();
    }

    public async Task WriteContributionEventAsync(
        ContributionEvent contributionEvent,
        CancellationToken cancellationToken = default)
    {
        _ = await GetRevisionAsync(contributionEvent.ProjectId, contributionEvent.RevisionId, cancellationToken);
        var path = Path.Combine(
            ProjectPath(contributionEvent.ProjectId),
            "events",
            $"{contributionEvent.CreatedAt:yyyyMMddHHmmssfffffff}-{contributionEvent.Id}.json");
        await WriteImmutableAsync(path, contributionEvent, cancellationToken);
    }

    public async Task<List<ContributionEvent>> ListContributionEventsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(ProjectPath(projectId), "events");
        if (!Directory.Exists(directory))
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Project '{projectId}' does not exist.");
        }

        var events = new List<ContributionEvent>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add(await ReadAsync<ContributionEvent>(path, cancellationToken));
        }

        return events.OrderBy(item => item.CreatedAt).ToList();
    }

    public Task WriteArtifactAsync(
        string projectId,
        string artifactId,
        byte[] content,
        CancellationToken cancellationToken = default) =>
        WriteBytesImmutableAsync(ArtifactPath(projectId, artifactId), content, cancellationToken);

    public async Task<byte[]> ReadArtifactAsync(
        string projectId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var projectRoot = ProjectPath(projectId);
        var path = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        var pathFromProjectRoot = Path.GetRelativePath(projectRoot, path);
        if (Path.IsPathRooted(pathFromProjectRoot) ||
            pathFromProjectRoot is "." or ".." ||
            pathFromProjectRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "Artifact path escapes its project.");
        }

        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Artifact '{relativePath}' does not exist.");
        }
    }

    public string GetArtifactRelativePath(string artifactId) => Path.Combine("artifacts", $"{artifactId}.json");

    public async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<T>(bytes, JsonSupport.Options)
                ?? throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"File '{path}' contains JSON null.");
        }
        catch (FileNotFoundException)
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"File '{Path.GetFileName(path)}' does not exist.");
        }
    }

    private void ValidateChanges(IEnumerable<ProposedDocumentChange> changes)
    {
        if (changes is null)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "Document changes are required.");
        }

        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change is null ||
                change.ProposedDocument is null ||
                !StringComparer.Ordinal.Equals(change.DocumentId, change.ProposedDocument.Id) ||
                !documentIds.Add(change.DocumentId))
            {
                throw new WorkspaceException(WorkspaceErrorCode.Validation, "Changes must contain unique matching document ids.");
            }

            ValidateDocument(change.ProposedDocument);
        }
    }

    private static void ValidateChangeTargets(
        RevisionSnapshot revision,
        IEnumerable<ProposedDocumentChange> changes)
    {
        foreach (var change in changes)
        {
            if (!revision.DocumentHashes.ContainsKey(change.DocumentId))
            {
                throw new WorkspaceException(
                    WorkspaceErrorCode.Validation,
                    $"Document '{change.DocumentId}' does not exist in base revision '{revision.Id}'.");
            }
        }
    }

    private static void ValidateDocument(DocumentDefinition document)
    {
        DefinitionValidator.ValidateDocument(document);
    }

    private static void ValidateChangedReferences(
        RevisionSnapshot revision,
        IEnumerable<ProposedDocumentChange> changes)
    {
        foreach (var change in changes)
        {
            ValidateDocumentReferences(change.ProposedDocument, revision.DocumentHashes.Keys);
            if (change.DocumentId == "graph")
            {
                _ = DefinitionValidator.ValidateGraph(change.ProposedDocument, revision.DocumentHashes.Keys);
            }
        }
    }

    private static void ValidateDocumentReferences(DocumentDefinition document, IEnumerable<string> availableIds)
    {
        var available = new HashSet<string>(availableIds, StringComparer.Ordinal);
        foreach (var reference in document.References)
        {
            if (!available.Contains(reference))
            {
                throw new WorkspaceException(
                    WorkspaceErrorCode.Validation,
                    $"Document '{document.Id}' references missing document '{reference}'.");
            }
        }
    }

    private static void ValidateIdentifier(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            id is "." or ".." ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Invalid {name} '{id}'.");
        }
    }

    private string ProjectPath(string projectId)
    {
        ValidateIdentifier(projectId, "project id");
        return Path.Combine(_root, "projects", projectId);
    }

    private string ManifestPath(string projectId) => Path.Combine(ProjectPath(projectId), "project.json");
    private string RevisionPath(string projectId, string revisionId) =>
        Path.Combine(ProjectPath(projectId), "revisions", $"{SafeFileId(revisionId)}.json");
    private string ChangeSetPath(string projectId, string changeSetId) =>
        Path.Combine(ProjectPath(projectId), "changesets", $"{SafeFileId(changeSetId)}.json");
    private string WorkUnitPath(string projectId, string workUnitId) =>
        Path.Combine(ProjectPath(projectId), "work-units", $"{SafeFileId(workUnitId)}.json");
    private string BranchPath(string projectId, string branchId) =>
        Path.Combine(ProjectPath(projectId), "branches", $"{SafeFileId(branchId)}.json");
    private string DocumentPath(string projectId, string documentId, string hash) =>
        Path.Combine(ProjectPath(projectId), "documents", SafeFileId(documentId), $"{hash}.toml");
    private string ArtifactPath(string projectId, string artifactId) =>
        Path.Combine(ProjectPath(projectId), "artifacts", $"{SafeFileId(artifactId)}.json");

    private static string SafeFileId(string id)
    {
        ValidateIdentifier(id, "id");
        return id;
    }

    private async Task<RevisionSnapshot> ReadRevisionAsync(
        string projectId,
        string revisionId,
        CancellationToken cancellationToken)
    {
        var revision = await ReadAsync<RevisionSnapshot>(RevisionPath(projectId, revisionId), cancellationToken);
        if (!IsSha256(revision.SnapshotHash) ||
            !StringComparer.Ordinal.Equals(JsonSupport.Hash(revision.DocumentHashes), revision.SnapshotHash))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Revision '{revisionId}' failed its snapshot hash check.");
        }

        return revision;
    }

    private async Task<DocumentDefinition> ReadDocumentAsync(
        string path,
        string documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            return TomlDefinitionCodec.Deserialize(await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Document '{documentId}' is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Document '{documentId}' is missing.");
        }
        catch (TomlException exception)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Document '{documentId}' contains invalid TOML: {exception.Message}");
        }
        catch (WorkspaceException exception) when (exception.Code == WorkspaceErrorCode.Validation)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Document '{documentId}' is invalid: {exception.Message}");
        }
    }

    private static bool IsSha256(string hash) =>
        hash.Length == 64 && hash.All(Uri.IsHexDigit);

    private static async Task WriteMutableAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(value, JsonSupport.Options), cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteImmutableAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            return;
        }

        await WriteBytesImmutableAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonSupport.Options), cancellationToken);
    }

    private static async Task WriteBytesImmutableAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            return;
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temporaryPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
