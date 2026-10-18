using System.Collections.Generic;
using MangaPipeline.Core;

namespace MangaPipeline.Api;

public sealed record CreateProjectRequest(string Name);

public sealed record CreateBranchRequest(string Id, string Name, string ParentRevisionId, string Actor);

public sealed record CreateWorkUnitRequest(
    string BranchId,
    string BaseRevisionId,
    string TargetScope,
    string Title,
    string Purpose,
    List<string> CompletionCriteria,
    string Actor);

public sealed record SubmitWorkUnitRequest(
    string RawFeedback,
    List<ProposedDocumentChange> Changes,
    string Actor,
    string? SourceArtifactId);

public sealed record CreateChangeSetRequest(
    string BranchId,
    string BaseRevisionId,
    string RawFeedback,
    string Actor,
    List<ProposedDocumentChange> Changes,
    string? SourceArtifactId);

public sealed record ApproveChangeSetRequest(string Actor);

public sealed record RestoreRevisionRequest(string BranchId, string Actor);

public sealed record EnqueueGenerationRequest(
    string RevisionId,
    string BranchId,
    List<string> TargetNodeIds);

public sealed record ApproveArtifactRequest(string Actor);

public sealed record VerifyProvenanceRequest(ProvenanceManifest Manifest);

public sealed record ApiError(string Code, string Message);

public sealed record ApiConflict(WorkUnit WorkUnit, ChangeSet ChangeSet, string Message);
