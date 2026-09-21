using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PyRunner.Models;
using PyRunner.Terminal;

namespace PyRunner.Services;

public sealed class PythonProcessRunner : IPythonProcessRunner
{
    public async Task<PythonProcessResult> RunAsync(
        PythonProcessRequest request,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Arguments.Count == 0) throw new ArgumentException("Python arguments are required.", nameof(request));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(request.InterpreterPath),
            WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        IntPtr job = IntPtr.Zero;
        try
        {
            if (!process.Start())
                return new PythonProcessResult(null, false, false, string.Empty, "Dependency_Error_ProcessStart");

            job = CreateKillOnCloseJob();
            if (!ConPtyNative.AssignProcessToJobObject(job, process.Handle))
            {
                TryKill(process);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
            }

            var stdout = new BoundedOutput();
            var stderr = new BoundedOutput();
            var stdoutTask = PumpAsync(process.StandardOutput, stdout, output);
            var stderrTask = PumpAsync(process.StandardError, stderr, output);
            using var timeout = new CancellationTokenSource(request.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                CloseJob(ref job);
                TryKill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { }
                await Task.WhenAll(stdoutTask, stderrTask);
                return new PythonProcessResult(
                    null,
                    cancellationToken.IsCancellationRequested,
                    !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested,
                    stdout.Text,
                    stderr.Text);
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            return new PythonProcessResult(process.ExitCode, false, false, stdout.Text, stderr.Text);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new PythonProcessResult(null, false, false, string.Empty,
                DependencyTextSanitizer.Sanitize(ex.Message, 2048));
        }
        finally
        {
            CloseJob(ref job);
        }
    }

    private static async Task PumpAsync(StreamReader reader, BoundedOutput destination, IProgress<string>? progress)
    {
        var buffer = new char[2048];
        var line = new StringBuilder();
        var lineOmitted = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value is '\r' or '\n')
                {
                    if (value == '\r' && index + 1 < read && buffer[index + 1] == '\n') index++;
                    EmitCompletedLine(line, lineOmitted, destination, progress, appendNewLine: true);
                    line.Clear();
                    lineOmitted = false;
                    continue;
                }

                if (!lineOmitted)
                {
                    if (line.Length < 8192) line.Append(value);
                    else
                    {
                        line.Clear();
                        lineOmitted = true;
                    }
                }
            }
        }
        if (line.Length != 0 || lineOmitted)
            EmitCompletedLine(line, lineOmitted, destination, progress, appendNewLine: false);
    }

    private static void EmitCompletedLine(
        StringBuilder line,
        bool omitted,
        BoundedOutput destination,
        IProgress<string>? progress,
        bool appendNewLine)
    {
        // Waiting for a complete, bounded line prevents credentials split across
        // StreamReader chunks from bypassing URL/secret redaction.
        var safe = omitted
            ? "[dependency output line omitted]"
            : DependencyTextSanitizer.Sanitize(line.ToString(), 8192);
        if (appendNewLine) safe += Environment.NewLine;
        destination.Append(safe);
        if (safe.Length != 0) progress?.Report(safe);
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = ConPtyNative.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        var information = new ConPtyNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        information.BasicLimitInformation.LimitFlags = ConPtyNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!ConPtyNative.SetInformationJobObject(
                job,
                ConPtyNative.JobObjectExtendedLimitInformation,
                ref information,
                Marshal.SizeOf<ConPtyNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            ConPtyNative.CloseHandle(job);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");
        }
        return job;
    }

    private static void CloseJob(ref IntPtr job)
    {
        var current = Interlocked.Exchange(ref job, IntPtr.Zero);
        if (current != IntPtr.Zero) ConPtyNative.CloseHandle(current);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    private sealed class BoundedOutput
    {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();
        public string Text { get { lock (_gate) return _builder.ToString(); } }

        public void Append(string value)
        {
            lock (_gate)
            {
                _builder.Append(value);
                if (_builder.Length > DependencyTextSanitizer.MaximumDiagnosticLength)
                    _builder.Remove(0, _builder.Length - DependencyTextSanitizer.MaximumDiagnosticLength);
            }
        }
    }
}
