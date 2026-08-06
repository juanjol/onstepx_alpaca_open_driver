using ASCOM.Common.Interfaces;
using ASCOM.Tools;

namespace OnStepX.ComShim.LocalServer
{
    /// <summary>
    /// Trace log shared by the local server and by the drivers it serves.
    /// </summary>
    /// <remarks>
    /// A COM local server has nowhere to print to. COM starts it with no
    /// console and its only window is hidden, so a log file is the single way
    /// to find out what happened during an activation. Every call swallows its
    /// own failures on purpose: a problem writing the log must never take down
    /// a driver that a client is already using.
    /// </remarks>
    internal static class ShimLog
    {
        private static readonly object Gate = new object();

        private static TraceLogger _logger;

        /// <summary>Opens the log file. Safe to call more than once.</summary>
        internal static void Start()
        {
            lock (Gate)
            {
                if (_logger != null)
                {
                    return;
                }

                try
                {
                    _logger = new TraceLogger("OnStepXComShim", true, 25, LogLevel.Information);
                }
                catch
                {
                    _logger = null;
                }
            }
        }

        /// <summary>Writes one line, doing nothing if the log is unavailable.</summary>
        internal static void Write(string source, string message)
        {
            lock (Gate)
            {
                if (_logger == null)
                {
                    return;
                }

                try
                {
                    _logger.LogMessage(source, message);
                }
                catch
                {
                    // Losing a trace line is always preferable to failing the
                    // COM call that produced it.
                }
            }
        }

        /// <summary>Closes the log file.</summary>
        internal static void Stop()
        {
            lock (Gate)
            {
                if (_logger == null)
                {
                    return;
                }

                try
                {
                    _logger.Dispose();
                }
                catch
                {
                }
                finally
                {
                    _logger = null;
                }
            }
        }
    }
}
