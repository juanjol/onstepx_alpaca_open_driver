using System.Globalization;
using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace OnStepX.Core.Discovery;

/// <summary>
/// Enumerates serial ports on Windows, enriched with WMI.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SerialPort.GetPortNames"/> only gives <c>COM7</c>, with no
/// hint of what is behind it. WMI, with the <c>Win32_PnPEntity</c> class,
/// provides the friendly name and the device identifier, from which the
/// VID and the PID are extracted. That is what allows the candidates to be
/// ranked before probing them, and in particular to set aside Bluetooth
/// ports, which block when opened.
/// </para>
/// <para>
/// If WMI fails, due to permissions or because the service is stopped, it
/// degrades to the list of names with no metadata instead of leaving
/// discovery unusable.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSerialPortEnumerator : ISerialPortEnumerator
{
    /// <inheritdoc />
    public string Description => "WMI Win32_PnPEntity";

    /// <inheritdoc />
    public IReadOnlyList<SerialPortInfo> Enumerate()
    {
        string[] names = SafeGetPortNames();
        Dictionary<string, (string? Name, int? Vid, int? Pid)> metadata = QueryWmi();

        var results = new List<SerialPortInfo>(names.Length);

        foreach (string portName in names)
        {
            metadata.TryGetValue(portName, out var meta);

            results.Add(new SerialPortInfo
            {
                PortName = portName,
                FriendlyName = meta.Name,
                VendorId = meta.Vid,
                ProductId = meta.Pid,
            });
        }

        // WMI may know about ports that GetPortNames does not return.
        foreach ((string portName, var meta) in metadata)
        {
            if (!names.Contains(portName, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(new SerialPortInfo
                {
                    PortName = portName,
                    FriendlyName = meta.Name,
                    VendorId = meta.Vid,
                    ProductId = meta.Pid,
                });
            }
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

    private static Dictionary<string, (string? Name, int? Vid, int? Pid)> QueryWmi()
    {
        var result = new Dictionary<string, (string?, int?, int?)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Accessed via reflection on purpose so OnStepX.Core stays
            // cross platform without dragging System.Management in as a
            // mandatory dependency on Linux.
            Type? searcherType = Type.GetType(
                "System.Management.ManagementObjectSearcher, System.Management");

            if (searcherType is null)
            {
                return result;
            }

            object? searcher = Activator.CreateInstance(
                searcherType,
                "SELECT Name, Caption, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            if (searcher is null)
            {
                return result;
            }

            object? collection = searcherType.GetMethod("Get", Type.EmptyTypes)?.Invoke(searcher, null);

            if (collection is not System.Collections.IEnumerable items)
            {
                return result;
            }

            foreach (object item in items)
            {
                Type itemType = item.GetType();

                string? name = GetProperty(itemType, item, "Name")
                    ?? GetProperty(itemType, item, "Caption");
                string? deviceId = GetProperty(itemType, item, "DeviceID");

                if (name is null)
                {
                    continue;
                }

                Match portMatch = ComPortInName().Match(name);
                if (!portMatch.Success)
                {
                    continue;
                }

                string portName = portMatch.Groups[1].Value;

                (int? vid, int? pid) = ParseVidPid(deviceId);

                result[portName] = (name, vid, pid);
            }
        }
        catch (Exception)
        {
            // WMI is a luxury, not a requirement. Without it, discovery
            // still works, even if it has to probe in a worse order.
        }

        return result;
    }

    private static string? GetProperty(Type type, object item, string property)
    {
        try
        {
            // ManagementObject exposes its fields via an indexer.
            object? value = type.GetProperty("Item")?.GetValue(item, [property]);

            return value?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (int? Vid, int? Pid) ParseVidPid(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return (null, null);
        }

        Match match = VidPidInDeviceId().Match(deviceId);

        if (!match.Success)
        {
            return (null, null);
        }

        int? vid = ParseHex(match.Groups[1].Value);
        int? pid = ParseHex(match.Groups[2].Value);

        return (vid, pid);
    }

    private static int? ParseHex(string text) =>
        int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComPortInName();

    [GeneratedRegex(@"VID_([0-9A-F]{4}).*?PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidInDeviceId();
}

/// <summary>
/// Fallback enumerator: names only, no metadata.
/// </summary>
public sealed class BasicSerialPortEnumerator : ISerialPortEnumerator
{
    /// <inheritdoc />
    public string Description => "SerialPort.GetPortNames";

    /// <inheritdoc />
    public IReadOnlyList<SerialPortInfo> Enumerate()
    {
        try
        {
            return SerialPort.GetPortNames()
                .Select(p => new SerialPortInfo { PortName = p })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// Chooses the right enumerator for the current system.
/// </summary>
public static class SerialPortEnumerators
{
    /// <summary>Creates the enumerator that matches this platform.</summary>
    public static ISerialPortEnumerator CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSerialPortEnumerator();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxSerialPortEnumerator();
        }

        return new BasicSerialPortEnumerator();
    }
}
