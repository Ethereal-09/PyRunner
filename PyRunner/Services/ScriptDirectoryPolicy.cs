namespace PyRunner.Services;

/// <summary>
/// Defines the boundary of a configured script directory. Normal user folders are scanned
/// recursively, while dependency/build/cache folders and directory links are never traversed.
/// </summary>
internal static class ScriptDirectoryPolicy
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".idea",
        ".svn",
        ".vscode",
        ".mypy_cache",
        ".pytest_cache",
        ".tox",
        ".venv",
        "__pycache__",
        "build",
        "dist",
        "env",
        "node_modules",
        "site-packages",
        "venv",
    };

    public static IReadOnlyList<string> ScanPythonFiles(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return Array.Empty<string>();

        string root;
        try { root = Path.GetFullPath(rootPath.Trim()); }
        catch { return Array.Empty<string>(); }

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current, "*.py", SearchOption.TopDirectoryOnly))
                {
                    if (HasSkippedAttributes(file)) continue;
                    files.Add(Path.GetFullPath(file));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    if (ShouldSkipDirectory(directory)) continue;
                    pending.Push(directory);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    public static bool ShouldSkipDirectory(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return ExcludedDirectoryNames.Contains(name) || HasSkippedAttributes(path);
    }

    private static bool HasSkippedAttributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0;
        }
        catch
        {
            return true;
        }
    }
}
