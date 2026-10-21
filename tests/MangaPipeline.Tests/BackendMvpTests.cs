using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using MangaPipeline.Core;

namespace MangaPipeline.Tests;

public sealed class BackendMvpTests
{
    [Fact]
    public async Task SampleProjectStoresIndependentDocumentsAndReportsFileSizes()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var revisionId = project.BranchHeads["main"];
        var metadata = await fixture.Workspace.ListDocumentMetadataAsync(project.Id, revisionId);

        Assert.Equal(9, metadata.Count);
        Assert.All(metadata, item =>
        {
            Assert.True(item.SizeBytes > 0);
            Assert.Equal(64, item.ContentHash.Length);
        });
        Assert.Contains(metadata, item => item.DocumentId == "character.protagonist");

        var world = metadata.Single(item => item.DocumentId == "world");
        var worldPath = Path.Combine(
            fixture.Projects.Root,
            "projects",
            project.Id,
            "documents",
            "world",
            $"{world.ContentHash}.toml");
        Assert.True(File.Exists(worldPath));
        Assert.Contains("id = \"world\"", await File.ReadAllTextAsync(worldPath));
    }

    [Fact]
    public async Task ProjectCanBeCreatedFromCallerProvidedDocuments()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateProjectAsync("Custom project", fixture.Documents);
        var revision = await fixture.Workspace.GetRevisionAsync(project.Id, project.BranchHeads["main"]);
        var world = await fixture.Workspace.GetDocumentAsync(project.Id, revision.Id, "world");

        Assert.Equal("Custom project", project.Name);
        Assert.Equal("Initial project", revision.Reason);
        Assert.Equal(fixture.Documents.Count, revision.DocumentHashes.Count);
        Assert.Equal("あめつぶ書店", world.Content["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidProjectDefinitionsAreRejectedBeforeCreatingProjectFiles()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var invalidDocuments = fixture.Documents
            .Select(document => document.Id == "world"
                ? document with { References = ["missing-document"] }
                : document)
            .ToList();

        var error = await Assert.ThrowsAsync<WorkspaceException>(() =>
            fixture.Workspace.CreateProjectAsync("Invalid project", invalidDocuments));

        Assert.Equal(WorkspaceErrorCode.Validation, error.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Projects.Root, "projects")));
    }

    [Fact]
    public async Task ConceptCandidateGraphIsStoredSeparatelyFromCanonicalStory()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateProjectAsync("Concept candidates", fixture.Documents);
        var revisionId = project.BranchHeads["main"];
        var originalRevision = await fixture.Workspace.GetRevisionAsync(project.Id, revisionId);
        var originalStory = await fixture.Workspace.GetDocumentAsync(project.Id, revisionId, "story");

        var candidate = await fixture.Workspace.CreateConceptCandidateAsync(
            project.Id,
            [
                new ConceptCandidateElement("seed", "core-idea", "記憶を売買する古本屋", null),
                new ConceptCandidateElement("theme", "theme", "記憶と自己同一性", null),
                new ConceptCandidateElement("book", "motif", "古い本", "売られた記憶の象徴")
            ],
            [
                new ConceptCandidateRelation("seed", "expresses", "theme"),
                new ConceptCandidateRelation("seed", "appears-as", "book")
            ],
            ["記憶を失った人物は、何を自分らしさの根拠にするのか？"],
            "writer");

        var retrieved = await fixture.Workspace.GetConceptCandidateAsync(project.Id, candidate.Id);
        var listed = await fixture.Workspace.ListConceptCandidatesAsync(project.Id);
        var currentProject = await fixture.Workspace.GetProjectAsync(project.Id);
        var currentRevision = await fixture.Workspace.GetRevisionAsync(project.Id, revisionId);
        var currentStory = await fixture.Workspace.GetDocumentAsync(project.Id, revisionId, "story");

        Assert.Equal(candidate.Id, retrieved.Id);
        Assert.Equal(candidate.Elements, retrieved.Elements);
        Assert.Equal(candidate.Relations, retrieved.Relations);
        Assert.Equal(candidate.OpenQuestions, retrieved.OpenQuestions);
        Assert.Equal(candidate.Id, Assert.Single(listed).Id);
        Assert.Equal("writer", candidate.CreatedBy);
        Assert.Equal(revisionId, currentProject.BranchHeads["main"]);
        Assert.Equal(originalRevision.DocumentHashes, currentRevision.DocumentHashes);
        Assert.Equal(originalStory.Content.ToJsonString(), currentStory.Content.ToJsonString());
        Assert.DoesNotContain(
            typeof(ConceptCandidate).GetProperties(),
            property => property.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConceptCandidateRejectsRelationsToUnknownElements()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateProjectAsync("Invalid candidate", fixture.Documents);

        var error = await Assert.ThrowsAsync<WorkspaceException>(() =>
            fixture.Workspace.CreateConceptCandidateAsync(
                project.Id,
                [new ConceptCandidateElement("seed", "core-idea", "A concept", null)],
                [new ConceptCandidateRelation("seed", "expresses", "missing")],
                [],
                "writer"));

        Assert.Equal(WorkspaceErrorCode.Validation, error.Code);
        Assert.Empty(await fixture.Workspace.ListConceptCandidatesAsync(project.Id));
    }

    [Fact]
    public async Task CharacterChangeReusesBackgroundAndInvalidatesOnlyDependents()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var initialRevisionId = project.BranchHeads["main"];

        var first = await fixture.RunGraphAsync(project.Id, initialRevisionId);
        Assert.All(first.Nodes, node => Assert.False(node.CacheHit));
        Assert.True(first.GraphDefinitionBytes > 0);
        Assert.True(first.TotalInputBytes > 0);

        var second = await fixture.RunGraphAsync(project.Id, initialRevisionId);
        Assert.All(second.Nodes, node => Assert.True(node.CacheHit));

        var originalCharacter = await fixture.Projects.GetDocumentAsync(project.Id, initialRevisionId, "character.protagonist");
        var changedContent = JsonNode.Parse(originalCharacter.Content.ToJsonString())!.AsObject();
        changedContent["motivation"] = "自分の記憶も取り戻したい";
        var changes = new List<ProposedDocumentChange>
        {
            new(
                originalCharacter.Id,
                originalCharacter with { Content = changedContent })
        };
        var changeSet = await fixture.Workspace.CreateChangeSetAsync(
            project.Id,
            "main",
            initialRevisionId,
            "主人公の動機を明確にする",
            "tester",
            changes,
            null);
        var revised = await fixture.Workspace.ApproveChangeSetAsync(project.Id, changeSet.Id, "tester");

        var third = await fixture.RunGraphAsync(project.Id, revised.Id);
        var firstByNode = first.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var thirdByNode = third.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        Assert.True(thirdByNode["background"].CacheHit);
        Assert.Equal(firstByNode["background"].ArtifactId, thirdByNode["background"].ArtifactId);
        Assert.False(thirdByNode["character"].CacheHit);
        Assert.False(thirdByNode["panel-script"].CacheHit);
        Assert.False(thirdByNode["composition"].CacheHit);
    }

    [Fact]
    public async Task PanelChangeRegeneratesOnlyPanelAndDownstreamNodes()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var revisionId = project.BranchHeads["main"];
        var first = await fixture.RunGraphAsync(project.Id, revisionId);

        var panel = await fixture.Projects.GetDocumentAsync(project.Id, revisionId, "panel.1");
        var changedContent = JsonNode.Parse(panel.Content.ToJsonString())!.AsObject();
        changedContent["dialogue"] = "新しい台詞";
        var changeSet = await fixture.Workspace.CreateChangeSetAsync(
            project.Id,
            "main",
            revisionId,
            "コマ1の台詞を変更",
            "tester",
            [new ProposedDocumentChange("panel.1", panel with { Content = changedContent })],
            null);
        var revision = await fixture.Workspace.ApproveChangeSetAsync(project.Id, changeSet.Id, "tester");

        var report = await fixture.RunGraphAsync(project.Id, revision.Id);
        var results = report.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var original = first.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        Assert.True(results["background"].CacheHit);
        Assert.True(results["character"].CacheHit);
        Assert.Equal(original["background"].ArtifactId, results["background"].ArtifactId);
        Assert.False(results["panel-script"].CacheHit);
        Assert.False(results["composition"].CacheHit);
    }

    [Fact]
    public async Task InstructionSetChangeInvalidatesEveryNodeThatConsumesIt()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var revisionId = project.BranchHeads["main"];
        await fixture.RunGraphAsync(project.Id, revisionId);

        var instruction = await fixture.Projects.GetDocumentAsync(project.Id, revisionId, "instruction.illustration");
        var changedContent = JsonNode.Parse(instruction.Content.ToJsonString())!.AsObject();
        changedContent["version"] = 2;
        var changeSet = await fixture.Workspace.CreateChangeSetAsync(
            project.Id,
            "main",
            revisionId,
            "演出指示セットを更新",
            "tester",
            [new ProposedDocumentChange("instruction.illustration", instruction with { Content = changedContent })],
            null);
        var revision = await fixture.Workspace.ApproveChangeSetAsync(project.Id, changeSet.Id, "tester");

        var report = await fixture.RunGraphAsync(project.Id, revision.Id);

        Assert.All(report.Nodes, node => Assert.False(node.CacheHit));
    }

    [Fact]
    public async Task InterruptedRunningJobIsRequeuedAfterIndexRestart()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var job = await fixture.Workspace.EnqueueGenerationAsync(project.Id, project.BranchHeads["main"], "main", []);
        Assert.Equal(job.Id, (await fixture.Index.TryClaimNextJobAsync())!.Id);

        var restartedIndex = new SqliteExecutionIndex(fixture.IndexPath, pooling: false);
        await restartedIndex.InitializeAsync();
        var resumed = await restartedIndex.TryClaimNextJobAsync();

        Assert.NotNull(resumed);
        Assert.Equal(job.Id, resumed.Id);
        Assert.Equal("Running", resumed.Status);
    }

    [Fact]
    public async Task FailedReadDoesNotDestroyPreviouslyGeneratedArtifacts()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var revisionId = project.BranchHeads["main"];
        var report = await fixture.RunGraphAsync(project.Id, revisionId);
        var before = await fixture.Workspace.ListArtifactsAsync(project.Id);
        var graphRevision = await fixture.Workspace.GetRevisionAsync(project.Id, revisionId);
        var graphHash = graphRevision.DocumentHashes["graph"];
        var graphPath = Path.Combine(
            fixture.Projects.Root,
            "projects",
            project.Id,
            "documents",
            "graph",
            $"{graphHash}.toml");
        await File.WriteAllTextAsync(graphPath, "nodes = [");
        var job = await fixture.Workspace.EnqueueGenerationAsync(project.Id, revisionId, "main", []);
        var claimed = await fixture.Index.TryClaimNextJobAsync();
        Assert.NotNull(claimed);
        Assert.Equal(job.Id, claimed.Id);

        var error = await Assert.ThrowsAsync<WorkspaceException>(() => fixture.Execution.ExecuteAsync(claimed));
        Assert.Equal(WorkspaceErrorCode.Integrity, error.Code);

        var after = await fixture.Workspace.ListArtifactsAsync(project.Id);
        Assert.Equal(before.Count, after.Count);
        Assert.All(report.Nodes, node => Assert.Contains(after, artifact => artifact.Id == node.ArtifactId));
        foreach (var artifact in after)
        {
            Assert.NotEmpty(await fixture.Projects.ReadArtifactAsync(project.Id, artifact.RelativePath));
        }
    }

    [Fact]
    public async Task ArtifactReadRejectsPathsOutsideProjectOnEveryPlatform()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);

        var error = await Assert.ThrowsAsync<WorkspaceException>(() =>
            fixture.Projects.ReadArtifactAsync(project.Id, "../../outside.txt"));

        Assert.Equal(WorkspaceErrorCode.Validation, error.Code);
    }

    [Fact]
    public async Task WorkUnitConflictRetainsOfflineProposalAndRestoreCreatesNewRevision()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var baseRevisionId = project.BranchHeads["main"];
        var workUnit = await fixture.Workspace.CreateWorkUnitAsync(
            project.Id,
            "main",
            baseRevisionId,
            "panel.1",
            "コマ1の演出を見直す",
            "主人公の動機が伝わるようにする",
            ["動機を示す台詞か表情がある"],
            "tester");

        var story = await fixture.Projects.GetDocumentAsync(project.Id, baseRevisionId, "story");
        var competingContent = JsonNode.Parse(story.Content.ToJsonString())!.AsObject();
        competingContent["question"] = "記憶を取り戻したとき、何が変わる？";
        var competingChangeSet = await fixture.Workspace.CreateChangeSetAsync(
            project.Id,
            "main",
            baseRevisionId,
            "物語の問いを変更",
            "editor",
            [new ProposedDocumentChange("story", story with { Content = competingContent })],
            null);
        var competingRevision = await fixture.Workspace.ApproveChangeSetAsync(
            project.Id,
            competingChangeSet.Id,
            "editor");

        var panel = await fixture.Projects.GetDocumentAsync(project.Id, baseRevisionId, "panel.1");
        var proposalContent = JsonNode.Parse(panel.Content.ToJsonString())!.AsObject();
        proposalContent["dialogue"] = "オフラインで編集した台詞";
        var submission = await fixture.Workspace.SubmitWorkUnitAsync(
            project.Id,
            workUnit.Id,
            new WorkUnitProposal(
                "オフラインで台詞を修正",
                [new ProposedDocumentChange("panel.1", panel with { Content = proposalContent })]),
            "tester");

        Assert.True(submission.HasConflict);
        Assert.Equal("Conflict", submission.ChangeSet.Status);
        Assert.Equal("オフラインで台詞を修正", submission.WorkUnit.RawFeedback);
        Assert.Equal("Conflict", (await fixture.Workspace.GetWorkUnitAsync(project.Id, workUnit.Id)).Status);

        var restored = await fixture.Workspace.RestoreRevisionAsync(project.Id, "main", baseRevisionId, "tester");
        var baseRevision = await fixture.Workspace.GetRevisionAsync(project.Id, baseRevisionId);
        Assert.NotEqual(competingRevision.Id, restored.Id);
        Assert.Equal(competingRevision.Id, restored.ParentRevisionId);
        Assert.Equal(baseRevision.DocumentHashes["character.protagonist"], restored.DocumentHashes["character.protagonist"]);
        Assert.Equal(restored.Id, (await fixture.Workspace.GetProjectAsync(project.Id)).BranchHeads["main"]);
    }

    [Fact]
    public async Task StoryBranchesRemainIsolatedAndProvenanceDetectsTampering()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var baseRevisionId = project.BranchHeads["main"];
        await fixture.Workspace.CreateBranchAsync(project.Id, "if-route-b", "IF:別の結末", baseRevisionId, "tester");

        var story = await fixture.Projects.GetDocumentAsync(project.Id, baseRevisionId, "story");
        var branchContent = JsonNode.Parse(story.Content.ToJsonString())!.AsObject();
        branchContent["premise"] = "少女が雨をすべての人に返す。";
        var branchChangeSet = await fixture.Workspace.CreateChangeSetAsync(
            project.Id,
            "if-route-b",
            baseRevisionId,
            "IFルートの導入を変更",
            "tester",
            [new ProposedDocumentChange("story", story with { Content = branchContent })],
            null);
        var branchRevision = await fixture.Workspace.ApproveChangeSetAsync(project.Id, branchChangeSet.Id, "tester");

        var manifest = await fixture.Workspace.ExportProvenanceAsync(project.Id, branchRevision.Id);
        Assert.True((await fixture.Workspace.VerifyProvenanceAsync(manifest)).IsValid);
        var alteredHashes = new Dictionary<string, string>(manifest.DocumentHashes, StringComparer.Ordinal)
        {
            ["story"] = new string('0', 64)
        };
        var alteredManifest = manifest with { DocumentHashes = alteredHashes };
        Assert.False((await fixture.Workspace.VerifyProvenanceAsync(alteredManifest)).IsValid);

        var updatedProject = await fixture.Workspace.GetProjectAsync(project.Id);
        Assert.Equal(baseRevisionId, updatedProject.BranchHeads["main"]);
        Assert.Equal(branchRevision.Id, updatedProject.BranchHeads["if-route-b"]);
    }

    [Fact]
    public async Task ClearingCachePreservesApprovedArtifactsAndDefinitions()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var revisionId = project.BranchHeads["main"];
        var report = await fixture.RunGraphAsync(project.Id, revisionId);
        var approvedArtifact = await fixture.Workspace.ApproveArtifactAsync(
            project.Id,
            report.Nodes[0].ArtifactId,
            "tester");

        var removedEntries = await fixture.Workspace.ClearCacheAsync(project.Id);
        var artifacts = await fixture.Workspace.ListArtifactsAsync(project.Id);
        var definition = await fixture.Workspace.GetDocumentAsync(project.Id, revisionId, "world");

        Assert.True(removedEntries > 0);
        Assert.Contains(artifacts, artifact => artifact.Id == approvedArtifact.Id && artifact.IsApproved);
        Assert.Equal("あめつぶ書店", definition.Content["title"]!.GetValue<string>());
        Assert.NotEmpty(await fixture.Workspace.GetArtifactContentAsync(project.Id, approvedArtifact.Id));
    }

    [Fact]
    public async Task InvalidGraphCycleFailsBeforeGeneratingNodes()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        var project = await fixture.Workspace.CreateSampleProjectAsync("Sample", fixture.Documents);
        var baseRevisionId = project.BranchHeads["main"];
        var graphDocument = await fixture.Projects.GetDocumentAsync(project.Id, baseRevisionId, "graph");
        var changedGraph = JsonNode.Parse(graphDocument.Content.ToJsonString())!.AsObject();
        changedGraph["nodes"]!.AsArray()[0]!["dependsOnNodeIds"] = new JsonArray("composition");
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => fixture.Projects.CreateRevisionAsync(
            project.Id,
            "main",
            baseRevisionId,
            new Dictionary<string, DocumentDefinition>(StringComparer.Ordinal)
            {
                ["graph"] = graphDocument with { Content = changedGraph }
            },
            "tester",
            "Create an invalid graph for validation",
            null));

        Assert.Equal(WorkspaceErrorCode.Validation, error.Code);
        Assert.Empty(await fixture.Workspace.ListArtifactsAsync(project.Id));
        Assert.Equal(baseRevisionId, (await fixture.Workspace.GetProjectAsync(project.Id)).BranchHeads["main"]);
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly string _root;

        private WorkspaceFixture(string root, List<DocumentDefinition> documents)
        {
            _root = root;
            Documents = documents;
            Projects = new LocalProjectStore(Path.Combine(root, "workspace"));
            Index = new SqliteExecutionIndex(Path.Combine(root, "execution-index.sqlite"), pooling: false);
            Workspace = new WorkspaceService(Projects, Index);
            Execution = new GraphExecutionService(Projects, Index);
        }

        public List<DocumentDefinition> Documents { get; }
        public LocalProjectStore Projects { get; }
        public SqliteExecutionIndex Index { get; }
        public string IndexPath => Path.Combine(_root, "execution-index.sqlite");
        public WorkspaceService Workspace { get; }
        public GraphExecutionService Execution { get; }

        public static async Task<WorkspaceFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "manga-pipeline-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var documents = await SampleProjectLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "one-page"));
            var fixture = new WorkspaceFixture(root, documents);
            await fixture.Index.InitializeAsync();
            return fixture;
        }

        public async Task<GenerationRunReport> RunGraphAsync(string projectId, string revisionId)
        {
            var queued = await Workspace.EnqueueGenerationAsync(projectId, revisionId, "main", []);
            var claimed = await Index.TryClaimNextJobAsync();
            Assert.NotNull(claimed);
            Assert.Equal(queued.Id, claimed.Id);
            var report = await Execution.ExecuteAsync(claimed);
            await Index.CompleteJobAsync(claimed.Id, report);
            var finished = await Workspace.GetJobAsync(claimed.Id);
            Assert.Equal("Succeeded", finished.Status);
            return finished.Report ?? throw new Xunit.Sdk.XunitException("Completed job did not persist its report.");
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
