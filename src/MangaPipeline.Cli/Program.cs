using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MangaPipeline.Core;

if (args.Length == 0 ||
    (args[0] == "validate" && args.Length != 2) ||
    (args[0] == "check-continuity" && args.Length != 2) ||
    (args[0] == "create" && args.Length != 3) ||
    (args[0] is not "validate" and not "check-continuity" and not "create"))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  MangaPipeline.Cli validate <definition-directory>");
    Console.Error.WriteLine("  MangaPipeline.Cli check-continuity <definition-directory>");
    Console.Error.WriteLine("  MangaPipeline.Cli create <project-name> <definition-directory>");
    return 2;
}

try
{
    var command = args[0];
    var projectName = command == "create" ? args[1] : null;
    var directory = Path.GetFullPath(args[command == "create" ? 2 : 1]);
    var documents = await SampleProjectLoader.LoadAsync(directory);

    if (command == "check-continuity")
    {
        var report = ContinuityAnalyzer.Analyze("local", directory, documents);
        var claimCount = documents.Sum(document =>
            document.Content["continuity"] is JsonObject facts ? facts.Count : 0);
        Console.WriteLine($"Checked {claimCount} continuity claims across {documents.Count} definitions.");

        foreach (var conflict in report.Conflicts)
        {
            Console.WriteLine($"Conflict: {conflict.FactId}");
            foreach (var claim in conflict.Claims)
            {
                Console.WriteLine($"  {claim.DocumentId} ({claim.DocumentKind}): {claim.Value.ToJsonString()}");
            }
        }

        if (report.Conflicts.Count == 0)
        {
            Console.WriteLine("No continuity conflicts found.");
        }

        return report.Conflicts.Count == 0 ? 0 : 1;
    }

    var files = Directory.EnumerateFiles(directory, "*.toml").ToList();
    var totalBytes = files.Sum(path => new FileInfo(path).Length);
    var largest = files
        .Select(path => (Name: Path.GetFileName(path), Size: new FileInfo(path).Length))
        .OrderByDescending(file => file.Size)
        .First();

    Console.WriteLine($"Valid: {documents.Count} documents");
    Console.WriteLine($"Total TOML data: {totalBytes:N0} bytes");
    Console.WriteLine($"Largest file: {largest.Name} ({largest.Size:N0} bytes)");
    Console.WriteLine("References and generation graph: valid");

    if (projectName is not null)
    {
        var dataDirectory = Environment.GetEnvironmentVariable("MANGA_PIPELINE_DATA")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MangaPipeline");
        var store = new LocalProjectStore(Path.Combine(dataDirectory, "workspace"));
        var project = await store.CreateProjectAsync(projectName, documents);
        Console.WriteLine($"Created project: {project.Name} ({project.Id})");
        Console.WriteLine($"Initial revision: {project.BranchHeads[project.MainBranchId]}");
        Console.WriteLine($"Workspace: {store.Root}");
    }

    return 0;
}
catch (WorkspaceException exception)
{
    Console.Error.WriteLine($"Invalid definitions ({exception.Code}): {exception.Message}");
    return 1;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Could not read definitions: {exception.Message}");
    return 1;
}
