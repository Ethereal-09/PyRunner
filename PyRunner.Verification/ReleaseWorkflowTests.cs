using System.Diagnostics;

internal static class ReleaseWorkflowTests
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else { failures++; Console.Error.WriteLine($"FAIL: {name}"); }
        }

        var root = FindSolutionRoot();
        var ci = await File.ReadAllTextAsync(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var release = await File.ReadAllTextAsync(Path.Combine(root, ".github", "workflows", "release.yml"));

        Verify(
            ci.Contains("permissions:\n  contents: read", StringComparison.Ordinal) &&
            !ci.Contains("contents: write", StringComparison.Ordinal) &&
            !ci.Contains("deploy-pages", StringComparison.Ordinal) &&
            !ci.Contains("gh release", StringComparison.Ordinal),
            "ordinary CI remains read-only and contains no release or Pages publishing");

        var publishJob = Slice(release, "  publish-release:\n", "  deploy-pages:\n");
        var pagesJob = Slice(release, "  deploy-pages:\n", null);
        Verify(
            release.Contains("workflow_dispatch:", StringComparison.Ordinal) &&
            !release.Contains("pull_request:", StringComparison.Ordinal) &&
            !release.Contains("release:\n    types:", StringComparison.Ordinal) &&
            publishJob.Contains("permissions:\n      contents: write", StringComparison.Ordinal) &&
            pagesJob.Contains("permissions:\n      contents: read\n      pages: write\n      id-token: write", StringComparison.Ordinal) &&
            !pagesJob.Contains("contents: write", StringComparison.Ordinal),
            "release and Pages jobs use separate least-privilege permissions");

        Verify(
            release.Contains("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", StringComparison.Ordinal) &&
            release.Contains("actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68", StringComparison.Ordinal) &&
            release.Contains("actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02", StringComparison.Ordinal) &&
            release.Contains("actions/download-artifact@634f93cb2916e3fdff6788551b99b062d0335ce0", StringComparison.Ordinal) &&
            release.Contains("actions/upload-pages-artifact@7b1f4a764d45c48632c6b24a0339c27f5614fb0b", StringComparison.Ordinal) &&
            release.Contains("actions/deploy-pages@d6db90164ac5ed86f2b6aed7e0febac5b3c0c03e", StringComparison.Ordinal) &&
            !release.Contains("uses: actions/checkout@v", StringComparison.Ordinal) &&
            !release.Contains("uses: actions/setup-dotnet@v", StringComparison.Ordinal),
            "release workflow pins every GitHub Action to a commit SHA");

        var create = release.IndexOf("gh release create", StringComparison.Ordinal);
        var upload = release.IndexOf("gh release upload", StringComparison.Ordinal);
        var publish = release.IndexOf("gh release edit", StringComparison.Ordinal);
        var download = release.IndexOf("gh release download", StringComparison.Ordinal);
        var generate = release.IndexOf("New-UpdateManifest.ps1", StringComparison.Ordinal);
        var stage = release.IndexOf("Stage verified Pages content", StringComparison.Ordinal);
        var deploy = release.IndexOf("uses: actions/deploy-pages@", StringComparison.Ordinal);
        Verify(
            create >= 0 && create < upload && upload < publish && publish < download &&
            download < generate && generate < stage && stage < deploy,
            "release publication, remote verification, manifest generation and Pages deployment are ordered");

        var simulation = pagesJob.IndexOf("Simulate Pages deployment failure", StringComparison.Ordinal);
        var pagesUpload = pagesJob.IndexOf("uses: actions/upload-pages-artifact@", StringComparison.Ordinal);
        Verify(
            simulation >= 0 && simulation < pagesUpload &&
            pagesJob.Contains("needs: publish-release", StringComparison.Ordinal),
            "failure drill stops before the atomic Pages deployment");

        var temp = Path.Combine(Path.GetTempPath(), "PyRunner.ReleaseWorkflow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var output = Path.Combine(temp, "notes.txt");
            var exitCode = await RunPowerShellAsync(
                Path.Combine(root, "tools", "release", "Get-ChangelogReleaseNotes.ps1"),
                "-Version", "1.1.0",
                "-ChangelogPath", Path.Combine(root, "CHANGELOG.md"),
                "-OutputPath", output);
            var notes = exitCode == 0 ? await File.ReadAllTextAsync(output) : string.Empty;
            Verify(exitCode == 0 && notes.Contains("新增应用内定时任务", StringComparison.Ordinal) &&
                   !notes.Contains("## [1.0.1]", StringComparison.Ordinal),
                "release notes extractor selects exactly the requested dated changelog section");

            var missingExitCode = await RunPowerShellAsync(
                Path.Combine(root, "tools", "release", "Get-ChangelogReleaseNotes.ps1"),
                "-Version", "9.9.9",
                "-ChangelogPath", Path.Combine(root, "CHANGELOG.md"),
                "-OutputPath", Path.Combine(temp, "missing.txt"));
            Verify(missingExitCode != 0, "release notes extractor rejects a missing stable version");
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }

        return failures;
    }

    private static string Slice(string source, string startMarker, string? endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        if (endMarker is null) return source[start..];
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static async Task<int> RunPowerShellAsync(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start PowerShell.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return process.ExitCode;
    }

    private static string FindSolutionRoot()
    {
        foreach (var seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(seed);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PyRunner.sln"))) return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate PyRunner.sln");
    }
}
