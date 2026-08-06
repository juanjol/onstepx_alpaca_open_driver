using ASCOM.Alpaca;
using Microsoft.Extensions.Logging;

namespace OnStepX.AlpacaServer;

/// <summary>
/// Registers the devices with the official REST layer's <c>DeviceManager</c>.
/// </summary>
/// <remarks>
/// Registration has to happen <b>before</b> the host starts serving requests, because the
/// Alpaca controllers resolve the device by number on every call and an unregistered
/// device number is an error.
/// </remarks>
public static class DeviceRegistration
{
    private static readonly List<OnStepX.Devices.OnStepDeviceBase> Registered = [];

    /// <summary>
    /// Devices registered in this process.
    /// </summary>
    /// <remarks>
    /// Kept so that a write from the setup UI can mark their cached snapshots as stale.
    /// The official <c>DeviceManager</c> exposes devices only through its typed accessors,
    /// so there is no way to enumerate them back out of it.
    /// </remarks>
    public static IReadOnlyList<OnStepX.Devices.OnStepDeviceBase> Devices
    {
        get
        {
            lock (Registered)
            {
                return Registered.ToArray();
            }
        }
    }

    /// <summary>Marks every registered device's cached snapshot as stale.</summary>
    public static void InvalidateAllSnapshots()
    {
        foreach (OnStepX.Devices.OnStepDeviceBase device in Devices)
        {
            device.InvalidateSnapshot();
        }
    }

    /// <summary>Registers every device this build offers.</summary>
    public static void RegisterAll(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ILogger logger = loggerFactory.CreateLogger(nameof(DeviceRegistration));

        int registered = 0;

        registered += RegisterTelescope(loggerFactory, logger);
        registered += RegisterFocuser(loggerFactory, logger);
        registered += RegisterRotator(loggerFactory, logger);
        registered += RegisterObservingConditions(loggerFactory, logger);
        registered += RegisterSwitch(loggerFactory, logger);

        if (registered == 0)
        {
            logger.LogWarning(
                "No devices were registered. The server will still answer discovery " +
                "but will offer nothing to connect to.");
        }
        else
        {
            logger.LogInformation("Registered {Count} devices", registered);
        }
    }

    private static void Track(OnStepX.Devices.OnStepDeviceBase device)
    {
        lock (Registered)
        {
            Registered.Add(device);
        }
    }

    private static int RegisterTelescope(ILoggerFactory loggerFactory, ILogger logger)
    {
        var telescope = new OnStepX.Devices.OnStepTelescope(
            ServerRuntime.Connection,
            () => ServerRuntime.Settings,
            loggerFactory);

        DeviceManager.LoadTelescope(
            DeviceNumber,
            telescope,
            telescope.Name,
            UniqueId("telescope"));

        Track(telescope);

        logger.LogInformation("Registered telescope device {Number}", DeviceNumber);

        return 1;
    }

    /// <summary>
    /// There is only ever one mount behind one controller, so every device type uses
    /// number zero.
    /// </summary>
    private const int DeviceNumber = 0;

    /// <summary>
    /// Stable per device identifier.
    /// </summary>
    /// <remarks>
    /// Alpaca clients use this to recognise the same physical device across restarts,
    /// so it must not change between runs. A fixed namespace GUID combined with the
    /// device type gives that without needing anything persisted.
    /// </remarks>
    private static string UniqueId(string deviceType)
    {
        // Fixed namespace for this driver. Chosen once and never changed.
        const string Namespace = "5f2c9a41-8d3b-4e17-9c62-onstepx-ascom";

        byte[] bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(Namespace + ":" + deviceType));

        return new Guid(bytes).ToString();
    }

    private static int RegisterFocuser(ILoggerFactory loggerFactory, ILogger logger)
    {
        int number = Math.Clamp(ServerRuntime.Settings.Focuser.FocuserNumber, 1, 6);

        var focuser = new OnStepX.Devices.OnStepFocuser(
            ServerRuntime.Connection,
            () => ServerRuntime.Settings,
            loggerFactory,
            number);

        DeviceManager.LoadFocuser(
            DeviceNumber,
            focuser,
            focuser.Name,
            UniqueId("focuser"));

        Track(focuser);

        logger.LogInformation("Registered focuser device {Number}, OnStep focuser {Focuser}",
            DeviceNumber, number);

        return 1;
    }

    private static int RegisterRotator(ILoggerFactory loggerFactory, ILogger logger)
    {
        var rotator = new OnStepX.Devices.OnStepRotator(
            ServerRuntime.Connection,
            () => ServerRuntime.Settings,
            loggerFactory);

        DeviceManager.LoadRotator(
            DeviceNumber,
            rotator,
            rotator.Name,
            UniqueId("rotator"));

        Track(rotator);

        logger.LogInformation("Registered rotator device {Number}", DeviceNumber);

        return 1;
    }

    private static int RegisterObservingConditions(ILoggerFactory loggerFactory, ILogger logger)
    {
        var weather = new OnStepX.Devices.OnStepObservingConditions(
            ServerRuntime.Connection,
            () => ServerRuntime.Settings,
            loggerFactory);

        DeviceManager.LoadObservingConditions(
            DeviceNumber,
            weather,
            weather.Name,
            UniqueId("observingconditions"));

        Track(weather);

        logger.LogInformation("Registered observing conditions device {Number}", DeviceNumber);

        return 1;
    }

    /// <summary>
    /// Registers the switch device, which exposes the controller's auxiliary features.
    /// </summary>
    /// <remarks>
    /// Registered unconditionally, like the others. Whether this controller has any auxiliary
    /// features cannot be known here, because finding out takes a command and nothing is
    /// connected yet, so the device answers that question when a client connects to it.
    /// </remarks>
    private static int RegisterSwitch(ILoggerFactory loggerFactory, ILogger logger)
    {
        var auxiliary = new OnStepX.Devices.OnStepSwitch(
            ServerRuntime.Connection,
            () => ServerRuntime.Settings,
            loggerFactory);

        DeviceManager.LoadSwitch(
            DeviceNumber,
            auxiliary,
            auxiliary.Name,
            UniqueId("switch"));

        Track(auxiliary);

        logger.LogInformation("Registered switch device {Number}", DeviceNumber);

        return 1;
    }
}
