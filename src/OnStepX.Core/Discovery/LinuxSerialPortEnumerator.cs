using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;

namespace OnStepX.Core.Discovery;

/// <summary>
/// Enumerates serial ports on Linux by reading <c>/sys</c> and
/// <c>/dev/serial/by-id</c>.
/// </summary>
/// <remarks>
/// There is no WMI, but <c>/sys/class/tty/&lt;dev&gt;/device</c> leads
/// through symbolic links to the USB device, where <c>idVendor</c> and
/// <c>idProduct</c> give the same as Windows's VID and PID, and
/// <c>product</c> or <c>manufacturer</c> give the friendly name.
/// </remarks>
public sealed partial class LinuxSerialPortEnumerator : ISerialPortEnumerator
{
    private const string DefaultSysClassTty = "/sys/class/tty";
    private const string DefaultDevSerialById = "/dev/serial/by-id";

    private readonly string _sysClassTty;
    private readonly string _devSerialById;
    private readonly Func<string[]> _portNameSource;

    /// <summary>Creates the enumerator with the system's real paths.</summary>
    public LinuxSerialPortEnumerator()
        : this(DefaultSysClassTty, DefaultDevSerialById, null)
    {
    }

    /// <summary>
    /// Creates the enumerator with configurable paths and name source.
    /// </summary>
    /// <remarks>
    /// This exists so sysfs parsing can be tested against a synthetic
    /// tree. It is the only way to verify this code path, because neither
    /// the development machine nor the CI one have any serial port, and
    /// yet this is the path that will be used when deploying on a
    /// Raspberry Pi next to the mount.
    /// </remarks>
    public LinuxSerialPortEnumerator(
        string sysClassTty,
        string devSerialById,
        Func<string[]>? portNameSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sysClassTty);
        ArgumentException.ThrowIfNullOrWhiteSpace(devSerialById);

        _sysClassTty = sysClassTty;
        _devSerialById = devSerialById;
        _portNameSource = portNameSource ?? SafeGetPortNames;
    }

    /// <inheritdoc />
    public string Description => "sysfs and /dev/serial/by-id";

    /// <inheritdoc />
    public IReadOnlyList<SerialPortInfo> Enumerate()
    {
        var results = new List<SerialPortInfo>();

        foreach (string portName in _portNameSource())
        {
            results.Add(Describe(portName));
        }

        return results;
    }

    private static string[] SafeGetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private SerialPortInfo Describe(string portName)
    {
        string device = Path.GetFileName(portName);

        (int? vid, int? pid, string? name) = ReadUsbAttributes(device);

        // If sysfs gave no name, the /dev/serial/by-id link usually
        // carries the model and serial number in the file name itself.
        name ??= ReadByIdName(portName);

        return new SerialPortInfo
        {
            PortName = portName,
            FriendlyName = name,
            VendorId = vid,
            ProductId = pid,
        };
    }

    private (int? Vid, int? Pid, string? Name) ReadUsbAttributes(string device)
    {
        try
        {
            string deviceLink = Path.Combine(_sysClassTty, device, "device");
            if (!Directory.Exists(deviceLink) && !File.Exists(deviceLink))
            {
                return (null, null, null);
            }

            // The link must be resolved BEFORE walking up the tree.
            //
            // In sysfs, /sys/class/tty/<tty>/device is always a symbolic
            // link pointing somewhere under /sys/devices. Path.GetFullPath
            // only normalizes the string, it does not follow links, so
            // walking up from the link's own path would traverse
            // /sys/class/tty instead of the device tree, and would never
            // find idVendor.
            string? current = ResolveLink(deviceLink);

            // Walk up the tree until the USB node that has the attributes
            // is found. It is usually two or three levels up.

            for (int depth = 0; depth < 6 && current is not null; depth++)
            {
                string vidPath = Path.Combine(current, "idVendor");
                string pidPath = Path.Combine(current, "idProduct");

                if (File.Exists(vidPath) && File.Exists(pidPath))
                {
                    int? vid = ParseHex(ReadTrimmed(vidPath));
                    int? pid = ParseHex(ReadTrimmed(pidPath));

                    string? product = ReadTrimmed(Path.Combine(current, "product"));
                    string? manufacturer = ReadTrimmed(Path.Combine(current, "manufacturer"));

                    string? name = (manufacturer, product) switch
                    {
                        (not null, not null) => $"{manufacturer} {product}",
                        (null, not null) => product,
                        (not null, null) => manufacturer,
                        _ => null,
                    };

                    return (vid, pid, name);
                }

                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No permissions or sysfs not mounted. Continues without metadata.
        }

        return (null, null, null);
    }

    private string? ReadByIdName(string portName)
    {
        try
        {
            if (!Directory.Exists(_devSerialById))
            {
                return null;
            }

            foreach (string link in Directory.EnumerateFiles(_devSerialById))
            {
                string? target = new FileInfo(link).LinkTarget;

                if (target is null)
                {
                    continue;
                }

                string resolved = Path.GetFullPath(
                    Path.Combine(_devSerialById, target));

                // portName must go through the same normalization as resolved,
                // or the comparison never matches when GetFullPath rewrites a
                // rootless absolute path (like the tests' fake "/dev/ttyUSB0")
                // by prefixing it with the current drive.
                if (string.Equals(resolved, Path.GetFullPath(portName), StringComparison.Ordinal))
                {
                    // Names look like
                    // usb-Silicon_Labs_CP2102_USB_to_UART-if00-port0
                    return UnderscoreRuns()
                        .Replace(Path.GetFileName(link), " ")
                        .Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing wrong with missing /dev/serial/by-id, it is just an extra clue.
        }

        return null;
    }

    /// <summary>
    /// Resolves a symbolic link to its final target, as an absolute path.
    /// If it is not a link, returns the normalized path.
    /// </summary>
    private static string? ResolveLink(string path)
    {
        try
        {
            FileSystemInfo? target = Directory.Exists(path)
                ? new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)
                : new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);

            // ResolveLinkTarget returns null if it is not a link.
            return target?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ParseHex(string? text) =>
        int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    [GeneratedRegex("_+")]
    private static partial Regex UnderscoreRuns();
}
