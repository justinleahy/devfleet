using System.Text.Json;
using PiCommandCenter.Application.Completion;

namespace PiCommandCenter.Infrastructure.Completion;

internal static class CompletionJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string SerializeFiles(IReadOnlyList<string>? files) =>
        JsonSerializer.Serialize(files ?? [], Options);

    public static IReadOnlyList<string> DeserializeFiles(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeFindings(IReadOnlyList<ReviewFinding> findings) =>
        JsonSerializer.Serialize(findings, Options);

    public static IReadOnlyList<ReviewFinding> DeserializeFindings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ReviewFinding>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeSummary(string? summary) =>
        JsonSerializer.Serialize(summary ?? string.Empty, Options);

    public static string DeserializeSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(json, Options) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
