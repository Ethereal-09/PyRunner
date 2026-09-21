using System.Net;
using System.Text;
using PyRunner.Models;
using PyRunner.Services.AI;
using PyRunner.Services;
using PyRunner.ViewModels;
using PyRunner.Data;

internal static class AiAssistantFeatureTests
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Check(bool condition, string name)
        {
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: AI {name}");
            if (!condition) failures++;
        }

        Check(AiEndpointPolicy.Normalize("https://example.test/v1", AiProviderKind.OpenAiCompatible).AbsoluteUri ==
              "https://example.test/v1/", "normalizes compatible HTTPS endpoint");
        Check(Throws(() => AiEndpointPolicy.Normalize("http://example.test/v1", AiProviderKind.OpenAiCompatible)),
            "rejects non-HTTPS remote endpoint");
        Check(Throws(() => AiEndpointPolicy.Normalize("https://user:pass@example.test/v1", AiProviderKind.OpenAiCompatible)),
            "rejects URL user info");
        Check(Throws(() => AiEndpointPolicy.Normalize("file:///tmp/api", AiProviderKind.OpenAiCompatible)),
            "rejects dangerous protocol");

        var scanner = new SecretScanService();
        const string secretText = "name = 'safe'\napi_key = 'sk-test-secret-value'\n-----BEGIN PRIVATE KEY-----";
        var findings = scanner.Scan(secretText);
        var masked = scanner.CreateMaskedPreview(secretText, findings);
        Check(findings.Count >= 2 && !masked.Contains("sk-test-secret-value", StringComparison.Ordinal),
            "masks credentials without returning matched content");

        var contextBuilder = new AiContextBuilder(scanner);
        var preview = contextBuilder.BuildPreview(
            new AiConfiguration(AiProviderKind.OpenAI, new Uri("https://api.openai.com/v1/"), "test-model"),
            new AiEditorContext("print('ok')", "test.py", true, 0, 11, "HASH"));
        Check(preview.Scope == "selection" && preview.CharacterCount == 11 && preview.Host == "api.openai.com",
            "previews only the confirmed editor scope");
        var privacyContext = new AiEditorContext("open(r'C:\\Users\\Alice\\secret.txt')", "test.py", true, 0, 35, "HASH");
        var privateRequest = contextBuilder.BuildRequest(AiOperation.Diagnose, privacyContext,
            "password = top-secret-value");
        var privatePreview = contextBuilder.BuildPreview(
            new AiConfiguration(AiProviderKind.OpenAI, new Uri("https://api.openai.com/v1/"), "test-model"),
            privacyContext, "password = top-secret-value");
        Check(!privateRequest.Context.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase) &&
              privatePreview.Findings.Count > 0,
            "redacts absolute paths and scans optional error text before sending");
        Check(Throws(() => contextBuilder.BuildPreview(
            new AiConfiguration(AiProviderKind.OpenAI, new Uri("https://api.openai.com/v1/"), "test-model"),
            new AiEditorContext(new string('x', AiContextBuilder.MaximumContextCharacters + 1), "x.py", false, 0, 0, "H"))),
            "rejects oversized context");
        var conversationRequest = contextBuilder.BuildConversationRequest(AiPermissionMode.ReadOnlyAnalysis,
            [new AiContextFile("C:\\private\\alpha.py", "alpha.py", "print(1)", 8, "AA", "utf-8", false, "\n", true)],
            [new AiConversationTurn("user", "earlier question"), new AiConversationTurn("assistant", "earlier answer")],
            "follow up");
        Check(conversationRequest.Context.Contains("BEGIN FILE: alpha.py", StringComparison.Ordinal) &&
              !conversationRequest.Context.Contains("C:\\private", StringComparison.OrdinalIgnoreCase) &&
              conversationRequest.History?.Count == 2,
            "builds multi-turn context with display names instead of private paths");

        var events = string.Join("\n\n", new[]
        {
            "event: response.created\ndata: {\"type\":\"response.created\"}",
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"hel\"}",
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"lo\"}",
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}}",
        }) + "\n\n";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(Encoding.UTF8.GetBytes(events), 3)),
        });
        using (var client = new OpenAiResponsesClient(new HttpClient(handler)))
        {
            var result = await client.StreamAsync(
                new AiConfiguration(AiProviderKind.OpenAI, new Uri("https://api.openai.com/v1/"), "test-model"),
                "test-key", new AiProviderRequest(AiOperation.Explain, "safe", null, "3.12", false), null, CancellationToken.None);
            Check(result.Text == "hello" && result.InputTokens == 3 && result.OutputTokens == 2,
                "parses SSE across arbitrary network chunks");
            Check(handler.LastRequest?.RequestUri?.AbsoluteUri == "https://api.openai.com/v1/responses" &&
                  handler.LastRequest.Headers.Authorization?.Scheme == "Bearer",
                "uses Responses endpoint and authorization header");
        }
        var conversationHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(events, Encoding.UTF8, "text/event-stream"),
        });
        using (var client = new OpenAiResponsesClient(new HttpClient(conversationHandler)))
        {
            await client.StreamAsync(
                new AiConfiguration(AiProviderKind.OpenAI, new Uri("https://api.openai.com/v1/"), "test-model"),
                "test-key", conversationRequest, null, CancellationToken.None);
            Check(conversationHandler.LastBody?.Contains("\"store\":false", StringComparison.Ordinal) == true &&
                  conversationHandler.LastBody.Contains("earlier question", StringComparison.Ordinal) &&
                  !conversationHandler.LastBody.Contains("C:\\\\private", StringComparison.OrdinalIgnoreCase),
                "sends stateless conversation history with provider storage disabled");
        }

        await CheckError(HttpStatusCode.Unauthorized, AiErrorKind.Unauthorized, "classifies 401", Check);
        await CheckError(HttpStatusCode.Forbidden, AiErrorKind.Unauthorized, "classifies 403", Check);
        await CheckError(HttpStatusCode.TooManyRequests, AiErrorKind.RateLimited, "classifies 429 without retry", Check);
        await CheckError(HttpStatusCode.InternalServerError, AiErrorKind.Server, "classifies 5xx", Check);
        await CheckError(HttpStatusCode.Redirect, AiErrorKind.InvalidConfiguration, "blocks redirects", Check);

        var cancellationHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream()),
        });
        using (var client = new OpenAiResponsesClient(new HttpClient(cancellationHandler)))
        using (var cts = new CancellationTokenSource(40))
        {
            try
            {
                await client.StreamAsync(new AiConfiguration(AiProviderKind.OpenAI,
                    new Uri("https://api.openai.com/v1/"), "test-model"),
                    "test-key", new AiProviderRequest(AiOperation.Explain, "safe", null, "3", false), null, cts.Token);
                Check(false, "cancels streaming request");
            }
            catch (AiProviderException ex) { Check(ex.Kind == AiErrorKind.Cancelled, "cancels streaming request"); }
        }

        var review = new AiChangeReviewService();
        var candidate = review.ParseCandidate("{\"summary\":\"change\",\"code\":\"print(2)\",\"notes\":\"review\"}");
        Check(candidate?.Code == "print(2)" && review.ParseCandidate("<script>alert(1)</script>") is null,
            "accepts strict candidate JSON and rejects hostile HTML");
        var multiCandidate = review.ParseCandidate("{\"summary\":\"two\",\"notes\":\"review\",\"changes\":[{\"file\":\"a.py\",\"content\":\"a=2\",\"summary\":\"a\"},{\"file\":\"b.py\",\"content\":\"b=2\",\"summary\":\"b\"}]}");
        Check(multiCandidate?.EffectiveFiles.Count == 2 &&
              review.ParseCandidate("{\"summary\":\"bad\",\"changes\":[{\"file\":\"../a.py\",\"content\":\"x\"}]}") is null,
            "accepts confirmed multi-file candidates and rejects traversal names");
        var original = new AiEditorContext("print(1)", "x.py", true, 0, 8, "AAA");
        Check(review.IsOriginalCurrent(original, original) &&
              !review.IsOriginalCurrent(original, original with { BufferSha256 = "BBB" }),
            "blocks apply after source fingerprint changes");
        var diff = review.CreateLineDiff("print(1)\n", "print(2)\n");
        Check(diff.Contains("- print(1)", StringComparison.Ordinal) && diff.Contains("+ print(2)", StringComparison.Ordinal),
            "builds the review diff locally rather than trusting provider line numbers");

        var fakeStore = new FakeCredentialStore();
        var credentialKey = new AiCredentialKey(AiProviderKind.OpenAI, "api.openai.com");
        fakeStore.Save(credentialKey, "test-secret");
        Check(fakeStore.Retrieve(credentialKey) == "test-secret", "credential abstraction supports save and retrieve");
        fakeStore.Delete(credentialKey);
        Check(fakeStore.Retrieve(credentialKey) is null, "credential abstraction supports deletion");
        failures += await RunPersistenceAndPatchTestsAsync();
        failures += await RunViewModelSafetyTestsAsync();
        return failures;
    }

    private static async Task<int> RunPersistenceAndPatchTestsAsync()
    {
        var failures = 0;
        void Check(bool condition, string name)
        {
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: AI {name}");
            if (!condition) failures++;
        }
        var root = Path.Combine(Path.GetTempPath(), $"pyrunner-ai-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databaseRoot = Path.Combine(root, "db");
            var factory = new SqliteConnectionFactory(databaseRoot);
            new SchemaMigrator(factory).Migrate();
            var store = new AiConversationStore(factory);
            var id = Guid.NewGuid().ToString();
            store.Save(new AiConversationSnapshot(id, "safe title", AiPermissionMode.ReadOnlyAnalysis,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                [new AiStoredMessage(Guid.NewGuid().ToString(), AiMessageRole.System, AiMessageKind.AnalysisStatus,
                     "HIDDEN_SYSTEM_PROMPT", DateTimeOffset.UtcNow),
                 new AiStoredMessage(Guid.NewGuid().ToString(), AiMessageRole.User, AiMessageKind.Text,
                     "explicitly sent", DateTimeOffset.UtcNow)],
                [Path.Combine(root, "context.py")]));
            var restored = store.Load(id);
            Check(restored?.Messages.Count == 1 && restored.Messages[0].Content == "explicitly sent" &&
                  store.List().Single().MessageCount == 1,
                "history persists explicit turns but excludes system prompts and credentials");
            store.Delete(id);
            Check(store.Load(id) is null, "history deletion removes the selected local conversation");

            var patchRoot = Path.Combine(root, "patch"); Directory.CreateDirectory(patchRoot);
            var backupRoot = Path.Combine(root, "backups");
            var target = Path.Combine(patchRoot, "safe.py");
            await File.WriteAllTextAsync(target, "print(1)\n", new UTF8Encoding(false));
            var originalBytes = await File.ReadAllBytesAsync(target);
            var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalBytes));
            var service = new AiPatchApplicationService(backupRoot);
            var change = new AiPendingFileChange(target, "safe.py", originalHash, "print(1)\n", "print(2)\n",
                "utf-8", false, "\n", "change");
            var applied = await service.ApplyAsync([change]);
            Check(applied.Succeeded && await File.ReadAllTextAsync(target) == "print(2)\n" &&
                  applied.BackupDirectory is not null && Directory.GetFiles(applied.BackupDirectory).Length == 1 &&
                  await File.ReadAllTextAsync(Directory.GetFiles(applied.BackupDirectory)[0]) == "print(1)\n",
                "confirmed patch creates a recoverable backup and atomically replaces the file");

            await File.WriteAllTextAsync(target, "external change\n", new UTF8Encoding(false));
            var rejected = await service.ApplyAsync([change]);
            Check(!rejected.Succeeded && rejected.ErrorKey == "AI_Error_SourceChanged" &&
                  await File.ReadAllTextAsync(target) == "external change\n",
                "external modification blocks patch application without overwriting the file");

            var first = Path.Combine(patchRoot, "first.py"); var second = Path.Combine(patchRoot, "second.py");
            await File.WriteAllTextAsync(first, "first=1\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(second, "second=1\n", new UTF8Encoding(false));
            static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var rollback = await service.ApplyAsync([
                new(first, "first.py", Hash(first), "first=1\n", "first=2\n", "utf-8", false, "\n", "first"),
                new(second, "second.py", Hash(second), "second=1\n", "second=2\n", "not-an-encoding", false, "\n", "second")]);
            Check(!rollback.Succeeded && await File.ReadAllTextAsync(first) == "first=1\n" &&
                  await File.ReadAllTextAsync(second) == "second=1\n",
                "multi-file patch failure rolls back files already replaced");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        return failures;
    }

    private static async Task<int> RunViewModelSafetyTestsAsync()
    {
        var failures = 0;
        void Check(bool condition, string name)
        {
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: AI {name}");
            if (!condition) failures++;
        }
        var settings = new FakeSettingsService();
        var credentials = new FakeCredentialStore();
        credentials.Save(new AiCredentialKey(AiProviderKind.OpenAI, "api.openai.com"), "test-key");
        var editor = new FakeEditorBridge();
        var localization = new FakeLocalization();
        var scanner = new SecretScanService();

        var rejectingInteraction = new FakeInteraction { ConfirmSend = false };
        var provider = new FakeProvider();
        using (var vm = CreateVm(settings, credentials, provider, editor, rejectingInteraction, localization, scanner))
        {
            await vm.SynchronizeCurrentFileAsync();
            vm.PromptText = "explain";
            await vm.SendCommand.ExecuteAsync(null);
            Check(provider.CallCount == 0, "user refusal prevents any paid request");
        }

        var waitingProvider = new FakeProvider { WaitForRelease = true };
        var acceptingInteraction = new FakeInteraction { ConfirmSend = true };
        using (var vm = CreateVm(settings, credentials, waitingProvider, editor, acceptingInteraction, localization, scanner))
        {
            await vm.SynchronizeCurrentFileAsync();
            vm.PromptText = "explain";
            var first = vm.SendCommand.ExecuteAsync(null);
            await waitingProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = vm.SendCommand.ExecuteAsync(null);
            await Task.Delay(30);
            Check(waitingProvider.CallCount == 1, "duplicate clicks do not start concurrent requests");
            waitingProvider.Release.TrySetResult();
            await Task.WhenAll(first, second);
        }

        var structuredProvider = new FakeProvider
        {
            ResultText = "{\"summary\":\"change\",\"notes\":\"review\",\"changes\":[{\"file\":\"test.py\",\"content\":\"print(2)\",\"summary\":\"update\"}]}",
        };
        var rejectApply = new FakeInteraction { ConfirmSend = true, ConfirmApply = false };
        var patcher = new FakePatchService();
        using (var vm = CreateVm(settings, credentials, structuredProvider, editor, rejectApply, localization, scanner, patcher))
        {
            await vm.SynchronizeCurrentFileAsync();
            vm.SelectedModeIndex = 1;
            vm.PromptText = "refactor";
            await vm.SendCommand.ExecuteAsync(null);
            await vm.ApplyProposalCommand.ExecuteAsync(null);
            Check(patcher.CallCount == 0, "rejecting change review never writes a patch");
        }

        var conversationProvider = new FakeProvider();
        using (var vm = CreateVm(settings, credentials, conversationProvider, editor, acceptingInteraction, localization, scanner))
        {
            await vm.SynchronizeCurrentFileAsync();
            vm.PromptText = "first"; await vm.SendCommand.ExecuteAsync(null);
            vm.PromptText = "follow up"; await vm.SendCommand.ExecuteAsync(null);
            Check(conversationProvider.Requests.Count == 2 && conversationProvider.Requests[1].History?.Count == 2,
                "continuous follow-up includes only locally stored explicit turns");
        }
        return failures;
    }

    private static AiAssistantViewModel CreateVm(FakeSettingsService settings, IAiCredentialStore credentials,
        IAiProviderClient provider, IAiEditorBridge editor, IAiInteractionService interaction,
        ILocalizationService localization, ISecretScanService scanner, IAiPatchApplicationService? patcher = null)
    {
        var vm = new AiAssistantViewModel(new AiConfigurationService(settings), credentials, provider,
            new AiContextBuilder(scanner), scanner, new AiChangeReviewService(), interaction,
            new FakeConversationStore(), new FakeFileContextService(), patcher ?? new FakePatchService(), localization);
        vm.AttachEditor(editor);
        return vm;
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; } catch (AiProviderException) { return true; }
    }

    private static async Task CheckError(HttpStatusCode status, AiErrorKind expected, string name, Action<bool, string> check)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status));
        using var client = new OpenAiResponsesClient(new HttpClient(handler));
        try
        {
            await client.StreamAsync(new AiConfiguration(AiProviderKind.OpenAI,
                new Uri("https://api.openai.com/v1/"), "model"),
                "test-key", new AiProviderRequest(AiOperation.Explain, "safe", null, "3", false), null, CancellationToken.None);
            check(false, name);
        }
        catch (AiProviderException ex) { check(ex.Kind == expected && handler.CallCount == 1, name); }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++; LastRequest = request;
            LastBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(response(request));
        }
    }

    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => data.Length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            if (_position >= data.Length) return 0;
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class FakeCredentialStore : IAiCredentialStore
    {
        private readonly Dictionary<string, string> _values = new();
        public bool HasCredential(AiCredentialKey key) => _values.ContainsKey(key.Resource);
        public void Save(AiCredentialKey key, string secret) => _values[key.Resource] = secret;
        public string? Retrieve(AiCredentialKey key) => _values.GetValueOrDefault(key.Resource);
        public void Delete(AiCredentialKey key) => _values.Remove(key.Resource);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        private readonly AppSettings _settings = new()
        {
            AiEnabled = true, AiProvider = "OpenAI", AiEndpoint = "https://api.openai.com/v1/", AiModel = "test-model",
        };
        public AppSettings Current => _settings;
        public void Update(Action<AppSettings> update) => update(_settings);
        public void Flush() { }
        public void FlushOrThrow() { }
    }

    private sealed class FakeLocalization : ILocalizationService
    {
        public string CurrentLanguage => "en-US";
        public string this[string key] => key;
        public event Action? LanguageChanged { add { } remove { } }
        public void SetLanguage(string languageCode) { }
    }

    private sealed class FakeEditorBridge : IAiEditorBridge
    {
        public int ApplyCount { get; set; }
        public string? CurrentFilePath => Path.Combine(Path.GetTempPath(), "test.py");
        public bool HasUnsavedChanges { get; set; }
        private readonly AiEditorContext _context = new("print(1)", "test.py", true, 0, 8,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("print(1)"))));
        public Task<AiEditorContext?> RequestAiContextAsync(CancellationToken cancellationToken = default) => Task.FromResult<AiEditorContext?>(_context);
        public Task<bool> ApplyAiCandidateAsync(AiEditorContext original, string candidate, CancellationToken cancellationToken = default)
        { ApplyCount++; return Task.FromResult(true); }
        public Task<bool> PrepareToSwitchAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void OpenGeneratedDraft(string code) => ApplyCount++;
        public Task ReloadFromDiskAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeInteraction : IAiInteractionService
    {
        public bool ConfirmSend { get; init; }
        public bool ConfirmApply { get; init; }
        public Task<bool> ConfirmSendAsync(AiSendPreview preview, string maskedPreview, CancellationToken cancellationToken) => Task.FromResult(ConfirmSend);
        public Task<bool> ConfirmApplyAsync(AiCodeCandidate candidate, string localDiff, CancellationToken cancellationToken) => Task.FromResult(ConfirmApply);
        public Task ShowDiffAsync(string localDiff, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ConfirmDeleteConversationAsync(string title, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeProvider : IAiProviderClient
    {
        public int CallCount { get; private set; }
        public List<AiProviderRequest> Requests { get; } = [];
        public bool WaitForRelease { get; init; }
        public string ResultText { get; init; } = "ok";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AiProviderResult> StreamAsync(AiConfiguration configuration, string apiKey,
            AiProviderRequest request, IProgress<AiStreamEvent>? progress, CancellationToken cancellationToken)
        {
            CallCount++;
            Requests.Add(request);
            Started.TrySetResult();
            if (WaitForRelease) await Release.Task.WaitAsync(cancellationToken);
            progress?.Report(new AiStreamEvent(ResultText));
            return new AiProviderResult(ResultText, "fake-request", 1, 1);
        }
    }

    private sealed class FakeConversationStore : IAiConversationStore
    {
        private readonly Dictionary<string, AiConversationSnapshot> _items = new();
        public IReadOnlyList<AiConversationSummary> List() => _items.Values.Select(item =>
            new AiConversationSummary(item.Id, item.Title, item.Mode, item.UpdatedAtUtc, item.Messages.Count)).ToArray();
        public AiConversationSnapshot? Load(string id) => _items.GetValueOrDefault(id);
        public void Save(AiConversationSnapshot conversation) => _items[conversation.Id] = conversation;
        public void Delete(string id) => _items.Remove(id);
    }

    private sealed class FakeFileContextService : IAiFileContextService
    {
        public Task<AiContextFile> LoadAsync(string path, bool isPrimary, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiContextFile(Path.GetFullPath(path), "test.py", "print(1)", 8,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("print(1)"))),
                "utf-8", false, "\n", isPrimary));
    }

    private sealed class FakePatchService : IAiPatchApplicationService
    {
        public int CallCount { get; private set; }
        public Task<AiPatchApplyResult> ApplyAsync(IReadOnlyList<AiPendingFileChange> changes,
            CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(new AiPatchApplyResult(true, string.Empty, "backup")); }
    }
}
