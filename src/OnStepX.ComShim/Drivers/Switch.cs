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
    /// COM switch driver backed by the OnStepX Alpaca server.
    /// </summary>
    /// <remarks>
    /// The GUID and the ProgID are permanent, see <see cref="Telescope"/> for
    /// why.
    /// <para>
    /// The COM <c>ISwitchV3</c> does not inherit <c>ISwitchV2</c> the way the
    /// library one does, so every member is declared here. They are all
    /// forwarded unchanged: nothing on this interface uses a type the two
    /// worlds disagree about, so unlike the telescope there is no conversion
    /// to get wrong.
    /// </para>
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("42c27c0d-66c4-4e9d-9c1e-f3437e32c134")]
    [ProgId("OnStepX.Switch")]
    [ServedDriver("OnStepX Switch", DeviceTypes.Switch)]
    public class Switch : AlpacaDriverBase, ISwitchV3
    {
        private const int DeviceNumber = 0;

        private readonly Library.ISwitchV3 _device;

        /// <summary>Creates the driver and its Alpaca client.</summary>
        public Switch()
            : base(AlpacaEndpoint.CreateClient<AlpacaSwitch>(DeviceNumber))
        {
            _device = (Library.ISwitchV3)Device;
            ShimLog.Write("Switch", "Driver instance created");
        }

        /// <inheritdoc />
        public short MaxSwitch => _device.MaxSwitch;

        /// <inheritdoc />
        public bool CanWrite(short id)
        {
            return _device.CanWrite(id);
        }

        /// <inheritdoc />
        public bool GetSwitch(short id)
        {
            return _device.GetSwitch(id);
        }

        /// <inheritdoc />
        public string GetSwitchDescription(short id)
        {
            return _device.GetSwitchDescription(id);
        }

        /// <inheritdoc />
        public string GetSwitchName(short id)
        {
            return _device.GetSwitchName(id);
        }

        /// <inheritdoc />
        public double GetSwitchValue(short id)
        {
            return _device.GetSwitchValue(id);
        }

        /// <inheritdoc />
        public double MaxSwitchValue(short id)
        {
            return _device.MaxSwitchValue(id);
        }

        /// <inheritdoc />
        public double MinSwitchValue(short id)
        {
            return _device.MinSwitchValue(id);
        }

        /// <inheritdoc />
        public void SetSwitch(short id, bool state)
        {
            _device.SetSwitch(id, state);
        }

        /// <inheritdoc />
        public void SetSwitchName(short id, string name)
        {
            _device.SetSwitchName(id, name);
        }

        /// <inheritdoc />
        public void SetSwitchValue(short id, double value)
        {
            _device.SetSwitchValue(id, value);
        }

        /// <inheritdoc />
        public double SwitchStep(short id)
        {
            return _device.SwitchStep(id);
        }

        /// <inheritdoc />
        public bool CanAsync(short id)
        {
            return _device.CanAsync(id);
        }

        /// <inheritdoc />
        public void SetAsync(short id, bool state)
        {
            _device.SetAsync(id, state);
        }

        /// <inheritdoc />
        public void SetAsyncValue(short id, double value)
        {
            _device.SetAsyncValue(id, value);
        }

        /// <inheritdoc />
        public bool StateChangeComplete(short id)
        {
            return _device.StateChangeComplete(id);
        }

        /// <inheritdoc />
        public void CancelAsync(short id)
        {
            _device.CancelAsync(id);
        }
    }
}
