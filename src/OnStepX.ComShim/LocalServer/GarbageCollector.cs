using System;
using System.Threading;
using System.Threading.Tasks;

namespace OnStepX.ComShim.LocalServer
{
    /// <summary>
    /// Collects garbage on a timer while the local server is running.
    /// </summary>
    /// <remarks>
    /// Driver objects report that they are gone from their finaliser, and
    /// finalisers only run after a collection. Left to itself the runtime may
    /// never collect in a process that is doing nothing, which would leave the
    /// server alive long after its last client disconnected.
    /// </remarks>
    internal sealed class GarbageCollector
    {
        private readonly TimeSpan _interval;

        private CancellationTokenSource _cancellation;
        private Task _task;

        /// <summary>Creates a collector that runs every <paramref name="interval"/>.</summary>
        internal GarbageCollector(TimeSpan interval)
        {
            _interval = interval;
        }

        /// <summary>Starts the background collection loop.</summary>
        internal void Start()
        {
            _cancellation = new CancellationTokenSource();

            CancellationToken token = _cancellation.Token;
            _task = Task.Factory.StartNew(
                () => Loop(token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        /// <summary>Stops the loop and waits for it to finish.</summary>
        internal void Stop()
        {
            if (_cancellation == null)
            {
                return;
            }

            _cancellation.Cancel();

            try
            {
                _task.Wait();
            }
            catch (AggregateException)
            {
                // The loop only ends through its cancellation token, and the
                // process is closing anyway.
            }

            _cancellation.Dispose();
            _cancellation = null;
            _task = null;
        }

        private void Loop(CancellationToken token)
        {
            while (!token.WaitHandle.WaitOne(_interval))
            {
                GC.Collect();
            }

            // One last pass so objects released during shutdown still get their
            // finalisers run before the process ends.
            GC.Collect();
        }
    }
}
