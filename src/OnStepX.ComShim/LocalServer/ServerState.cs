using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OnStepX.ComShim.LocalServer
{
    /// <summary>
    /// Lifetime bookkeeping for the local server: how many driver objects are
    /// alive and how many times COM has locked the server.
    /// </summary>
    /// <remarks>
    /// COM never tells a local server that a client has released its last
    /// reference, so the count is kept by hand: incremented when a driver
    /// object is constructed and decremented from its finaliser, which is why
    /// <see cref="GarbageCollector"/> has to keep collecting on a timer.
    /// </remarks>
    internal static class ServerState
    {
        private const uint WmQuit = 0x0012;

        private static readonly object Gate = new object();

        private static int _objectCount;
        private static int _lockCount;
        private static uint _mainThreadId;
        private static bool _startedByCom;

        /// <summary>Number of driver objects currently alive.</summary>
        internal static int ObjectCount
        {
            get
            {
                lock (Gate)
                {
                    return _objectCount;
                }
            }
        }

        /// <summary>Number of outstanding <c>IClassFactory::LockServer</c> locks.</summary>
        internal static int LockCount
        {
            get
            {
                lock (Gate)
                {
                    return _lockCount;
                }
            }
        }

        /// <summary>
        /// Records the thread that owns the message pump and whether COM
        /// started this process.
        /// </summary>
        /// <remarks>
        /// Only a process COM started on demand may shut itself down when it
        /// falls idle. One a user launched has to stay up, because there is no
        /// client whose disconnection would justify closing it.
        /// </remarks>
        internal static void CaptureMainThread(bool startedByCom)
        {
            lock (Gate)
            {
                _mainThreadId = GetCurrentThreadId();
                _startedByCom = startedByCom;
            }
        }

        /// <summary>Counts a newly constructed driver object.</summary>
        internal static void IncrementObjectCount()
        {
            int count = Interlocked.Increment(ref _objectCount);
            ShimLog.Write("ServerState", $"Driver objects alive: {count}");
        }

        /// <summary>Counts a finalised driver object.</summary>
        internal static void DecrementObjectCount()
        {
            int count = Interlocked.Decrement(ref _objectCount);
            ShimLog.Write("ServerState", $"Driver objects alive: {count}");
        }

        /// <summary>Counts a <c>LockServer(true)</c> call.</summary>
        internal static void IncrementLockCount()
        {
            int count = Interlocked.Increment(ref _lockCount);
            ShimLog.Write("ServerState", $"Server locks held: {count}");
        }

        /// <summary>Counts a <c>LockServer(false)</c> call.</summary>
        internal static void DecrementLockCount()
        {
            int count = Interlocked.Decrement(ref _lockCount);
            ShimLog.Write("ServerState", $"Server locks held: {count}");
        }

        /// <summary>
        /// Ends the process if nothing is using it any more.
        /// </summary>
        /// <remarks>
        /// The message pump cannot be stopped from an arbitrary thread, and
        /// this runs on whichever thread happened to finalise the last driver
        /// object, so the pump is asked to end by posting <c>WM_QUIT</c> to the
        /// thread that owns it.
        /// </remarks>
        internal static void ExitIfIdle()
        {
            lock (Gate)
            {
                if (_objectCount > 0 || _lockCount > 0 || !_startedByCom)
                {
                    return;
                }

                ShimLog.Write("ServerState", "Nothing left in use, ending the message pump");
                PostThreadMessage(_mainThreadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
