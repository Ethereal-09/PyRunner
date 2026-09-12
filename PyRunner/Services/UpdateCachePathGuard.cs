using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PyRunner.Services;

/// <summary>锁住更新根目录和单次目录，拒绝联接/符号链接及路径替换。</summary>
public sealed class UpdateCachePathGuard : IDisposable
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private readonly SafeFileHandle _rootHandle;
    private readonly SafeFileHandle _attemptHandle;

    private UpdateCachePathGuard(SafeFileHandle rootHandle, SafeFileHandle attemptHandle)
    {
        _rootHandle = rootHandle;
        _attemptHandle = attemptHandle;
    }

    public static bool TryOpen(string cacheRoot, string attemptDirectory, out UpdateCachePathGuard? guard)
    {
        guard = null;
        SafeFileHandle? rootHandle = null;
        SafeFileHandle? attemptHandle = null;
        try
        {
            var root = Normalize(cacheRoot);
            var attempt = Normalize(attemptDirectory);
            if (!string.Equals(Path.GetDirectoryName(attempt), root, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(root) || !Directory.Exists(attempt) ||
                IsReparsePoint(root) || IsReparsePoint(attempt))
                return false;

            rootHandle = OpenDirectory(root);
            attemptHandle = OpenDirectory(attempt);
            if (rootHandle.IsInvalid || attemptHandle.IsInvalid ||
                IsReparsePoint(root) || IsReparsePoint(attempt) ||
                !string.Equals(ReadFinalPath(rootHandle), root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ReadFinalPath(attemptHandle), attempt, StringComparison.OrdinalIgnoreCase))
                return false;

            guard = new UpdateCachePathGuard(rootHandle, attemptHandle);
            rootHandle = null;
            attemptHandle = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            rootHandle?.Dispose();
            attemptHandle?.Dispose();
        }
    }

    public static bool IsSafeFilePath(string cacheRoot, string filePath, string expectedFileName)
    {
        try
        {
            var root = Normalize(cacheRoot);
            var file = Normalize(filePath);
            var attempt = Path.GetDirectoryName(file);
            return attempt is not null &&
                   string.Equals(Path.GetDirectoryName(attempt), root, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetFileName(file), expectedFileName, StringComparison.Ordinal) &&
                   !IsReparsePoint(root) && !IsReparsePoint(attempt) &&
                   (!File.Exists(file) || !IsReparsePoint(file));
        }
        catch
        {
            return false;
        }
    }

    private static SafeFileHandle OpenDirectory(string path) => CreateFile(
        path,
        0,
        FileShareRead | FileShareWrite,
        IntPtr.Zero,
        OpenExisting,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint,
        IntPtr.Zero);

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Unable to resolve update directory.");
        return Normalize(RemoveDevicePrefix(buffer.ToString()));
    }

    private static string RemoveDevicePrefix(string path) => path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
        ? "\\\\" + path[8..]
        : path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    public void Dispose()
    {
        _attemptHandle.Dispose();
        _rootHandle.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
