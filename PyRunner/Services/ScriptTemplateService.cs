using System.Text;
using PyRunner.Editor;

namespace PyRunner.Services;

public sealed class ScriptTemplateService : IScriptTemplateService
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly IScriptPathService _paths;

    private static readonly IReadOnlyList<ScriptTemplate> Templates =
    [
        new("basic", "Template_BasicName", "Template_BasicDescription", "1",
            "\"\"\"PyRunner script.\"\"\"\r\n\r\ndef main() -> int:\r\n    try:\r\n        print(\"Hello from PyRunner\")\r\n        return 0\r\n    except Exception as error:\r\n        print(f\"Error: {error}\")\r\n        return 1\r\n\r\n\r\nif __name__ == \"__main__\":\r\n    raise SystemExit(main())\r\n"),
        new("cli", "Template_CliName", "Template_CliDescription", "1",
            "\"\"\"PyRunner command-line script.\"\"\"\r\n\r\nimport argparse\r\n\r\n\r\ndef parse_args() -> argparse.Namespace:\r\n    parser = argparse.ArgumentParser(description=\"Describe this command\")\r\n    parser.add_argument(\"name\", nargs=\"?\", default=\"world\")\r\n    return parser.parse_args()\r\n\r\n\r\ndef main() -> int:\r\n    args = parse_args()\r\n    print(f\"Hello, {args.name}!\")\r\n    return 0\r\n\r\n\r\nif __name__ == \"__main__\":\r\n    raise SystemExit(main())\r\n"),
    ];

    public ScriptTemplateService(IScriptPathService paths) => _paths = paths;

    public IReadOnlyList<ScriptTemplate> GetTemplates() => Templates;

    public ScriptTemplateCreateResult Create(string templateId, string targetDirectory, string fileName)
    {
        var template = Templates.SingleOrDefault(item => item.Id == templateId);
        if (template is null) return new(false, null, "Template_Error_UnknownTemplate");
        string directory;
        try { directory = Path.GetFullPath(targetDirectory); }
        catch { return new(false, null, "Template_Error_Directory"); }
        if (!Directory.Exists(directory) || !_paths.GetAll().Any(path => path.Enabled &&
                string.Equals(Path.GetFullPath(path.Path), directory, StringComparison.OrdinalIgnoreCase)))
            return new(false, null, "Template_Error_Directory");
        try { RejectReparsePoints(directory); }
        catch { return new(false, null, "Template_Error_Directory"); }

        var validatedName = ValidateName(fileName);
        if (validatedName is null) return new(false, null, "Template_Error_Name");
        var target = Path.GetFullPath(Path.Combine(directory, validatedName));
        if (!string.Equals(Path.GetDirectoryName(target), directory, StringComparison.OrdinalIgnoreCase))
            return new(false, null, "Template_Error_Directory");

        try
        {
            using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.WriteThrough);
            var bytes = new UTF8Encoding(false).GetBytes(template.Content);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return new(true, target, string.Empty);
        }
        catch (IOException) when (File.Exists(target))
        {
            return new(false, null, "Template_Error_AlreadyExists");
        }
        catch
        {
            return new(false, null, "Template_Error_CreateFailed");
        }
    }

    internal static string? ValidateName(string fileName)
    {
        var value = fileName?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.EndsWith('.') || value.EndsWith(' ') ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            return null;
        if (!value.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) value += ".py";
        var stem = Path.GetFileNameWithoutExtension(value);
        return ReservedNames.Contains(stem) || stem.Length == 0 ? null : value;
    }

    private static void RejectReparsePoints(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException();
        }
    }
}
