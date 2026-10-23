using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace MangaPipeline.InferenceWorker;

public sealed class InferenceEngine : IDisposable
{
    private const string JsonGrammar = """
        root ::= value
        value ::= object | array | string | number | "true" | "false" | "null"
        object ::= "{" ws (string ws ":" ws value (ws "," ws string ws ":" ws value)*)? ws "}"
        array ::= "[" ws (value (ws "," ws value)*)? ws "]"
        string ::= "\"" ([^"\\\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F]{4}))* "\""
        number ::= "-"? ("0" | [1-9] [0-9]*) ("." [0-9]+)? ([eE] [+-]? [0-9]+)?
        ws ::= [ \t\n\r]*
        """;

    private readonly LLamaWeights weights;
    private readonly string modelPath;
    private readonly uint contextSize;
    private readonly SemaphoreSlim inferenceLock = new(1, 1);

    public InferenceEngine(string modelPath, string modelId, uint contextSize)
    {
        this.modelPath = modelPath;
        ModelId = modelId;
        this.contextSize = contextSize;
        weights = LLamaWeights.LoadFromFile(new ModelParams(modelPath)
        {
            ContextSize = contextSize
        });
    }

    public string ModelId { get; }

    public async Task<string> CompleteAsync(
        InferenceRequest request,
        CancellationToken cancellationToken)
    {
        await inferenceLock.WaitAsync(cancellationToken);
        try
        {
            using var context = weights.CreateContext(new ModelParams(modelPath)
            {
                ContextSize = contextSize
            });
            var session = new ChatSession(new InteractiveExecutor(context))
                .WithHistoryTransform(new Phi4MiniHistoryTransform());
            var history = new ChatHistory();
            foreach (var message in request.Messages!)
            {
                history.AddMessage(message.Role switch
                {
                    "system" => AuthorRole.System,
                    "assistant" => AuthorRole.Assistant,
                    _ => AuthorRole.User
                }, message.Content);
            }

            using var samplingPipeline = new DefaultSamplingPipeline
            {
                Grammar = request.ResponseFormat == "json"
                    ? new Grammar(JsonGrammar, "root")
                    : null
            };
            var inferenceParameters = new InferenceParams
            {
                MaxTokens = request.MaxOutputTokens ?? 512,
                SamplingPipeline = samplingPipeline
            };
            var output = new StringBuilder();
            await foreach (var token in session.ChatAsync(history, inferenceParameters, cancellationToken))
            {
                output.Append(token);
            }

            return output.ToString();
        }
        finally
        {
            inferenceLock.Release();
        }
    }

    public void Dispose()
    {
        inferenceLock.Dispose();
        weights.Dispose();
    }
}
