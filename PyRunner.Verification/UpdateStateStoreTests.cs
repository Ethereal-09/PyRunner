using System.Collections.Concurrent;
using PyRunner.Models;
using PyRunner.Services;

internal static class UpdateStateStoreTests
{
    public static async Task<int> RunAsync()
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

        await Task.Run(() => store.Complete(new UpdateCheckResult(UpdateCheckStatus.Offline)));
        Verify(!notificationRaised && dispatcher.PendingCount == 1,
            "background update state publication is queued instead of touching UI subscribers");
        dispatcher.Drain();
        Verify(notificationRaised && notificationHadUiAccess,
            "queued update state notification runs on the UI dispatcher thread");

        return failures;
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
