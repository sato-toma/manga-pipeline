using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MangaPipeline.InferenceWorker;

const string modelId =
    "unsloth/Phi-4-mini-instruct-GGUF@78eb92a46fc37e6b524df991ed9aca9bc6aa7b80:Q4_K_M";
const string expectedModelSha256 =
    "88c00229914083cd112853aab84ed51b87bdf6b9ce42f532d8c85c7c63b1730a";

var builder = WebApplication.CreateBuilder(args);
var modelPath = builder.Configuration["MangaPipeline:Inference:ModelPath"];
if (string.IsNullOrWhiteSpace(modelPath))
{
    throw new InvalidOperationException(
        "Set MangaPipeline:Inference:ModelPath to an externally provisioned GGUF model file.");
}

if (!File.Exists(modelPath) ||
    !string.Equals(Path.GetExtension(modelPath), ".gguf", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "MangaPipeline:Inference:ModelPath must point to an existing GGUF model file.");
}

await using (var modelFile = File.OpenRead(modelPath))
{
    var actualModelSha256 = Convert.ToHexString(
        await SHA256.HashDataAsync(modelFile, CancellationToken.None)).ToLowerInvariant();
    if (!string.Equals(actualModelSha256, expectedModelSha256, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"The GGUF model SHA-256 does not match the pinned model '{modelId}'.");
    }
}

var contextSize = 4096u;
var contextSizeValue = builder.Configuration["MangaPipeline:Inference:ContextSize"];
if (contextSizeValue is not null &&
    (!uint.TryParse(contextSizeValue, out contextSize) || contextSize < 512))
{
    throw new InvalidOperationException(
        "MangaPipeline:Inference:ContextSize must be an integer of at least 512.");
}

var port = 5079;
var portValue = builder.Configuration["MangaPipeline:Inference:Port"];
if (portValue is not null &&
    (!int.TryParse(portValue, out port) || port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort))
{
    throw new InvalidOperationException(
        "MangaPipeline:Inference:Port must be a valid TCP port.");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(port);
    options.Limits.MaxRequestBodySize = 64 * 1024;
});

var inferenceEngine = new InferenceEngine(modelPath, modelId, contextSize);
builder.Services.AddSingleton(inferenceEngine);

var app = builder.Build();

app.MapGet("/health", (InferenceEngine engine) =>
    Results.Ok(new { status = "ready", model = engine.ModelId }));

app.MapPost(
    "/v1/chat/completions",
    async (
        InferenceRequest request,
        InferenceEngine engine,
        CancellationToken cancellationToken) =>
    {
        var validationError = InferenceRequestValidator.Validate(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new InferenceError("invalid_request", validationError));
        }

        var content = await engine.CompleteAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "The local model returned an empty response.",
                extensions: new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["code"] = "empty_model_output"
                });
        }

        var responseValidationError =
            InferenceResponseValidator.Validate(request.ResponseFormat, content);
        if (responseValidationError is not null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: responseValidationError,
                extensions: new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["code"] = "invalid_structured_output"
                });
        }

        return Results.Ok(new InferenceResponse("assistant", content, engine.ModelId));
    });

await app.RunAsync();

public partial class Program { }
