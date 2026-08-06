using OnStepX.Core.Discovery;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// Ranking decides which ports get opened and in what order. It is the
/// part of autodiscovery that avoids blocking: opening a Bluetooth port
/// can hang for several seconds or trigger a pairing.
/// </summary>
public class PortRankingTests
{
    private static SerialPortInfo Port(string name, string? friendly = null, int? vid = null) =>
        new() { PortName = name, FriendlyName = friendly, VendorId = vid };

    [Theory]
    [InlineData("Standard Serial over Bluetooth link (COM5)")]
    [InlineData("Bluetooth Device (COM9)")]
    [InlineData("BthEnum Serial Port (COM12)")]
    [InlineData("RFCOMM Communications Port (COM3)")]
    [InlineData("Fax Modem (COM4)")]
    [InlineData("IrDA Serial Port (COM6)")]
    public void PortsThatBlockOnOpenAreExcludedBeforeBeingTouched(string friendlyName)
    {
        SerialPortInfo ranked = PortRanking.Rank(Port("COM5", friendlyName));

        Assert.False(ranked.IsCandidate);
        Assert.NotNull(ranked.ExcludedReason);
        Assert.Contains("block", ranked.ExcludedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExclusionIsCaseInsensitive()
    {
        Assert.False(PortRanking.Rank(Port("COM5", "STANDARD SERIAL OVER BLUETOOTH")).IsCandidate);
        Assert.False(PortRanking.Rank(Port("COM5", "bluetooth")).IsCandidate);
    }

    [Fact]
    public void KnownUsbBridgesOutrankUnknownDevices()
    {
        var cp210x = PortRanking.Rank(Port("COM7", "CP210x USB to UART", PortRanking.Vendors.SiliconLabs));
        var unknown = PortRanking.Rank(Port("COM8", "Unknown device", 0x1234));

        Assert.True(cp210x.Priority > unknown.Priority);
    }

    [Theory]
    [InlineData(0x10C4)] // Silicon Labs, CP210x
    [InlineData(0x1A86)] // WCH, CH340
    [InlineData(0x0403)] // FTDI
    [InlineData(0x16C0)] // PJRC, Teensy
    [InlineData(0x303A)] // Espressif
    public void EveryBridgeOnStepXActuallyUsesIsPreferred(int vendorId)
    {
        var known = PortRanking.Rank(Port("COM7", "USB Serial", vendorId));
        var noVendor = PortRanking.Rank(Port("COM8", "USB Serial"));

        Assert.True(
            known.Priority > noVendor.Priority,
            $"vendor 0x{vendorId:X4} should be preferred");
    }

    [Fact]
    public void SiliconLabsIsPreferredOverProlific()
    {
        // CP210x is the most common bridge on OnStepX, PL2303 is rarely
        // seen and also has problematic drivers.
        var cp210x = PortRanking.Rank(Port("COM7", null, PortRanking.Vendors.SiliconLabs));
        var pl2303 = PortRanking.Rank(Port("COM8", null, PortRanking.Vendors.Prolific));

        Assert.True(cp210x.Priority > pl2303.Priority);
    }

    [Fact]
    public void FriendlyNameHelpsWhenNoVendorIdIsAvailable()
    {
        // On Linux without permissions over sysfs, only the name is left.
        var named = PortRanking.Rank(Port("/dev/ttyUSB0", "Silicon Labs CP2102 USB to UART"));
        var plain = PortRanking.Rank(Port("/dev/ttyUSB1"));

        Assert.True(named.Priority > plain.Priority);
    }

    [Fact]
    public void LinuxUsbSerialDevicesOutrankMotherboardPorts()
    {
        // ttyS0 is usually a motherboard port that does not physically
        // exist, and probing it is wasted time.
        var usb = PortRanking.Rank(Port("/dev/ttyUSB0"));
        var acm = PortRanking.Rank(Port("/dev/ttyACM0"));
        var legacy = PortRanking.Rank(Port("/dev/ttyS0"));

        Assert.True(usb.Priority > legacy.Priority);
        Assert.True(acm.Priority > legacy.Priority);
    }

    [Fact]
    public void PrioritiseDropsExcludedPortsAndSortsByPriority()
    {
        SerialPortInfo[] ports =
        [
            Port("COM3", "Standard Serial over Bluetooth link"),
            Port("COM4"),
            Port("COM7", "CP210x USB to UART", PortRanking.Vendors.SiliconLabs),
            Port("COM9", "USB Serial", PortRanking.Vendors.Wch),
        ];

        IReadOnlyList<SerialPortInfo> result = PortRanking.Prioritise(ports);

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, p => p.PortName == "COM3");
        Assert.Equal("COM7", result[0].PortName);
        Assert.Equal("COM9", result[1].PortName);
        Assert.Equal("COM4", result[2].PortName);
    }

    [Fact]
    public void RankAllKeepsExcludedPortsSoTheUiCanExplainWhy()
    {
        SerialPortInfo[] ports =
        [
            Port("COM3", "Standard Serial over Bluetooth link"),
            Port("COM7", "CP210x", PortRanking.Vendors.SiliconLabs),
        ];

        IReadOnlyList<SerialPortInfo> result = PortRanking.RankAll(ports);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.PortName == "COM3" && !p.IsCandidate);
    }

    [Fact]
    public void OrderIsStableAndReproducible()
    {
        // Two ports with the same score must always come out in the same
        // order, or autodiscovery would give different results between runs.
        SerialPortInfo[] ports = [Port("COM9"), Port("COM4"), Port("COM7")];

        IReadOnlyList<SerialPortInfo> first = PortRanking.Prioritise(ports);
        IReadOnlyList<SerialPortInfo> second = PortRanking.Prioritise(ports.Reverse());

        Assert.Equal(
            first.Select(p => p.PortName),
            second.Select(p => p.PortName));
    }

    [Fact]
    public void EmptyInputYieldsEmptyOutput()
    {
        Assert.Empty(PortRanking.Prioritise([]));
        Assert.Empty(PortRanking.RankAll([]));
    }
}
