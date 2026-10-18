using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MangaPipeline.Core;

public sealed class SqliteExecutionIndex
{
    private readonly string _connectionString;

    public SqliteExecutionIndex(string path, bool pooling = true)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = pooling,
            ForeignKeys = true,
            DefaultTimeout = 30
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                revision_id TEXT NOT NULL,
                branch_id TEXT NOT NULL,
                target_node_ids_json TEXT NOT NULL,
                status TEXT NOT NULL,
                error TEXT NULL,
                created_at TEXT NOT NULL,
                started_at TEXT NULL,
                completed_at TEXT NULL,
                report_json TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_jobs_status_created ON jobs(status, created_at);
            CREATE INDEX IF NOT EXISTS ix_jobs_project_created ON jobs(project_id, created_at);
            CREATE TABLE IF NOT EXISTS artifacts (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                revision_id TEXT NOT NULL,
                node_id TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                is_approved INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_artifacts_project_created ON artifacts(project_id, created_at);
            CREATE TABLE IF NOT EXISTS cache_entries (
                project_id TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                artifact_id TEXT NOT NULL,
                PRIMARY KEY (project_id, cache_key),
                FOREIGN KEY (artifact_id) REFERENCES artifacts(id)
            );
            UPDATE jobs SET status = 'Queued', started_at = NULL WHERE status = 'Running';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueAsync(GenerationJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (
                id, project_id, revision_id, branch_id, target_node_ids_json,
                status, error, created_at, started_at, completed_at, report_json
            ) VALUES (
                $id, $projectId, $revisionId, $branchId, $targets,
                $status, NULL, $createdAt, NULL, NULL, NULL
            );
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$projectId", job.ProjectId);
        command.Parameters.AddWithValue("$revisionId", job.RevisionId);
        command.Parameters.AddWithValue("$branchId", job.BranchId);
        command.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(job.TargetNodeIds, JsonSupport.Options));
        command.Parameters.AddWithValue("$status", "Queued");
        command.Parameters.AddWithValue("$createdAt", Format(job.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GenerationJob?> TryClaimNextJobAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET status = 'Running', started_at = $startedAt
            WHERE id = (
                SELECT id FROM jobs WHERE status = 'Queued' ORDER BY created_at LIMIT 1
            ) AND status = 'Queued'
            RETURNING id, project_id, revision_id, branch_id, target_node_ids_json,
                      status, error, created_at, started_at, completed_at, report_json;
            """;
        command.Parameters.AddWithValue("$startedAt", Format(DateTimeOffset.UtcNow));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async Task<GenerationJob> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectJob + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadJob(reader)
            : throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Job '{jobId}' does not exist.");
    }

    public async Task<List<GenerationJob>> ListJobsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectJob + " WHERE project_id = $projectId ORDER BY created_at;";
        command.Parameters.AddWithValue("$projectId", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<GenerationJob>();
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    public async Task CompleteJobAsync(
        string jobId,
        GenerationRunReport report,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET status = $status, error = NULL, completed_at = $completedAt, report_json = $report
            WHERE id = $id AND status = 'Running';
            """;
        command.Parameters.AddWithValue("$status", report.Status);
        command.Parameters.AddWithValue("$completedAt", Format(report.CompletedAt));
        command.Parameters.AddWithValue("$report", JsonSerializer.Serialize(report, JsonSupport.Options));
        command.Parameters.AddWithValue("$id", jobId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new WorkspaceException(WorkspaceErrorCode.InvalidState, $"Job '{jobId}' is not running.");
        }
    }

    public async Task FailJobAsync(
        string jobId,
        string error,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET status = 'Failed', error = $error, completed_at = $completedAt
            WHERE id = $id AND status = 'Running';
            """;
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$completedAt", Format(completedAt));
        command.Parameters.AddWithValue("$id", jobId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new WorkspaceException(WorkspaceErrorCode.InvalidState, $"Job '{jobId}' is not running.");
        }
    }

    public async Task<ArtifactMetadata?> GetCacheEntryAsync(
        string projectId,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.project_id, a.revision_id, a.node_id, a.cache_key,
                   a.content_hash, a.relative_path, a.size_bytes, a.is_approved, a.created_at
            FROM cache_entries c
            JOIN artifacts a ON a.id = c.artifact_id
            WHERE c.project_id = $projectId AND c.cache_key = $cacheKey;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$cacheKey", cacheKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadArtifact(reader) : null;
    }

    public async Task StoreArtifactAsync(
        ArtifactMetadata artifact,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var artifactCommand = connection.CreateCommand())
        {
            artifactCommand.Transaction = (SqliteTransaction)transaction;
            artifactCommand.CommandText = """
                INSERT INTO artifacts (
                    id, project_id, revision_id, node_id, cache_key, content_hash,
                    relative_path, size_bytes, is_approved, created_at
                ) VALUES (
                    $id, $projectId, $revisionId, $nodeId, $cacheKey, $contentHash,
                    $relativePath, $sizeBytes, 0, $createdAt
                );
                """;
            AddArtifactParameters(artifactCommand, artifact);
            await artifactCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cacheCommand = connection.CreateCommand())
        {
            cacheCommand.Transaction = (SqliteTransaction)transaction;
            cacheCommand.CommandText = """
                INSERT INTO cache_entries(project_id, cache_key, artifact_id)
                VALUES($projectId, $cacheKey, $artifactId);
                """;
            cacheCommand.Parameters.AddWithValue("$projectId", artifact.ProjectId);
            cacheCommand.Parameters.AddWithValue("$cacheKey", artifact.CacheKey);
            cacheCommand.Parameters.AddWithValue("$artifactId", artifact.Id);
            await cacheCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ArtifactMetadata> GetArtifactAsync(
        string projectId,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, project_id, revision_id, node_id, cache_key,
                   content_hash, relative_path, size_bytes, is_approved, created_at
            FROM artifacts WHERE project_id = $projectId AND id = $artifactId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$artifactId", artifactId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadArtifact(reader)
            : throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Artifact '{artifactId}' does not exist.");
    }

    public async Task<List<ArtifactMetadata>> ListArtifactsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, project_id, revision_id, node_id, cache_key,
                   content_hash, relative_path, size_bytes, is_approved, created_at
            FROM artifacts WHERE project_id = $projectId ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var artifacts = new List<ArtifactMetadata>();
        while (await reader.ReadAsync(cancellationToken))
        {
            artifacts.Add(ReadArtifact(reader));
        }

        return artifacts;
    }

    public async Task<ArtifactMetadata> ApproveArtifactAsync(
        string projectId,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE artifacts SET is_approved = 1
            WHERE project_id = $projectId AND id = $artifactId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$artifactId", artifactId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new WorkspaceException(WorkspaceErrorCode.NotFound, $"Artifact '{artifactId}' does not exist.");
        }

        return await GetArtifactAsync(projectId, artifactId, cancellationToken);
    }

    public async Task<int> ClearCacheAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cache_entries WHERE project_id = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveCacheEntryAsync(
        string projectId,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cache_entries WHERE project_id = $projectId AND cache_key = $cacheKey;";
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$cacheKey", cacheKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SelectJob = """
        SELECT id, project_id, revision_id, branch_id, target_node_ids_json,
               status, error, created_at, started_at, completed_at, report_json
        FROM jobs
        """;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static GenerationJob ReadJob(SqliteDataReader reader)
    {
        var reportJson = reader.IsDBNull(10) ? null : reader.GetString(10);
        return new GenerationJob(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonSupport.Options) ?? [],
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reportJson is null ? null : JsonSerializer.Deserialize<GenerationRunReport>(reportJson, JsonSupport.Options));
    }

    private static ArtifactMetadata ReadArtifact(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8) != 0,
            Parse(reader.GetString(9)));

    private static void AddArtifactParameters(SqliteCommand command, ArtifactMetadata artifact)
    {
        command.Parameters.AddWithValue("$id", artifact.Id);
        command.Parameters.AddWithValue("$projectId", artifact.ProjectId);
        command.Parameters.AddWithValue("$revisionId", artifact.RevisionId);
        command.Parameters.AddWithValue("$nodeId", artifact.NodeId);
        command.Parameters.AddWithValue("$cacheKey", artifact.CacheKey);
        command.Parameters.AddWithValue("$contentHash", artifact.ContentHash);
        command.Parameters.AddWithValue("$relativePath", artifact.RelativePath);
        command.Parameters.AddWithValue("$sizeBytes", artifact.SizeBytes);
        command.Parameters.AddWithValue("$createdAt", Format(artifact.CreatedAt));
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
