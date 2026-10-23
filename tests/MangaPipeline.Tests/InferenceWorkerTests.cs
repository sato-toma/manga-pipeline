using System.Collections.Generic;
using LLama.Common;
using MangaPipeline.InferenceWorker;

namespace MangaPipeline.Tests;

public sealed class InferenceWorkerTests
{
    [Fact]
    public void RequestValidatorAcceptsStatelessConversationHistory()
    {
        var request = new InferenceRequest(
        [
            new InferenceChatMessage("system", "Return a concise response."),
            new InferenceChatMessage("user", "What is the current premise?"),
            new InferenceChatMessage("assistant", "A character is looking for a lost book."),
            new InferenceChatMessage("user", "Ask a question if the motivation is unclear.")
        ]);

        Assert.Null(InferenceRequestValidator.Validate(request));
    }

    [Fact]
    public void RequestValidatorAcceptsJsonResponseFormat()
    {
        var request = new InferenceRequest(
            [new InferenceChatMessage("user", "Return a JSON object.")],
            ResponseFormat: "json");

        Assert.Null(InferenceRequestValidator.Validate(request));
    }

    [Theory]
    [InlineData("xml")]
    [InlineData(null)]
    public void RequestValidatorRejectsUnsupportedResponseFormat(string? responseFormat)
    {
        var request = new InferenceRequest(
            [new InferenceChatMessage("user", "Respond.")],
            ResponseFormat: responseFormat);

        Assert.NotNull(InferenceRequestValidator.Validate(request));
    }

    [Fact]
    public void Phi4HistoryTransformUsesModelChatTemplateAndGenerationPrompt()
    {
        var history = new ChatHistory();
        history.AddMessage(AuthorRole.System, "System instruction.");
        history.AddMessage(AuthorRole.User, "First question.");
        history.AddMessage(AuthorRole.Assistant, "First answer.");
        history.AddMessage(AuthorRole.User, "Follow-up.");

        var prompt = new Phi4MiniHistoryTransform().HistoryToText(history);

        Assert.Equal(
            "<|system|>System instruction.<|end|>" +
            "<|user|>First question.<|end|>" +
            "<|assistant|>First answer.<|end|>" +
            "<|user|>Follow-up.<|end|>" +
            "<|assistant|>",
            prompt);
    }

    [Theory]
    [InlineData("{\"change\":{\"title\":\"Example\"}}", null)]
    [InlineData("[1, true, null]", null)]
    [InlineData("This is prose, not JSON.", "The local model returned content that is not valid JSON.")]
    public void ResponseValidatorChecksJsonSyntax(string content, string? expectedError)
    {
        Assert.Equal(expectedError, InferenceResponseValidator.Validate("json", content));
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("tool")]
    public void RequestValidatorRejectsUnsupportedTurnEnd(string lastRole)
    {
        var request = new InferenceRequest(
        [
            new InferenceChatMessage("user", "Suggest a change."),
            new InferenceChatMessage(lastRole, "A response.")
        ]);

        Assert.NotNull(InferenceRequestValidator.Validate(request));
    }

    [Fact]
    public void RequestValidatorRejectsOutOfRangeOutputLimit()
    {
        var request = new InferenceRequest(
            [new InferenceChatMessage("user", "Suggest a change.")],
            InferenceRequestValidator.MaximumOutputTokens + 1);

        Assert.NotNull(InferenceRequestValidator.Validate(request));
    }

    [Fact]
    public void RequestValidatorRejectsOversizedMessageHistory()
    {
        var request = new InferenceRequest(
            [
                new InferenceChatMessage("user", new string('x', InferenceRequestValidator.MaximumMessageLength)),
                new InferenceChatMessage("assistant", new string('y', InferenceRequestValidator.MaximumMessageLength)),
                new InferenceChatMessage("user", "x")
            ]);

        Assert.NotNull(InferenceRequestValidator.Validate(request));
    }

    [Fact]
    public void RequestValidatorRejectsMissingHistory()
    {
        Assert.NotNull(InferenceRequestValidator.Validate(null));
        Assert.NotNull(InferenceRequestValidator.Validate(new InferenceRequest(new List<InferenceChatMessage>())));
    }
}
