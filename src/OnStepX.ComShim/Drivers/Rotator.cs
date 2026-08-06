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
    /// COM rotator driver backed by the OnStepX Alpaca server.
    /// </summary>
    /// <remarks>
    /// The GUID and the ProgID are permanent, see <see cref="Telescope"/> for
    /// why.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("19864d5f-c675-4df4-b311-70e5fa08c0cc")]
    [ProgId("OnStepX.Rotator")]
    [ServedDriver("OnStepX Rotator", DeviceTypes.Rotator)]
    public class Rotator : AlpacaDriverBase, IRotatorV4
    {
        private const int DeviceNumber = 0;

        private readonly Library.IRotatorV4 _device;

        /// <summary>Creates the driver and its Alpaca client.</summary>
        public Rotator()
            : base(AlpacaEndpoint.CreateClient<AlpacaRotator>(DeviceNumber))
        {
            _device = (Library.IRotatorV4)Device;
            ShimLog.Write("Rotator", "Driver instance created");
        }

        /// <inheritdoc />
        public bool CanReverse => _device.CanReverse;

        /// <inheritdoc />
        public bool IsMoving => _device.IsMoving;

        /// <inheritdoc />
        public float Position => _device.Position;

        /// <inheritdoc />
        public bool Reverse
        {
            get => _device.Reverse;
            set => _device.Reverse = value;
        }

        /// <inheritdoc />
        public float StepSize => _device.StepSize;

        /// <inheritdoc />
        public float TargetPosition => _device.TargetPosition;

        /// <inheritdoc />
        public float MechanicalPosition => _device.MechanicalPosition;

        /// <inheritdoc />
        public void Halt()
        {
            _device.Halt();
        }

        /// <inheritdoc />
        public void Move(float Position)
        {
            _device.Move(Position);
        }

        /// <inheritdoc />
        public void MoveAbsolute(float Position)
        {
            _device.MoveAbsolute(Position);
        }

        /// <inheritdoc />
        public void MoveMechanical(float Position)
        {
            _device.MoveMechanical(Position);
        }

        /// <inheritdoc />
        public void Sync(float Position)
        {
            _device.Sync(Position);
        }
    }
}
