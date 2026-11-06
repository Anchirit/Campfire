using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Campfire.Platform
{
    /// <summary>Preserves the original session's single UI thread semantics without a UI.</summary>
    internal sealed class SerialContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object state)
        {
            if (!_queue.IsAddingCompleted)
            {
                try { _queue.Add((callback, state)); }
                catch (InvalidOperationException) { }
            }
        }

        public void Run(Func<Task> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                Task task = action();
                _ = task.ContinueWith(_ => _queue.CompleteAdding(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                foreach (var work in _queue.GetConsumingEnumerable()) work.Callback(work.State);
                task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }

        public void Dispose() => _queue.Dispose();
    }
}
