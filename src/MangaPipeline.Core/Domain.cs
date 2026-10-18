using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace MangaPipeline.Core;

public sealed record DocumentDefinition(
    string Id,
    string Kind,
    string Scope,
    int SchemaVersion,
    JsonObject Content,
    List<string> References);

public sealed record DocumentFileMetadata(string DocumentId, string ContentHash, long SizeBytes);

public sealed record ProjectManifest(
    string Id,
    string Name,
    string MainBranchId,
    Dictionary<string, string> BranchHeads,
    DateTimeOffset CreatedAt);

public sealed record StoryBranch(
    string Id,
    string Name,
    string ParentRevisionId,
    DateTimeOffset CreatedAt);

public sealed record RevisionSnapshot(
    string Id,
    string ProjectId,
    string BranchId,
    string? ParentRevisionId,
    string? ChangeSetId,
    string CreatedBy,
    string Reason,
    DateTimeOffset CreatedAt,
    Dictionary<string, string> DocumentHashes,
    string SnapshotHash);

public sealed record ProposedDocumentChange(
    string DocumentId,
    DocumentDefinition ProposedDocument);

public sealed record ChangeSet(
    string Id,
    string ProjectId,
    string BranchId,
    string BaseRevisionId,
    string RawFeedback,
    string CreatedBy,
    string? WorkUnitId,
    string? SourceArtifactId,
    string Status,
    List<ProposedDocumentChange> Changes,
    DateTimeOffset CreatedAt);

public sealed record WorkUnit(
    string Id,
    string ProjectId,
    string BranchId,
    string BaseRevisionId,
    string TargetScope,
    string Title,
    string Purpose,
    List<string> CompletionCriteria,
    string Status,
    string? RawFeedback,
    List<ProposedDocumentChange> DraftChanges,
    string? ChangeSetId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WorkUnitProposal(
    string RawFeedback,
    List<ProposedDocumentChange> Changes,
    string? SourceArtifactId = null);

public sealed record GenerationNodeDefinition(
    string Id,
    string Kind,
    int Version,
    List<string> InputDocumentIds,
    List<string> DependsOnNodeIds);

public sealed record GenerationGraph(List<GenerationNodeDefinition> Nodes);

public sealed record ArtifactMetadata(
    string Id,
    string ProjectId,
    string RevisionId,
    string NodeId,
    string CacheKey,
    string ContentHash,
    string RelativePath,
    long SizeBytes,
    bool IsApproved,
    DateTimeOffset CreatedAt);

public sealed record GenerationNodeResult(
    string NodeId,
    string CacheKey,
    string ArtifactId,
    string ContentHash,
    long InputBytes,
    bool CacheHit,
    long PreparationMilliseconds,
    long GenerationMilliseconds,
    long PersistenceMilliseconds);

public sealed record GenerationRunReport(
    string JobId,
    string ProjectId,
    string RevisionId,
    string BranchId,
    string Status,
    List<GenerationNodeResult> Nodes,
    long GraphDefinitionBytes,
    long TotalInputBytes,
    long PreparationMilliseconds,
    long GenerationMilliseconds,
    long PersistenceMilliseconds,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record GenerationJob(
    string Id,
    string ProjectId,
    string RevisionId,
    string BranchId,
    List<string> TargetNodeIds,
    string Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    GenerationRunReport? Report);

public sealed record ContributionEvent(
    string Id,
    string ProjectId,
    string RevisionId,
    string? WorkUnitId,
    string? ChangeSetId,
    string? SourceArtifactId,
    string Actor,
    string Action,
    string Reason,
    DateTimeOffset CreatedAt);

public sealed record ProvenanceManifest(
    int SchemaVersion,
    string ProjectId,
    string RevisionId,
    string BranchId,
    string SnapshotHash,
    Dictionary<string, string> DocumentHashes,
    string ManifestHash);

public enum WorkspaceErrorCode
{
    NotFound,
    Conflict,
    Validation,
    InvalidState,
    Integrity
}

public sealed class WorkspaceException(WorkspaceErrorCode code, string message) : Exception(message)
{
    public WorkspaceErrorCode Code { get; } = code;
}
