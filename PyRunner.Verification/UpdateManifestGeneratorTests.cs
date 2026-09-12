using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class UpdateManifestGeneratorTests
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else { failures++; Console.Error.WriteLine($"FAIL: {name}"); }
        }

        var root = Path.Combine(Path.GetTempPath(), "PyRunner.ManifestGenerator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(FindSolutionRoot(), "tools", "release", "New-UpdateManifest.ps1");
            var localDirectory = Path.Combine(root, "local");
            var remoteDirectory = Path.Combine(root, "remote");
            Directory.CreateDirectory(localDirectory);
            Directory.CreateDirectory(remoteDirectory);
            const string fileName = "PyRunner-Setup-1.2.3-x64.exe";
            var local = Path.Combine(localDirectory, fileName);
            var remote = Path.Combine(remoteDirectory, fileName);
            var checksum = local + ".sha256";
            var remoteChecksum = remote + ".sha256";
            var content = "fake installer for manifest verification"u8.ToArray();
            await File.WriteAllBytesAsync(local, content);
            await File.WriteAllBytesAsync(remote, content);
            var checksumText = $"{Convert.ToHexString(SHA256.HashData(content))}  {fileName}\n";
            await File.WriteAllTextAsync(checksum, checksumText);
            await File.WriteAllTextAsync(remoteChecksum, checksumText);
            var notes = Path.Combine(root, "notes.txt");
            await File.WriteAllTextAsync(notes, "Plain release notes\u0001");
            var output = Path.Combine(root, "site", "update.json");

            var success = await RunGeneratorAsync(
                script, "1.2.3", notes, local, remote, checksum, remoteChecksum, output);
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(output));
            var asset = manifest.RootElement.GetProperty("assets")[0];
            Verify(success == 0 && manifest.RootElement.GetProperty("schemaVersion").GetInt32() == 1 &&
                   manifest.RootElement.GetProperty("tag").GetString() == "v1.2.3" &&
                   asset.GetProperty("fileName").GetString() == fileName &&
                   asset.GetProperty("size").GetInt64() == content.Length &&
                   asset.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(content)) &&
                   !manifest.RootElement.GetProperty("releaseNotes").GetString()!.Contains('\u0001'),
                "manifest generator emits schema v1 after local and remote asset verification");

            var previous = await File.ReadAllBytesAsync(output);
            await File.AppendAllTextAsync(remote, "changed");
            var mismatch = await RunGeneratorAsync(
                script, "1.2.3", notes, local, remote, checksum, remoteChecksum, output, output);
            Verify(mismatch != 0 && previous.SequenceEqual(await File.ReadAllBytesAsync(output)),
                "remote asset mismatch leaves the previous Pages manifest untouched");

            await File.WriteAllBytesAsync(remote, content);
            const string newer = "{\"schemaVersion\":1,\"version\":\"9.0.0\"}\n";
            await File.WriteAllTextAsync(output, newer);
            var rollback = await RunGeneratorAsync(
                script, "1.2.3", notes, local, remote, checksum, remoteChecksum, output, output);
            Verify(rollback != 0 && await File.ReadAllTextAsync(output) == newer,
                "older stable version cannot overwrite a newer deployed manifest");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return failures;
    }

    private static async Task<int> RunGeneratorAsync(
        string script, string version, string notes, string local, string remote,
        string checksum, string remoteChecksum, string output,
        string? existing = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script,
            "-Version", version, "-PublishedAt", "2026-09-01T08:00:00Z",
            "-ReleaseNotesPath", notes, "-InstallerPath", local,
            "-VerifiedRemoteInstallerPath", remote, "-ChecksumPath", checksum,
            "-VerifiedRemoteChecksumPath", remoteChecksum, "-OutputPath", output
        }) start.ArgumentList.Add(argument);
        if (existing is not null)
        {
            start.ArgumentList.Add("-ExistingManifestPath");
            start.ArgumentList.Add(existing);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start PowerShell.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return process.ExitCode;
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PyRunner.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate PyRunner.sln");
    }
}
