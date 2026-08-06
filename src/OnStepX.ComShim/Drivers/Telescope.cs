using System;
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
    /// COM telescope driver backed by the OnStepX Alpaca server.
    /// </summary>
    /// <remarks>
    /// The GUID and the ProgID are permanent. Both end up written into client
    /// configurations the first time somebody picks the driver in the Chooser,
    /// so changing either would silently break every existing installation.
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("e4875993-c975-457e-82f7-7df6722ee521")]
    [ProgId("OnStepX.Telescope")]
    [ServedDriver("OnStepX Telescope", DeviceTypes.Telescope)]
    public class Telescope : AlpacaDriverBase, ITelescopeV4
    {
        /// <summary>The server publishes a single mount, as Alpaca device zero.</summary>
        private const int DeviceNumber = 0;

        private readonly Library.ITelescopeV4 _device;

        /// <summary>Creates the driver and its Alpaca client.</summary>
        public Telescope()
            : base(AlpacaEndpoint.CreateClient<AlpacaTelescope>(DeviceNumber))
        {
            _device = (Library.ITelescopeV4)Device;
            ShimLog.Write("Telescope", "Driver instance created");
        }

        /// <inheritdoc />
        public AlignmentModes AlignmentMode => (AlignmentModes)_device.AlignmentMode;

        /// <inheritdoc />
        public double Altitude => _device.Altitude;

        /// <inheritdoc />
        public double ApertureArea => _device.ApertureArea;

        /// <inheritdoc />
        public double ApertureDiameter => _device.ApertureDiameter;

        /// <inheritdoc />
        public bool AtHome => _device.AtHome;

        /// <inheritdoc />
        public bool AtPark => _device.AtPark;

        /// <inheritdoc />
        public double Azimuth => _device.Azimuth;

        /// <inheritdoc />
        public bool CanFindHome => _device.CanFindHome;

        /// <inheritdoc />
        public bool CanPark => _device.CanPark;

        /// <inheritdoc />
        public bool CanPulseGuide => _device.CanPulseGuide;

        /// <inheritdoc />
        public bool CanSetDeclinationRate => _device.CanSetDeclinationRate;

        /// <inheritdoc />
        public bool CanSetGuideRates => _device.CanSetGuideRates;

        /// <inheritdoc />
        public bool CanSetPark => _device.CanSetPark;

        /// <inheritdoc />
        public bool CanSetPierSide => _device.CanSetPierSide;

        /// <inheritdoc />
        public bool CanSetRightAscensionRate => _device.CanSetRightAscensionRate;

        /// <inheritdoc />
        public bool CanSetTracking => _device.CanSetTracking;

        /// <inheritdoc />
        public bool CanSlew => _device.CanSlew;

        /// <inheritdoc />
        public bool CanSlewAltAz => _device.CanSlewAltAz;

        /// <inheritdoc />
        public bool CanSlewAltAzAsync => _device.CanSlewAltAzAsync;

        /// <inheritdoc />
        public bool CanSlewAsync => _device.CanSlewAsync;

        /// <inheritdoc />
        public bool CanSync => _device.CanSync;

        /// <inheritdoc />
        public bool CanSyncAltAz => _device.CanSyncAltAz;

        /// <inheritdoc />
        public bool CanUnpark => _device.CanUnpark;

        /// <inheritdoc />
        public double Declination => _device.Declination;

        /// <inheritdoc />
        public double DeclinationRate
        {
            get => _device.DeclinationRate;
            set => _device.DeclinationRate = value;
        }

        /// <inheritdoc />
        public bool DoesRefraction
        {
            get => _device.DoesRefraction;
            set => _device.DoesRefraction = value;
        }

        /// <inheritdoc />
        public EquatorialCoordinateType EquatorialSystem => (EquatorialCoordinateType)_device.EquatorialSystem;

        /// <inheritdoc />
        public double FocalLength => _device.FocalLength;

        /// <inheritdoc />
        public double GuideRateDeclination
        {
            get => _device.GuideRateDeclination;
            set => _device.GuideRateDeclination = value;
        }

        /// <inheritdoc />
        public double GuideRateRightAscension
        {
            get => _device.GuideRateRightAscension;
            set => _device.GuideRateRightAscension = value;
        }

        /// <inheritdoc />
        public bool IsPulseGuiding => _device.IsPulseGuiding;

        /// <inheritdoc />
        public double RightAscension => _device.RightAscension;

        /// <inheritdoc />
        public double RightAscensionRate
        {
            get => _device.RightAscensionRate;
            set => _device.RightAscensionRate = value;
        }

        /// <inheritdoc />
        public PierSide SideOfPier
        {
            get => (PierSide)_device.SideOfPier;
            set => _device.SideOfPier = (Library.PointingState)value;
        }

        /// <inheritdoc />
        public double SiderealTime => _device.SiderealTime;

        /// <inheritdoc />
        public double SiteElevation
        {
            get => _device.SiteElevation;
            set => _device.SiteElevation = value;
        }

        /// <inheritdoc />
        public double SiteLatitude
        {
            get => _device.SiteLatitude;
            set => _device.SiteLatitude = value;
        }

        /// <inheritdoc />
        public double SiteLongitude
        {
            get => _device.SiteLongitude;
            set => _device.SiteLongitude = value;
        }

        /// <inheritdoc />
        public bool Slewing => _device.Slewing;

        /// <inheritdoc />
        public short SlewSettleTime
        {
            get => _device.SlewSettleTime;
            set => _device.SlewSettleTime = value;
        }

        /// <inheritdoc />
        public double TargetDeclination
        {
            get => _device.TargetDeclination;
            set => _device.TargetDeclination = value;
        }

        /// <inheritdoc />
        public double TargetRightAscension
        {
            get => _device.TargetRightAscension;
            set => _device.TargetRightAscension = value;
        }

        /// <inheritdoc />
        public bool Tracking
        {
            get => _device.Tracking;
            set => _device.Tracking = value;
        }

        /// <inheritdoc />
        public DriveRates TrackingRate
        {
            get => (DriveRates)_device.TrackingRate;
            set => _device.TrackingRate = (Library.DriveRate)value;
        }

        /// <inheritdoc />
        public ITrackingRates TrackingRates => new ComTrackingRates(_device.TrackingRates);

        /// <inheritdoc />
        public DateTime UTCDate
        {
            get => _device.UTCDate;
            set => _device.UTCDate = value;
        }

        /// <inheritdoc />
        public void AbortSlew()
        {
            _device.AbortSlew();
        }

        /// <inheritdoc />
        public IAxisRates AxisRates(TelescopeAxes Axis)
        {
            return new ComAxisRates(_device.AxisRates((Library.TelescopeAxis)Axis));
        }

        /// <inheritdoc />
        public bool CanMoveAxis(TelescopeAxes Axis)
        {
            return _device.CanMoveAxis((Library.TelescopeAxis)Axis);
        }

        /// <inheritdoc />
        public PierSide DestinationSideOfPier(double RightAscension, double Declination)
        {
            return (PierSide)_device.DestinationSideOfPier(RightAscension, Declination);
        }

        /// <inheritdoc />
        public void FindHome()
        {
            _device.FindHome();
        }

        /// <inheritdoc />
        public void MoveAxis(TelescopeAxes Axis, double Rate)
        {
            _device.MoveAxis((Library.TelescopeAxis)Axis, Rate);
        }

        /// <inheritdoc />
        public void Park()
        {
            _device.Park();
        }

        /// <inheritdoc />
        public void PulseGuide(GuideDirections Direction, int Duration)
        {
            _device.PulseGuide((Library.GuideDirection)Direction, Duration);
        }

        /// <inheritdoc />
        public void SetPark()
        {
            _device.SetPark();
        }

        /// <inheritdoc />
        public void SlewToAltAz(double Azimuth, double Altitude)
        {
            _device.SlewToAltAz(Azimuth, Altitude);
        }

        /// <inheritdoc />
        public void SlewToAltAzAsync(double Azimuth, double Altitude)
        {
            _device.SlewToAltAzAsync(Azimuth, Altitude);
        }

        /// <inheritdoc />
        public void SlewToCoordinates(double RightAscension, double Declination)
        {
            _device.SlewToCoordinates(RightAscension, Declination);
        }

        /// <inheritdoc />
        public void SlewToCoordinatesAsync(double RightAscension, double Declination)
        {
            _device.SlewToCoordinatesAsync(RightAscension, Declination);
        }

        /// <inheritdoc />
        public void SlewToTarget()
        {
            _device.SlewToTarget();
        }

        /// <inheritdoc />
        public void SlewToTargetAsync()
        {
            _device.SlewToTargetAsync();
        }

        /// <inheritdoc />
        public void SyncToAltAz(double Azimuth, double Altitude)
        {
            _device.SyncToAltAz(Azimuth, Altitude);
        }

        /// <inheritdoc />
        public void SyncToCoordinates(double RightAscension, double Declination)
        {
            _device.SyncToCoordinates(RightAscension, Declination);
        }

        /// <inheritdoc />
        public void SyncToTarget()
        {
            _device.SyncToTarget();
        }

        /// <inheritdoc />
        public void Unpark()
        {
            _device.Unpark();
        }
    }
}
