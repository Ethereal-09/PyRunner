using System.Text.Json;
using PyRunner.Models;

namespace PyRunner.Services.AI;

public sealed class AiChangeReviewService : IAiChangeReviewService
{
    public AiCodeCandidate? ParseCandidate(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText) || responseText.Length > OpenAiResponsesClient.MaximumResponseBytes) return null;
        try
        {
            var trimmed = responseText.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLine = trimmed.IndexOf('\n');
                var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (firstLine < 0 || lastFence <= firstLine) return null;
                trimmed = trimmed[(firstLine + 1)..lastFence].Trim();
            }
            using var json = JsonDocument.Parse(trimmed);
            var root = json.RootElement;
            if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String) return null;
            if (root.TryGetProperty("changes", out var changesElement))
            {
                if (changesElement.ValueKind != JsonValueKind.Array || changesElement.GetArrayLength() is < 1 or > 8) return null;
                var files = new List<AiFileChange>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in changesElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String ||
                        !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;
                    var name = file.GetString() ?? string.Empty;
                    var codeValue = content.GetString() ?? string.Empty;
                    if (name != Path.GetFileName(name) || !name.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
                        !names.Add(name) || codeValue.Length == 0 || codeValue.Length > AiContextBuilder.MaximumContextCharacters) return null;
                    var itemSummary = item.TryGetProperty("summary", out var itemSummaryElement) &&
                                      itemSummaryElement.ValueKind == JsonValueKind.String
                        ? itemSummaryElement.GetString() ?? string.Empty : string.Empty;
                    files.Add(new(name, codeValue, itemSummary));
                }
                var notesValue = root.TryGetProperty("notes", out var changeNotes) && changeNotes.ValueKind == JsonValueKind.String
                    ? changeNotes.GetString() ?? string.Empty : string.Empty;
                return new(summary.GetString() ?? string.Empty, files[0].Code, notesValue, files);
            }
            if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String) return null;
            var codeText = code.GetString() ?? string.Empty;
            if (codeText.Length == 0 || codeText.Length > AiContextBuilder.MaximumContextCharacters) return null;
            var notes = root.TryGetProperty("notes", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() ?? string.Empty : string.Empty;
            return new AiCodeCandidate(summary.GetString() ?? string.Empty, codeText, notes);
        }
        catch (JsonException) { return null; }
    }

    public bool IsOriginalCurrent(AiEditorContext original, AiEditorContext current) =>
        original.BufferSha256.Equals(current.BufferSha256, StringComparison.OrdinalIgnoreCase) &&
        original.SelectionStart == current.SelectionStart && original.SelectionEnd == current.SelectionEnd &&
        original.Text.Equals(current.Text, StringComparison.Ordinal);

    public string CreateLineDiff(string original, string candidate)
    {
        var before = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var after = candidate.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new System.Text.StringBuilder();
        var limit = Math.Min(Math.Max(before.Length, after.Length), 2000);
        for (var i = 0; i < limit; i++)
        {
            var oldLine = i < before.Length ? before[i] : null;
            var newLine = i < after.Length ? after[i] : null;
            if (oldLine == newLine) output.Append("  ").AppendLine(oldLine);
            else
            {
                if (oldLine is not null) output.Append("- ").AppendLine(oldLine);
                if (newLine is not null) output.Append("+ ").AppendLine(newLine);
            }
        }
        if (Math.Max(before.Length, after.Length) > limit) output.AppendLine("… diff truncated …");
        return output.ToString();
    }
}
