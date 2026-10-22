using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MangaPipeline.Api;
using MangaPipeline.Core;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    var defaultUrls = builder.Configuration["MangaPipeline:Urls"];
    builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(defaultUrls)
        ? "http://127.0.0.1:5078"
        : defaultUrls);
}

var dataRoot = builder.Configuration["MangaPipeline:DataDirectory"]
    ?? Environment.GetEnvironmentVariable("MANGA_PIPELINE_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaPipeline");
var sampleDirectory = builder.Configuration["MangaPipeline:SampleDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "samples", "one-page");

var projectStore = new LocalProjectStore(Path.Combine(dataRoot, "workspace"));
var executionIndex = new SqliteExecutionIndex(Path.Combine(dataRoot, "execution-index.sqlite"));
await executionIndex.InitializeAsync();

builder.Services.AddSingleton(projectStore);
builder.Services.AddSingleton(executionIndex);
builder.Services.AddSingleton<GraphExecutionService>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddHostedService<GenerationJobWorker>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (WorkspaceException exception)
    {
        if (context.Response.HasStarted)
        {
            throw;
        }

        context.Response.Clear();
        context.Response.StatusCode = exception.Code switch
        {
            WorkspaceErrorCode.NotFound => (int)HttpStatusCode.NotFound,
            WorkspaceErrorCode.Conflict or WorkspaceErrorCode.InvalidState => (int)HttpStatusCode.Conflict,
            WorkspaceErrorCode.Validation => (int)HttpStatusCode.BadRequest,
            WorkspaceErrorCode.Integrity => (int)HttpStatusCode.UnprocessableEntity,
            _ => (int)HttpStatusCode.InternalServerError
        };
        await context.Response.WriteAsJsonAsync(
            new ApiError(exception.Code.ToString(), exception.Message),
            context.RequestAborted);
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", generationBackend = "mock-v1" }));

app.MapGet("/api/projects", async (WorkspaceService workspace, CancellationToken cancellationToken) =>
    Results.Ok(await workspace.ListProjectsAsync(cancellationToken)));

app.MapPost(
    "/api/projects",
    async (CreateProjectRequest request, WorkspaceService workspace, CancellationToken cancellationToken) =>
    {
        var documents = request.Documents
            ?? await SampleProjectLoader.LoadAsync(sampleDirectory, cancellationToken);
        var project = await workspace.CreateProjectAsync(request.Name, documents, cancellationToken);
        return Results.Created($"/api/projects/{project.Id}", project);
    });

app.MapGet("/api/projects/{projectId}", async (string projectId, WorkspaceService workspace, CancellationToken cancellationToken) =>
    Results.Ok(await workspace.GetProjectAsync(projectId, cancellationToken)));

app.MapPost(
    "/api/projects/{projectId}/concept-candidates",
    async (
        string projectId,
        CreateConceptCandidateRequest request,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
    {
        var candidate = await workspace.CreateConceptCandidateAsync(
            projectId,
            request.Elements,
            request.Relations,
            request.OpenQuestions,
            request.Actor,
            cancellationToken);
        return Results.Created(
            $"/api/projects/{projectId}/concept-candidates/{candidate.Id}",
            candidate);
    });

app.MapGet(
    "/api/projects/{projectId}/concept-candidates",
    async (string projectId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.ListConceptCandidatesAsync(projectId, cancellationToken)));

app.MapGet(
    "/api/projects/{projectId}/concept-candidates/{candidateId}",
    async (
        string projectId,
        string candidateId,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
        Results.Ok(await workspace.GetConceptCandidateAsync(projectId, candidateId, cancellationToken)));

app.MapGet(
    "/api/projects/{projectId}/revisions",
    async (string projectId, string? branchId, WorkspaceService workspace, CancellationToken cancellationToken) =>
    {
        var project = await workspace.GetProjectAsync(projectId, cancellationToken);
        var branch = branchId ?? project.MainBranchId;
        return Results.Ok(await workspace.GetRevisionsAsync(projectId, branch, cancellationToken));
    });

app.MapGet(
    "/api/projects/{projectId}/revisions/{revisionId}/documents",
    async (string projectId, string revisionId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.ListDocumentMetadataAsync(projectId, revisionId, cancellationToken)));

app.MapGet(
    "/api/projects/{projectId}/revisions/{revisionId}/documents/{documentId}",
    async (string projectId, string revisionId, string documentId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.GetDocumentAsync(projectId, revisionId, documentId, cancellationToken)));

app.MapGet(
    "/api/projects/{projectId}/revisions/{revisionId}/continuity",
    async (string projectId, string revisionId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.AnalyzeContinuityAsync(projectId, revisionId, cancellationToken)));

app.MapGet(
    "/api/projects/{projectId}/diff",
    async (
        string projectId,
        string fromRevisionId,
        string toRevisionId,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
        Results.Ok(await workspace.GetRevisionDiffAsync(projectId, fromRevisionId, toRevisionId, cancellationToken)));

app.MapPost(
    "/api/projects/{projectId}/branches",
    async (
        string projectId,
        CreateBranchRequest request,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
    {
        var branch = await workspace.CreateBranchAsync(
            projectId,
            request.Id,
            request.Name,
            request.ParentRevisionId,
            request.Actor,
            cancellationToken);
        return Results.Created($"/api/projects/{projectId}/branches/{branch.Id}", branch);
    });

app.MapPost(
    "/api/projects/{projectId}/work-units",
    async (
        string projectId,
        CreateWorkUnitRequest request,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
    {
        var workUnit = await workspace.CreateWorkUnitAsync(
            projectId,
            request.BranchId,
            request.BaseRevisionId,
            request.TargetScope,
            request.Title,
            request.Purpose,
            request.CompletionCriteria,
            request.Actor,
            cancellationToken);
        return Results.Created($"/api/projects/{projectId}/work-units/{workUnit.Id}", workUnit);
    });

app.MapGet(
    "/api/projects/{projectId}/work-units",
    async (string projectId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.ListWorkUnitsAsync(projectId, cancellationToken)));

app.MapGet(
    "/api/projects/{projectId}/work-units/{workUnitId}",
    async (string projectId, string workUnitId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.GetWorkUnitAsync(projectId, workUnitId, cancellationToken)));

app.MapPut(
    "/api/projects/{projectId}/work-units/{workUnitId}/proposal",
    async (
        string projectId,
        string workUnitId,
        SubmitWorkUnitRequest request,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
    {
        var submission = await workspace.SubmitWorkUnitAsync(
            projectId,
            workUnitId,
            new WorkUnitProposal(request.RawFeedback, request.Changes, request.SourceArtifactId),
            request.Actor,
            cancellationToken);
        if (submission.HasConflict)
        {
            return Results.Conflict(new ApiConflict(
                submission.WorkUnit,
                submission.ChangeSet,
                "The base revision is no longer the branch head. The proposal was retained for manual resolution."));
        }

        return Results.Ok(submission);
    });

app.MapPost(
    "/api/projects/{projectId}/change-sets",
    async (
        string projectId,
        CreateChangeSetRequest request,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
    {
        var changeSet = await workspace.CreateChangeSetAsync(
            projectId,
            request.BranchId,
            request.BaseRevisionId,
            request.RawFeedback,
            request.Actor,
            request.Changes,
            request.SourceArtifactId,
            cancellationToken: cancellationToken);
        if (changeSet.Status == "Conflict")
        {
            return Results.Conflict(changeSet);
        }

        return Results.Created($"/api/projects/{projectId}/change-sets/{changeSet.Id}", changeSet);
    });

app.MapGet(
    "/api/projects/{projectId}/change-sets/{changeSetId}",
    async (string projectId, string changeSetId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.GetChangeSetAsync(projectId, changeSetId, cancellationToken)));

app.MapPost(
    "/api/projects/{projectId}/change-sets/{changeSetId}/approve",
    async (
        string projectId,
        string changeSetId,
        ApproveChangeSetRequest request,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
    {
        var revision = await workspace.ApproveChangeSetAsync(projectId, changeSetId, request.Actor, cancellationToken);
        return Results.Created($"/api/projects/{projectId}/revisions/{revision.Id}", revision);
    });

app.MapPost(
    "/api/projects/{projectId}/revisions/{revisionId}/restore",
    async (
        string projectId,
        string revisionId,
        RestoreRevisionRequest request,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
    {
        var revision = await workspace.RestoreRevisionAsync(
            projectId,
            request.BranchId,
            revisionId,
            request.Actor,
            cancellationToken);
        return Results.Created($"/api/projects/{projectId}/revisions/{revision.Id}", revision);
    });

app.MapPost(
    "/api/projects/{projectId}/generation-jobs",
    async (
        string projectId,
        EnqueueGenerationRequest request,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
    {
        var job = await workspace.EnqueueGenerationAsync(
            projectId,
            request.RevisionId,
            request.BranchId,
            request.TargetNodeIds,
            cancellationToken);
        return Results.Accepted($"/api/generation-jobs/{job.Id}", job);
    });

app.MapGet(
    "/api/projects/{projectId}/generation-jobs",
    async (string projectId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.ListJobsAsync(projectId, cancellationToken)));

app.MapGet(
    "/api/generation-jobs/{jobId}",
    async (string jobId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.GetJobAsync(jobId, cancellationToken)));

app.MapPost(
    "/api/generation-jobs/{jobId}/retry",
    async (string jobId, WorkspaceService workspace, CancellationToken cancellationToken) =>
    {
        var job = await workspace.RetryJobAsync(jobId, cancellationToken);
        return Results.Accepted($"/api/generation-jobs/{job.Id}", job);
    });

app.MapGet(
    "/api/projects/{projectId}/artifacts",
    async (string projectId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.ListArtifactsAsync(projectId, cancellationToken)));

app.MapGet(
    "/api/projects/{projectId}/artifacts/{artifactId}/content",
    async (string projectId, string artifactId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.File(await workspace.GetArtifactContentAsync(projectId, artifactId, cancellationToken), "application/json"));

app.MapPost(
    "/api/projects/{projectId}/artifacts/{artifactId}/approve",
    async (
        string projectId,
        string artifactId,
        ApproveArtifactRequest request,
        WorkspaceService workspace,
        CancellationToken cancellationToken) =>
        Results.Ok(await workspace.ApproveArtifactAsync(projectId, artifactId, request.Actor, cancellationToken)));

app.MapDelete(
    "/api/projects/{projectId}/cache",
    async (string projectId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(new { removedCacheEntries = await workspace.ClearCacheAsync(projectId, cancellationToken), artifactsPreserved = true }));

app.MapGet(
    "/api/projects/{projectId}/revisions/{revisionId}/provenance",
    async (string projectId, string revisionId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.ExportProvenanceAsync(projectId, revisionId, cancellationToken)));

app.MapPost(
    "/api/provenance/verify",
    async (VerifyProvenanceRequest request, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.VerifyProvenanceAsync(request.Manifest, cancellationToken)));

app.MapGet(
    "/api/projects/{projectId}/contributions",
    async (string projectId, WorkspaceService workspace, CancellationToken cancellationToken) =>
        Results.Ok(await workspace.ListContributionEventsAsync(projectId, cancellationToken)));

app.Run();

public partial class Program { }
