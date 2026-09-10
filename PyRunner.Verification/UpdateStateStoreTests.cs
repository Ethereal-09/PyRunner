using System.Collections.Concurrent;
using PyRunner.Models;
using PyRunner.Services;

internal static class UpdateStateStoreTests
{
    public static Task<int> RunAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}");
            }
        }

        var dispatcher = new QueuedDispatcher();
        var store = new UpdateStateStore(dispatcher);
        var notificationRaised = false;
        var notificationHadUiAccess = false;
        store.Changed += (_, _) =>
        {
            notificationRaised = true;
            notificationHadUiAccess = dispatcher.HasThreadAccess;
        };

        var worker = new Thread(() => store.Complete(new UpdateCheckResult(UpdateCheckStatus.Offline)))
        {
            IsBackground = true,
        };
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Background update state publication did not complete.");
        Verify(!notificationRaised && dispatcher.PendingCount == 1,
            "background update state publication is queued instead of touching UI subscribers");
        dispatcher.Drain();
        Verify(notificationRaised && notificationHadUiAccess,
            "queued update state notification runs on the UI dispatcher thread");

        return Task.FromResult(failures);
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _actions = new();
        private bool _draining;
        private int OwnerThreadId { get; } = Environment.CurrentManagedThreadId;
        public int PendingCount => _actions.Count;
        public bool HasThreadAccess => _draining || Environment.CurrentManagedThreadId == OwnerThreadId;
        public bool TryEnqueue(Action action)
        {
            _actions.Enqueue(action);
            return true;
        }

        public void Drain()
        {
            _draining = true;
            try
            {
                while (_actions.TryDequeue(out var action)) action();
            }
            finally
            {
                _draining = false;
            }
        }
    }
}
