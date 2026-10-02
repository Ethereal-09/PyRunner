using Microsoft.VisualBasic.FileIO;

namespace PyRunner.Services;

/// <summary>仅将已配置脚本目录内的普通 Python 文件移入回收站。</summary>
public static class ScriptFileDeletionService
{
    public static void MoveToRecycleBin(string filePath, IEnumerable<string> configuredRoots)
    {
        var path = Path.GetFullPath(filePath);
        if (!Path.GetExtension(path).Equals(".py", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("DeleteFile_InvalidTarget");
        var root = configuredRoots.Select(Path.GetFullPath)
            .Where(candidate => path.StartsWith(Path.EndsInDirectorySeparator(candidate) ? candidate : candidate + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Length).FirstOrDefault();
        if (root is null) throw new ValidationException("DeleteFile_InvalidTarget");
        if (!File.Exists(path)) throw new FileNotFoundException(null, path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new ValidationException("DeleteFile_InvalidTarget");
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new ValidationException("DeleteFile_InvalidTarget");
            if (parent.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) break;
        }
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
    }
}
