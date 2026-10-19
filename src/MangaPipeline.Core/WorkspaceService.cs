using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MangaPipeline.Core;

public sealed record DocumentVersionDiff(
    string DocumentId,
    string? BeforeHash,
    string? AfterHash,
    DocumentDefinition? Before,
    DocumentDefinition? After);

public sealed record WorkUnitSubmission(WorkUnit WorkUnit, ChangeSet ChangeSet, bool HasConflict);

public sealed record ProvenanceVerification(bool IsValid, string RevisionId, List<string> Errors);

public sealed class WorkspaceService(
    LocalProjectStore projects,
    SqliteExecutionIndex executionIndex)
{
    public Task<List<ProjectManifest>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        projects.ListProjectsAsync(cancellationToken);

    public Task<ProjectManifest> CreateSampleProjectAsync(
        string name,
        IReadOnlyCollection<DocumentDefinition> documents,
        CancellationToken cancellationToken = default) =>
        CreateProjectAsync(name, documents, cancellationToken);

    public Task<ProjectManifest> CreateProjectAsync(
        string name,
        IReadOnlyCollection<DocumentDefinition> documents,
        CancellationToken cancellationToken = default) =>
        projects.CreateProjectAsync(name, documents, cancellationToken);

    public Task<ProjectManifest> GetProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
        projects.GetProjectAsync(projectId, cancellationToken);

    public Task<RevisionSnapshot> GetRevisionAsync(
        string projectId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        projects.GetRevisionAsync(projectId, revisionId, cancellationToken);

    public Task<List<RevisionSnapshot>> GetRevisionsAsync(
        string projectId,
        string branchId,
        CancellationToken cancellationToken = default) =>
        projects.ListRevisionsAsync(projectId, branchId, cancellationToken);

    public Task<List<DocumentFileMetadata>> ListDocumentMetadataAsync(
        string projectId,
        string revisionId,
        CancellationToken cancellationToken = default) =>
        projects.ListDocumentMetadataAsync(projectId, revisionId, cancellationToken);

    public Task<DocumentDefinition> GetDocumentAsync(
        string projectId,
        string revisionId,
        string documentId,
        CancellationToken cancellationToken = default) =>
        projects.GetDocumentAsync(projectId, revisionId, documentId, cancellationToken);

    public async Task<List<DocumentVersionDiff>> GetRevisionDiffAsync(
        string projectId,
        string fromRevisionId,
        string toRevisionId,
        CancellationToken cancellationToken = default)
    {
        var from = await projects.GetRevisionAsync(projectId, fromRevisionId, cancellationToken);
        var to = await projects.GetRevisionAsync(projectId, toRevisionId, cancellationToken);
        var changedIds = await projects.GetChangedDocumentIdsAsync(
            projectId,
            fromRevisionId,
            toRevisionId,
            cancellationToken);
        var result = new List<DocumentVersionDiff>();
        foreach (var documentId in changedIds)
        {
            from.DocumentHashes.TryGetValue(documentId, out var beforeHash);
            to.DocumentHashes.TryGetValue(documentId, out var afterHash);
            var before = beforeHash is null
                ? null
                : await projects.GetDocumentAsync(projectId, fromRevisionId, documentId, cancellationToken);
            var after = afterHash is null
                ? null
                : await projects.GetDocumentAsync(projectId, toRevisionId, documentId, cancellationToken);
            result.Add(new DocumentVersionDiff(documentId, beforeHash, afterHash, before, after));
        }

        return result;
    }

    public async Task<StoryBranch> CreateBranchAsync(
        string projectId,
        string branchId,
        string name,
        string parentRevisionId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var branch = await projects.CreateBranchAsync(projectId, branchId, name, parentRevisionId, cancellationToken);
        await projects.WriteContributionEventAsync(
            new ContributionEvent(
                Guid.NewGuid().ToString("N"),
                projectId,
                parentRevisionId,
                null,
                null,
                null,
                actor,
                "story-branch.created",
                $"Created story branch '{branchId}'.",
                DateTimeOffset.UtcNow),
            cancellationToken);
        return branch;
    }

    public async Task<WorkUnit> CreateWorkUnitAsync(
        string projectId,
        string branchId,
        string baseRevisionId,
        string targetScope,
        string title,
        string purpose,
        List<string> completionCriteria,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(purpose) ||
            completionCriteria is null ||
            completionCriteria.Count == 0 ||
            completionCriteria.Any(string.IsNullOrWhiteSpace))
        {
            throw new WorkspaceException(
                WorkspaceErrorCode.Validation,
                "A work unit requires a title, purpose, and at least one completion criterion.");
        }

        ValidatePanelScope(targetScope);
        var manifest = await projects.GetProjectAsync(projectId, cancellationToken);
        if (!manifest.BranchHeads.TryGetValue(branchId, out var headRevisionId))
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Branch '{branchId}' does not exist.");
        }

        if (!StringComparer.Ordinal.Equals(baseRevisionId, headRevisionId))
        {
            throw new WorkspaceException(
                WorkspaceErrorCode.Conflict,
                $"Branch '{branchId}' is at revision '{headRevisionId}', not '{baseRevisionId}'.");
        }

        await ValidatePanelExistsAsync(projectId, baseRevisionId, targetScope, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var workUnit = new WorkUnit(
            Guid.NewGuid().ToString("N"),
            projectId,
            branchId,
            baseRevisionId,
            targetScope,
            title.Trim(),
            purpose.Trim(),
            completionCriteria.Select(criterion => criterion.Trim()).ToList(),
            "Open",
            null,
            [],
            null,
            now,
            now);
        await projects.WriteWorkUnitAsync(workUnit, cancellationToken);
        await projects.WriteContributionEventAsync(
            new ContributionEvent(
                Guid.NewGuid().ToString("N"),
                projectId,
                baseRevisionId,
                workUnit.Id,
                null,
                null,
                string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim(),
                "work-unit.created",
                workUnit.Purpose,
                DateTimeOffset.UtcNow),
            cancellationToken);
        return workUnit;
    }

    public async Task<ChangeSet> CreateChangeSetAsync(
        string projectId,
        string branchId,
        string baseRevisionId,
        string rawFeedback,
        string actor,
        List<ProposedDocumentChange> changes,
        string? sourceArtifactId,
        string? workUnitId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawFeedback) ||
            changes is null ||
            changes.Count == 0 ||
            changes.Any(change => change is null || change.ProposedDocument is null))
        {
            throw new WorkspaceException(
                WorkspaceErrorCode.Validation,
                "A change set requires feedback and at least one proposed document change.");
        }

        _ = await projects.GetRevisionAsync(projectId, baseRevisionId, cancellationToken);
        var manifest = await projects.GetProjectAsync(projectId, cancellationToken);
        if (!manifest.BranchHeads.TryGetValue(branchId, out var headRevisionId))
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Branch '{branchId}' does not exist.");
        }

        if (sourceArtifactId is not null)
        {
            _ = await executionIndex.GetArtifactAsync(projectId, sourceArtifactId, cancellationToken);
        }

        var changeSet = new ChangeSet(
            Guid.NewGuid().ToString("N"),
            projectId,
            branchId,
            baseRevisionId,
            rawFeedback.Trim(),
            string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim(),
            workUnitId,
            sourceArtifactId,
            StringComparer.Ordinal.Equals(headRevisionId, baseRevisionId) ? "Proposed" : "Conflict",
            changes,
            DateTimeOffset.UtcNow);
        await projects.WriteChangeSetAsync(changeSet, cancellationToken);
        return changeSet;
    }

    public Task<ChangeSet> GetChangeSetAsync(
        string projectId,
        string changeSetId,
        CancellationToken cancellationToken = default) =>
        projects.GetChangeSetAsync(projectId, changeSetId, cancellationToken);

    public Task<WorkUnit> GetWorkUnitAsync(
        string projectId,
        string workUnitId,
        CancellationToken cancellationToken = default) =>
        projects.GetWorkUnitAsync(projectId, workUnitId, cancellationToken);

    public Task<List<WorkUnit>> ListWorkUnitsAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        projects.ListWorkUnitsAsync(projectId, cancellationToken);

    public Task<List<ContributionEvent>> ListContributionEventsAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        projects.ListContributionEventsAsync(projectId, cancellationToken);

    public async Task<WorkUnitSubmission> SubmitWorkUnitAsync(
        string projectId,
        string workUnitId,
        WorkUnitProposal proposal,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(proposal.RawFeedback) ||
            proposal.Changes is null ||
            proposal.Changes.Count == 0 ||
            proposal.Changes.Any(change => change is null || change.ProposedDocument is null))
        {
            throw new WorkspaceException(
                WorkspaceErrorCode.Validation,
                "A work unit submission requires feedback and at least one proposed document change.");
        }

        var workUnit = await projects.GetWorkUnitAsync(projectId, workUnitId, cancellationToken);
        if (workUnit.Status == "Accepted")
        {
            throw new WorkspaceException(WorkspaceErrorCode.InvalidState, "An accepted work unit cannot be edited.");
        }

        if (proposal.Changes.Any(change => !StringComparer.Ordinal.Equals(change.DocumentId, workUnit.TargetScope)))
        {
            throw new WorkspaceException(
                WorkspaceErrorCode.Validation,
                $"Work unit '{workUnit.Id}' may only change document '{workUnit.TargetScope}'.");
        }

        var changeSet = await CreateChangeSetAsync(
            projectId,
            workUnit.BranchId,
            workUnit.BaseRevisionId,
            proposal.RawFeedback,
            actor,
            proposal.Changes,
            proposal.SourceArtifactId,
            workUnit.Id,
            cancellationToken);
        var hasConflict = changeSet.Status == "Conflict";
        var updatedWorkUnit = workUnit with
        {
            RawFeedback = changeSet.RawFeedback,
            DraftChanges = proposal.Changes,
            ChangeSetId = changeSet.Id,
            Status = hasConflict ? "Conflict" : "Proposed",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await projects.WriteChangeSetAsync(changeSet, cancellationToken);
        await projects.WriteWorkUnitAsync(updatedWorkUnit, cancellationToken);
        return new WorkUnitSubmission(updatedWorkUnit, changeSet, hasConflict);
    }

    public async Task<RevisionSnapshot> ApproveChangeSetAsync(
        string projectId,
        string changeSetId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var changeSet = await projects.GetChangeSetAsync(projectId, changeSetId, cancellationToken);
        var existingRevisions = await projects.ListRevisionsAsync(projectId, changeSet.BranchId, cancellationToken);
        var alreadyApplied = existingRevisions.FirstOrDefault(revision =>
            StringComparer.Ordinal.Equals(revision.ChangeSetId, changeSet.Id));
        if (alreadyApplied is not null)
        {
            return alreadyApplied;
        }

        if (changeSet.Status != "Proposed")
        {
            throw new WorkspaceException(WorkspaceErrorCode.InvalidState, $"Change set is '{changeSet.Status}', not Proposed.");
        }

        var changedDocuments = changeSet.Changes.ToDictionary(
            change => change.DocumentId,
            change => change.ProposedDocument,
            StringComparer.Ordinal);
        var revision = await projects.CreateRevisionAsync(
            projectId,
            changeSet.BranchId,
            changeSet.BaseRevisionId,
            changedDocuments,
            actor,
            changeSet.RawFeedback,
            changeSet.Id,
            cancellationToken);
        await projects.WriteChangeSetAsync(changeSet with { Status = "Approved" }, cancellationToken);
        await projects.WriteContributionEventAsync(
            new ContributionEvent(
                Guid.NewGuid().ToString("N"),
                projectId,
                revision.Id,
                changeSet.WorkUnitId,
                changeSet.Id,
                changeSet.SourceArtifactId,
                string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim(),
                "changeset.approved",
                changeSet.RawFeedback,
                DateTimeOffset.UtcNow),
            cancellationToken);

        if (changeSet.WorkUnitId is not null)
        {
            var workUnit = await projects.GetWorkUnitAsync(projectId, changeSet.WorkUnitId, cancellationToken);
            await projects.WriteWorkUnitAsync(
                workUnit with { Status = "Accepted", UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }

        return revision;
    }

    public async Task<RevisionSnapshot> RestoreRevisionAsync(
        string projectId,
        string branchId,
        string revisionId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var revision = await projects.RestoreRevisionAsync(projectId, branchId, revisionId, actor, cancellationToken);
        await projects.WriteContributionEventAsync(
            new ContributionEvent(
                Guid.NewGuid().ToString("N"),
                projectId,
                revision.Id,
                null,
                null,
                null,
                actor,
                "revision.restored",
                $"Restored content from revision '{revisionId}'.",
                DateTimeOffset.UtcNow),
            cancellationToken);
        return revision;
    }

    public async Task<ProvenanceManifest> ExportProvenanceAsync(
        string projectId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        var revision = await projects.GetRevisionAsync(projectId, revisionId, cancellationToken);
        var documentHashes = new SortedDictionary<string, string>(revision.DocumentHashes, StringComparer.Ordinal);
        var unsignedManifest = new
        {
            SchemaVersion = 1,
            ProjectId = projectId,
            RevisionId = revisionId,
            revision.BranchId,
            revision.SnapshotHash,
            DocumentHashes = documentHashes
        };
        return new ProvenanceManifest(
            1,
            projectId,
            revisionId,
            revision.BranchId,
            revision.SnapshotHash,
            new Dictionary<string, string>(documentHashes, StringComparer.Ordinal),
            JsonSupport.Hash(unsignedManifest));
    }

    public async Task<ProvenanceVerification> VerifyProvenanceAsync(
        ProvenanceManifest manifest,
        CancellationToken cancellationToken = default)
    {
        if (manifest is null || manifest.DocumentHashes is null || manifest.SchemaVersion != 1)
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "A supported provenance manifest is required.");
        }

        var errors = new List<string>();
        var unsignedManifest = new
        {
            manifest.SchemaVersion,
            manifest.ProjectId,
            manifest.RevisionId,
            manifest.BranchId,
            manifest.SnapshotHash,
            DocumentHashes = new SortedDictionary<string, string>(manifest.DocumentHashes, StringComparer.Ordinal)
        };
        if (!StringComparer.Ordinal.Equals(JsonSupport.Hash(unsignedManifest), manifest.ManifestHash))
        {
            errors.Add("Manifest hash mismatch.");
        }

        var revision = await projects.GetRevisionAsync(manifest.ProjectId, manifest.RevisionId, cancellationToken);
        if (!StringComparer.Ordinal.Equals(revision.SnapshotHash, manifest.SnapshotHash) ||
            !StringComparer.Ordinal.Equals(revision.BranchId, manifest.BranchId) ||
            !DictionaryEqual(revision.DocumentHashes, manifest.DocumentHashes))
        {
            errors.Add("Manifest does not match the referenced revision.");
        }

        foreach (var documentId in manifest.DocumentHashes.Keys.Order(StringComparer.Ordinal))
        {
            try
            {
                _ = await projects.GetDocumentAsync(manifest.ProjectId, manifest.RevisionId, documentId, cancellationToken);
            }
            catch (WorkspaceException exception) when (exception.Code is WorkspaceErrorCode.Integrity or WorkspaceErrorCode.NotFound)
            {
                errors.Add($"Document '{documentId}' failed integrity verification.");
            }
        }

        return new ProvenanceVerification(errors.Count == 0, manifest.RevisionId, errors);
    }

    public async Task<GenerationJob> EnqueueGenerationAsync(
        string projectId,
        string revisionId,
        string branchId,
        List<string> targetNodeIds,
        CancellationToken cancellationToken = default)
    {
        if (targetNodeIds is null || targetNodeIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "Target node ids cannot contain null or empty values.");
        }

        _ = await projects.GetRevisionAsync(projectId, revisionId, cancellationToken);
        _ = await projects.GetBranchAsync(projectId, branchId, cancellationToken);
        var job = new GenerationJob(
            Guid.NewGuid().ToString("N"),
            projectId,
            revisionId,
            branchId,
            targetNodeIds.Distinct(StringComparer.Ordinal).ToList(),
            "Queued",
            null,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);
        await executionIndex.EnqueueAsync(job, cancellationToken);
        return job;
    }

    public Task<GenerationJob> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        executionIndex.GetJobAsync(jobId, cancellationToken);

    public async Task<List<GenerationJob>> ListJobsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        _ = await projects.GetProjectAsync(projectId, cancellationToken);
        return await executionIndex.ListJobsAsync(projectId, cancellationToken);
    }

    public async Task<GenerationJob> RetryJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var failedJob = await executionIndex.GetJobAsync(jobId, cancellationToken);
        if (failedJob.Status != "Failed")
        {
            throw new WorkspaceException(WorkspaceErrorCode.InvalidState, "Only failed jobs can be retried.");
        }

        return await EnqueueGenerationAsync(
            failedJob.ProjectId,
            failedJob.RevisionId,
            failedJob.BranchId,
            failedJob.TargetNodeIds,
            cancellationToken);
    }

    public async Task<List<ArtifactMetadata>> ListArtifactsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        _ = await projects.GetProjectAsync(projectId, cancellationToken);
        return await executionIndex.ListArtifactsAsync(projectId, cancellationToken);
    }

    public async Task<ArtifactMetadata> ApproveArtifactAsync(
        string projectId,
        string artifactId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var artifact = await executionIndex.GetArtifactAsync(projectId, artifactId, cancellationToken);
        var bytes = await projects.ReadArtifactAsync(projectId, artifact.RelativePath, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(hash, artifact.ContentHash))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Artifact '{artifactId}' failed its content hash check.");
        }

        var approved = await executionIndex.ApproveArtifactAsync(projectId, artifactId, cancellationToken);
        await projects.WriteContributionEventAsync(
            new ContributionEvent(
                Guid.NewGuid().ToString("N"),
                projectId,
                artifact.RevisionId,
                null,
                null,
                artifact.Id,
                actor,
                "artifact.approved",
                $"Approved artifact '{artifactId}'.",
                DateTimeOffset.UtcNow),
            cancellationToken);
        return approved;
    }

    public Task<byte[]> GetArtifactContentAsync(
        string projectId,
        string artifactId,
        CancellationToken cancellationToken = default) =>
        GetArtifactBytesAsync(projectId, artifactId, cancellationToken);

    public async Task<int> ClearCacheAsync(string projectId, CancellationToken cancellationToken = default)
    {
        _ = await projects.GetProjectAsync(projectId, cancellationToken);
        return await executionIndex.ClearCacheAsync(projectId, cancellationToken);
    }

    private async Task<byte[]> GetArtifactBytesAsync(
        string projectId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        var metadata = await executionIndex.GetArtifactAsync(projectId, artifactId, cancellationToken);
        var bytes = await projects.ReadArtifactAsync(projectId, metadata.RelativePath, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(hash, metadata.ContentHash))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Integrity, $"Artifact '{artifactId}' failed its content hash check.");
        }

        return bytes;
    }

    private async Task ValidatePanelExistsAsync(
        string projectId,
        string revisionId,
        string targetScope,
        CancellationToken cancellationToken)
    {
        var revision = await projects.GetRevisionAsync(projectId, revisionId, cancellationToken);
        foreach (var documentId in revision.DocumentHashes.Keys.Where(id => id.StartsWith("page.", StringComparison.Ordinal)))
        {
            var page = await projects.GetDocumentAsync(projectId, revisionId, documentId, cancellationToken);
            if (page.Content["panelIds"] is JsonArray panelIds &&
                panelIds.Any(panelId => StringComparer.Ordinal.Equals(panelId?.GetValue<string>(), targetScope)))
            {
                return;
            }
        }

        throw new WorkspaceException(WorkspaceErrorCode.Validation, $"Panel scope '{targetScope}' does not exist in revision '{revisionId}'.");
    }

    private static void ValidatePanelScope(string targetScope)
    {
        if (string.IsNullOrWhiteSpace(targetScope) ||
            !targetScope.StartsWith("panel.", StringComparison.Ordinal) ||
            targetScope.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new WorkspaceException(WorkspaceErrorCode.Validation, "A work unit target must be a valid panel scope.");
        }
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) && StringComparer.Ordinal.Equals(pair.Value, value));
}
