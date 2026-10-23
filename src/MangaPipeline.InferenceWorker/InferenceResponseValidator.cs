using System.Text.Json;

namespace MangaPipeline.InferenceWorker;

public static class InferenceResponseValidator
{
    public static string? Validate(string? responseFormat, string? content)
    {
        if (responseFormat != "json")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content ?? string.Empty);
            return null;
        }
        catch (JsonException)
        {
            return "The local model returned content that is not valid JSON.";
        }
    }
}
