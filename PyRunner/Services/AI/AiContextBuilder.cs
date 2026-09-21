using PyRunner.Models;
using System.Text.RegularExpressions;

namespace PyRunner.Services.AI;

public sealed partial class AiContextBuilder : IAiContextBuilder
{
    public const int MaximumContextCharacters = 120_000;
    private readonly ISecretScanService _scanner;
    public AiContextBuilder(ISecretScanService scanner) => _scanner = scanner;

    public AiSendPreview BuildPreview(AiConfiguration configuration, AiEditorContext context, string? userGoal = null)
    {
        var transportText = RedactAbsolutePaths(context.Text);
        var goal = RedactAbsolutePaths(SanitizeGoal(userGoal) ?? string.Empty);
        var previewText = string.IsNullOrWhiteSpace(goal) ? transportText : transportText + "\n\n" + goal;
        if (transportText.Length == 0 || previewText.Length > MaximumContextCharacters)
            throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_ContextTooLarge");
        return new AiSendPreview(configuration.Endpoint.Authority, configuration.Model,
            context.IsSelection ? "selection" : "current-file", previewText.Length,
            Math.Max(1, (int)Math.Ceiling(previewText.Length / 4d)), _scanner.Scan(previewText));
    }

    public AiProviderRequest BuildRequest(AiOperation operation, AiEditorContext context, string? userGoal)
    {
        var structured = operation != AiOperation.Explain;
        return new AiProviderRequest(operation, RedactAbsolutePaths(context.Text),
            RedactAbsolutePaths(SanitizeGoal(userGoal) ?? string.Empty), context.PythonVersion, structured);
    }

    public AiProviderRequest BuildConversationRequest(AiPermissionMode mode, IReadOnlyList<AiContextFile> files,
        IReadOnlyList<AiConversationTurn> history, string prompt)
    {
        if (files.Count is < 1 or > 8 || string.IsNullOrWhiteSpace(prompt))
            throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_NoContext");
        var safePrompt = RedactAbsolutePaths(SanitizeGoal(prompt) ?? string.Empty);
        var context = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.DisplayName);
            if (!name.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_InvalidContextFile");
            context.Append("--- BEGIN FILE: ").Append(name).AppendLine(" ---")
                .AppendLine(RedactAbsolutePaths(file.Content))
                .Append("--- END FILE: ").Append(name).AppendLine(" ---");
        }
        if (context.Length + safePrompt.Length > MaximumContextCharacters)
            throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_ContextTooLarge");
        var safeHistory = history.TakeLast(20)
            .Select(turn => new AiConversationTurn(turn.Role is "assistant" ? "assistant" : "user",
                Limit(RedactAbsolutePaths(turn.Content), 16_000)))
            .ToArray();
        return new(mode == AiPermissionMode.GenerateChanges ? AiOperation.Refactor : AiOperation.Explain,
            context.ToString(), safePrompt, "Unknown", mode == AiPermissionMode.GenerateChanges,
            safeHistory, mode);
    }

    private static string? SanitizeGoal(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 2000)];

    private static string Limit(string value, int length) => value[..Math.Min(value.Length, length)];

    private static string RedactAbsolutePaths(string value) => AbsolutePathRegex().Replace(value, "[LOCAL_PATH]");

    [GeneratedRegex(@"(?i)(?:""(?:[A-Z]:\\|\\\\)[^""\r\n]+""|'(?:[A-Z]:\\|\\\\)[^'\r\n]+'|(?:[A-Z]:\\|\\\\)[^\s,;)]+)")]
    private static partial Regex AbsolutePathRegex();
}
