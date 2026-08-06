namespace OnStepX.Core.Discovery;

/// <summary>
/// Sorts and filters candidate ports <b>before</b> probing them.
/// </summary>
/// <remarks>
/// Blindly probing every port is slow and, worse, risky: opening a
/// Bluetooth serial port can block for several seconds or trigger a
/// pairing, and there are virtual modems that never respond. Classifying
/// by USB identifier before touching anything turns discovery into
/// something fast and predictable.
/// </remarks>
public static class PortRanking
{
    /// <summary>
    /// Vendor identifiers of the USB to serial bridges that OnStepX uses.
    /// </summary>
    public static class Vendors
    {
        /// <summary>Silicon Labs, the CP210x.</summary>
        public const int SiliconLabs = 0x10C4;

        /// <summary>WCH, the CH340 and CH341.</summary>
        public const int Wch = 0x1A86;

        /// <summary>FTDI, the FT232 and similar.</summary>
        public const int Ftdi = 0x0403;

        /// <summary>Prolific, the PL2303.</summary>
        public const int Prolific = 0x067B;

        /// <summary>PJRC, the Teensy.</summary>
        public const int Pjrc = 0x16C0;

        /// <summary>Espressif, the ESP32 with native USB.</summary>
        public const int Espressif = 0x303A;

        /// <summary>Arduino.</summary>
        public const int Arduino = 0x2341;

        /// <summary>STMicroelectronics, the STM32 with native USB.</summary>
        public const int StMicro = 0x0483;

        /// <summary>Raspberry Pi, the RP2040.</summary>
        public const int RaspberryPi = 0x2E8A;
    }

    /// <summary>Known vendors, in order of preference.</summary>
    private static readonly int[] PreferredVendors =
    [
        Vendors.SiliconLabs,
        Vendors.Wch,
        Vendors.Ftdi,
        Vendors.Pjrc,
        Vendors.Espressif,
        Vendors.StMicro,
        Vendors.RaspberryPi,
        Vendors.Arduino,
        Vendors.Prolific,
    ];

    /// <summary>
    /// Name fragments that give away a port that should not be probed.
    /// </summary>
    private static readonly string[] ExcludedNameFragments =
    [
        "bluetooth",
        "bthenum",
        "rfcomm",
        "standard serial over",
        "modem",
        "fax",
        "irda",
        "virtual infrared",
        "printer",
    ];

    /// <summary>
    /// Name fragments that raise the priority, for when there is no VID.
    /// </summary>
    private static readonly string[] PreferredNameFragments =
    [
        "cp210",
        "ch340",
        "ch341",
        "ftdi",
        "usb serial",
        "usb-serial",
        "teensy",
        "esp32",
        "silicon labs",
        "onstep",
    ];

    /// <summary>
    /// Scores and filters a port.
    /// </summary>
    public static SerialPortInfo Rank(SerialPortInfo port)
    {
        ArgumentNullException.ThrowIfNull(port);

        string haystack = (port.FriendlyName ?? string.Empty).ToLowerInvariant();

        foreach (string fragment in ExcludedNameFragments)
        {
            if (haystack.Contains(fragment, StringComparison.Ordinal))
            {
                return port with
                {
                    Priority = int.MinValue,
                    ExcludedReason =
                        $"excluded by name, contains \"{fragment}\", opening " +
                        "these ports tends to block",
                };
            }
        }

        int priority = 0;

        // The vendor identifier is the most reliable signal.
        if (port.VendorId is int vid)
        {
            int index = Array.IndexOf(PreferredVendors, vid);
            priority += index >= 0 ? 1000 - (index * 10) : 100;
        }

        // The name is only used as reinforcement, or as the only clue on
        // Linux when the VID could not be read.
        foreach (string fragment in PreferredNameFragments)
        {
            if (haystack.Contains(fragment, StringComparison.Ordinal))
            {
                priority += 50;
                break;
            }
        }

        // On Linux, ttyUSB and ttyACM are genuine USB to serial bridges,
        // while ttyS0 and its kin are usually nonexistent motherboard ports.
        if (port.PortName.Contains("ttyUSB", StringComparison.Ordinal)
            || port.PortName.Contains("ttyACM", StringComparison.Ordinal))
        {
            priority += 200;
        }
        else if (port.PortName.Contains("ttyS", StringComparison.Ordinal))
        {
            priority -= 100;
        }

        return port with { Priority = priority };
    }

    /// <summary>
    /// Scores every port and returns only the probeable ones, from highest
    /// to lowest priority. The order is stable so the result is reproducible.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> Prioritise(IEnumerable<SerialPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        return ports
            .Select(Rank)
            .Where(p => p.IsCandidate)
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Scores every port and also returns the excluded ones, so the UI can
    /// show why each one was ignored.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> RankAll(IEnumerable<SerialPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        return ports
            .Select(Rank)
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
