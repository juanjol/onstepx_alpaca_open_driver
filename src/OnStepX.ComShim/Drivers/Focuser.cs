using System.Runtime.InteropServices;
using ASCOM.Alpaca.Clients;
using ASCOM.Common;
using ASCOM.DeviceInterface;
using OnStepX.ComShim.Config;
using OnStepX.ComShim.LocalServer;
using Library = ASCOM.Common.DeviceInterfaces;

namespace OnStepX.ComShim.Drivers
{
    /// <summary>
    /// COM focuser driver backed by the OnStepX Alpaca server.
    /// </summary>
    /// <remarks>
    /// Only the first focuser is exposed for now. The Alpaca server can publish
    /// up to six, but each extra one needs its own permanent GUID and ProgID,
    /// so they are left for a later increment rather than invented here.
    /// The GUID and the ProgID below are permanent, see
    /// <see cref="Telescope"/> for why.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("9cd5dbdd-01fb-42cf-8529-c1716b6accb5")]
    [ProgId("OnStepX.Focuser")]
    [ServedDriver("OnStepX Focuser", DeviceTypes.Focuser)]
    public class Focuser : AlpacaDriverBase, IFocuserV4
    {
        private const int DeviceNumber = 0;

        private readonly Library.IFocuserV4 _device;

        /// <summary>Creates the driver and its Alpaca client.</summary>
        public Focuser()
            : base(AlpacaEndpoint.CreateClient<AlpacaFocuser>(DeviceNumber))
        {
            _device = (Library.IFocuserV4)Device;
            ShimLog.Write("Focuser", "Driver instance created");
        }

        /// <inheritdoc />
        public bool Absolute => _device.Absolute;

        /// <inheritdoc />
        public bool IsMoving => _device.IsMoving;

        /// <summary>
        /// Connected state under its original name.
        /// </summary>
        /// <remarks>
        /// <c>Link</c> predates <c>Connected</c> and means exactly the same
        /// thing. It survives in the interface only for clients old enough to
        /// still use it.
        /// </remarks>
        public bool Link
        {
            get => Connected;
            set => Connected = value;
        }

        /// <inheritdoc />
        public int MaxIncrement => _device.MaxIncrement;

        /// <inheritdoc />
        public int MaxStep => _device.MaxStep;

        /// <inheritdoc />
        public int Position => _device.Position;

        /// <inheritdoc />
        public double StepSize => _device.StepSize;

        /// <inheritdoc />
        public bool TempComp
        {
            get => _device.TempComp;
            set => _device.TempComp = value;
        }

        /// <inheritdoc />
        public bool TempCompAvailable => _device.TempCompAvailable;

        /// <inheritdoc />
        public double Temperature => _device.Temperature;

        /// <inheritdoc />
        public void Halt()
        {
            _device.Halt();
        }

        /// <inheritdoc />
        public void Move(int Position)
        {
            _device.Move(Position);
        }
    }
}
