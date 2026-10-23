using System.Collections.Generic;

namespace MangaPipeline.InferenceWorker;

public sealed record InferenceChatMessage(string Role, string Content);

public sealed record InferenceRequest(
    List<InferenceChatMessage>? Messages,
    int? MaxOutputTokens = null,
    string? ResponseFormat = "text");

public sealed record InferenceResponse(string Role, string Content, string Model);

public sealed record InferenceError(string Code, string Message);
