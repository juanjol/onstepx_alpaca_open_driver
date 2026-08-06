using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OnStepX.ComShim.LocalServer
{
    /// <summary>
    /// The COM <c>IClassFactory</c> interface, redeclared because the .NET
    /// Framework does not expose it.
    /// </summary>
    /// <remarks>
    /// It is deliberately not visible to COM as a managed type: the GUID below
    /// is the real one COM uses, so the runtime marshals it as the original
    /// interface rather than exporting a copy of it.
    /// </remarks>
    [ComImport]
    [ComVisible(false)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000001-0000-0000-C000-000000000046")]
    public interface IClassFactory
    {
        /// <summary>Creates one instance of the served class.</summary>
        void CreateInstance(IntPtr outerUnknown, ref Guid interfaceId, out IntPtr instance);

        /// <summary>Holds the server open even with no live objects.</summary>
        void LockServer(bool takeLock);
    }

    /// <summary>
    /// Class factory that can serve any managed type, given its
    /// <see cref="Type"/>.
    /// </summary>
    /// <remarks>
    /// One instance is registered per driver class. Registration happens
    /// suspended and is resumed only once every factory is in place, so COM
    /// cannot activate a half initialised server.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ClassFactory : IClassFactory
    {
        private const int ClassENoAggregation = unchecked((int)0x80040110);
        private const int ENoInterface = unchecked((int)0x80004002);

        private const uint ClsCtxLocalServer = 0x4;
        private const uint RegClsMultipleUse = 0x1;
        private const uint RegClsSuspended = 0x4;

        private static readonly Guid IidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IidDispatch = new Guid("00020400-0000-0000-C000-000000000046");

        private readonly Type _servedType;
        private readonly Guid _classId;
        private readonly List<Type> _servedInterfaces;

        private uint _cookie;

        /// <summary>Creates the factory for a served driver type.</summary>
        internal ClassFactory(Type servedType)
        {
            if (servedType == null)
            {
                throw new ArgumentNullException(nameof(servedType));
            }

            _servedType = servedType;
            _classId = Marshal.GenerateGuidForType(servedType);
            _servedInterfaces = new List<Type>(servedType.GetInterfaces());
        }

        /// <summary>Name of the served type, for logging.</summary>
        internal string ServedTypeName => _servedType.Name;

        /// <summary>Adds this factory to the COM table of class objects.</summary>
        internal bool Register()
        {
            Guid classId = _classId;
            int result = CoRegisterClassObject(
                ref classId,
                this,
                ClsCtxLocalServer,
                RegClsMultipleUse | RegClsSuspended,
                out _cookie);

            return result == 0;
        }

        /// <summary>Removes this factory from the COM table of class objects.</summary>
        internal bool Revoke()
        {
            return CoRevokeClassObject(_cookie) == 0;
        }

        /// <summary>Lets COM start activating the registered class objects.</summary>
        internal static bool ResumeAll()
        {
            return CoResumeClassObjects() == 0;
        }

        /// <summary>Stops COM from activating any more class objects.</summary>
        internal static bool SuspendAll()
        {
            return CoSuspendClassObjects() == 0;
        }

        void IClassFactory.CreateInstance(IntPtr outerUnknown, ref Guid interfaceId, out IntPtr instance)
        {
            instance = IntPtr.Zero;

            // None of the served drivers supports aggregation, and saying so is
            // what the COM contract expects instead of failing later.
            if (outerUnknown != IntPtr.Zero)
            {
                throw new COMException("Aggregation is not supported", ClassENoAggregation);
            }

            ShimLog.Write("ClassFactory", $"Creating a {_servedType.Name} for interface {interfaceId:B}");

            object driver = Activator.CreateInstance(_servedType);

            foreach (Type servedInterface in _servedInterfaces)
            {
                if (interfaceId == Marshal.GenerateGuidForType(servedInterface))
                {
                    instance = Marshal.GetComInterfaceForObject(driver, servedInterface);
                    return;
                }
            }

            if (interfaceId == IidDispatch)
            {
                instance = Marshal.GetIDispatchForObject(driver);
                return;
            }

            if (interfaceId == IidUnknown)
            {
                instance = Marshal.GetIUnknownForObject(driver);
                return;
            }

            ShimLog.Write("ClassFactory", $"{_servedType.Name} does not implement {interfaceId:B}");
            throw new COMException("Interface not supported by this driver", ENoInterface);
        }

        void IClassFactory.LockServer(bool takeLock)
        {
            if (takeLock)
            {
                ServerState.IncrementLockCount();
            }
            else
            {
                ServerState.DecrementLockCount();
            }

            ServerState.ExitIfIdle();
        }

        [DllImport("ole32.dll")]
        private static extern int CoRegisterClassObject(
            [In] ref Guid classId,
            [MarshalAs(UnmanagedType.IUnknown)] object classObject,
            uint classContext,
            uint flags,
            out uint cookie);

        [DllImport("ole32.dll")]
        private static extern int CoRevokeClassObject(uint cookie);

        [DllImport("ole32.dll")]
        private static extern int CoResumeClassObjects();

        [DllImport("ole32.dll")]
        private static extern int CoSuspendClassObjects();
    }
}
