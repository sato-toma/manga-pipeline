using System;
using System.Text;
using LLama.Abstractions;
using LLama.Common;

namespace MangaPipeline.InferenceWorker;

public sealed class Phi4MiniHistoryTransform : IHistoryTransform
{
    public IHistoryTransform Clone() => new Phi4MiniHistoryTransform();

    public string HistoryToText(ChatHistory history)
    {
        var prompt = new StringBuilder();
        foreach (var message in history.Messages)
        {
            var role = message.AuthorRole switch
            {
                AuthorRole.System => "system",
                AuthorRole.User => "user",
                AuthorRole.Assistant => "assistant",
                _ => throw new InvalidOperationException(
                    $"Phi-4 does not support the '{message.AuthorRole}' chat role.")
            };

            prompt.Append("<|")
                .Append(role)
                .Append("|>")
                .Append(message.Content)
                .Append("<|end|>");
        }

        return prompt.Append("<|assistant|>").ToString();
    }

    public ChatHistory TextToHistory(AuthorRole role, string text)
    {
        var history = new ChatHistory();
        history.AddMessage(role, text);
        return history;
    }
}
