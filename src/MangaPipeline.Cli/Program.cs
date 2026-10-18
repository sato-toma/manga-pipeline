using System;
using System.IO;
using System.Linq;
using MangaPipeline.Core;

if (args.Length != 2 || args[0] != "validate")
{
    Console.Error.WriteLine("Usage: MangaPipeline.Cli validate <definition-directory>");
    return 2;
}

try
{
    var directory = Path.GetFullPath(args[1]);
    var documents = await SampleProjectLoader.LoadAsync(directory);
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
