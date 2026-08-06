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
    /// COM observing conditions driver backed by the OnStepX Alpaca server.
    /// </summary>
    /// <remarks>
    /// The GUID and the ProgID are permanent, see <see cref="Telescope"/> for
    /// why.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("06700de6-9b16-4b8b-a708-b76ba12faee5")]
    [ProgId("OnStepX.ObservingConditions")]
    [ServedDriver("OnStepX Observing Conditions", DeviceTypes.ObservingConditions)]
    public class ObservingConditions : AlpacaDriverBase, IObservingConditionsV2
    {
        private const int DeviceNumber = 0;

        private readonly Library.IObservingConditionsV2 _device;

        /// <summary>Creates the driver and its Alpaca client.</summary>
        public ObservingConditions()
            : base(AlpacaEndpoint.CreateClient<AlpacaObservingConditions>(DeviceNumber))
        {
            _device = (Library.IObservingConditionsV2)Device;
            ShimLog.Write("ObservingConditions", "Driver instance created");
        }

        /// <inheritdoc />
        public double AveragePeriod
        {
            get => _device.AveragePeriod;
            set => _device.AveragePeriod = value;
        }

        /// <inheritdoc />
        public double CloudCover => _device.CloudCover;

        /// <inheritdoc />
        public double DewPoint => _device.DewPoint;

        /// <inheritdoc />
        public double Humidity => _device.Humidity;

        /// <inheritdoc />
        public double Pressure => _device.Pressure;

        /// <inheritdoc />
        public double RainRate => _device.RainRate;

        /// <inheritdoc />
        public double SkyBrightness => _device.SkyBrightness;

        /// <inheritdoc />
        public double SkyQuality => _device.SkyQuality;

        /// <inheritdoc />
        public double SkyTemperature => _device.SkyTemperature;

        /// <inheritdoc />
        public double StarFWHM => _device.StarFWHM;

        /// <inheritdoc />
        public double Temperature => _device.Temperature;

        /// <inheritdoc />
        public double WindDirection => _device.WindDirection;

        /// <inheritdoc />
        public double WindGust => _device.WindGust;

        /// <inheritdoc />
        public double WindSpeed => _device.WindSpeed;

        /// <inheritdoc />
        public void Refresh()
        {
            _device.Refresh();
        }

        /// <inheritdoc />
        public string SensorDescription(string PropertyName)
        {
            return _device.SensorDescription(PropertyName);
        }

        /// <inheritdoc />
        public double TimeSinceLastUpdate(string PropertyName)
        {
            return _device.TimeSinceLastUpdate(PropertyName);
        }
    }
}
