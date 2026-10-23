using System;

namespace MangaPipeline.InferenceWorker;

public static class InferenceRequestValidator
{
    public const int MaximumMessageCount = 32;
    public const int MaximumMessageLength = 8_192;
    public const int MaximumTotalContentLength = 8_192;
    public const int MaximumOutputTokens = 1_024;

    public static string? Validate(InferenceRequest? request)
    {
        if (request?.Messages is null || request.Messages.Count == 0)
        {
            return "At least one chat message is required.";
        }

        var messages = request.Messages;
        if (messages.Count > MaximumMessageCount)
        {
            return $"No more than {MaximumMessageCount} messages are allowed.";
        }

        if (request.MaxOutputTokens is < 1 or > MaximumOutputTokens)
        {
            return $"MaxOutputTokens must be between 1 and {MaximumOutputTokens}.";
        }

        if (request.ResponseFormat is not ("text" or "json"))
        {
            return "ResponseFormat must be either 'text' or 'json'.";
        }

        var firstMessageIndex = messages[0] is { Role: "system" } ? 1 : 0;
        if (firstMessageIndex == messages.Count)
        {
            return "A user message is required.";
        }

        var expectedRole = "user";
        var totalLength = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message is null ||
                string.IsNullOrWhiteSpace(message.Content) ||
                message.Content.Length > MaximumMessageLength)
            {
                return $"Message {index} is empty or exceeds the allowed length.";
            }

            totalLength += message.Content.Length;
            if (totalLength > MaximumTotalContentLength)
            {
                return $"Total message content must not exceed {MaximumTotalContentLength} characters.";
            }

            if (index < firstMessageIndex)
            {
                continue;
            }

            if (!string.Equals(message.Role, expectedRole, StringComparison.Ordinal))
            {
                return $"Message {index} must have role '{expectedRole}'.";
            }

            expectedRole = expectedRole == "user" ? "assistant" : "user";
        }

        if (!string.Equals(messages[^1].Role, "user", StringComparison.Ordinal))
        {
            return "The last message must have role 'user'.";
        }

        return null;
    }
}
