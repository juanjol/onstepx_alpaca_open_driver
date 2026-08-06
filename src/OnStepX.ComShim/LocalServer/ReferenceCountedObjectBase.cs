using System.Runtime.InteropServices;

namespace OnStepX.ComShim.LocalServer
{
    /// <summary>
    /// Base class for every object handed out to a COM client, so the local
    /// server knows when it is no longer needed.
    /// </summary>
    /// <remarks>
    /// The finaliser is the only signal available: a COM client releasing its
    /// last reference drops the reference count of the callable wrapper, which
    /// makes the managed object collectable, but nothing calls back into this
    /// process. That is also why the finaliser is never suppressed, not even
    /// after <c>Dispose</c>.
    /// </remarks>
    [ComVisible(false)]
    public abstract class ReferenceCountedObjectBase
    {
        /// <summary>Counts this object as in use.</summary>
        protected ReferenceCountedObjectBase()
        {
            ServerState.IncrementObjectCount();
        }

        /// <summary>Stops counting this object and closes the server if idle.</summary>
        ~ReferenceCountedObjectBase()
        {
            ServerState.DecrementObjectCount();
            ServerState.ExitIfIdle();
        }
    }
}
