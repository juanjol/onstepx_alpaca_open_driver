using OnStepX.Core.Discovery;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// Tests sysfs parsing against a synthetic tree that replicates Linux's
/// real structure.
/// </summary>
/// <remarks>
/// This is the only way to verify this code path: neither the development
/// machine nor the CI one have serial ports, and yet this is the path used
/// when deploying on a Raspberry Pi next to the mount.
/// </remarks>
public sealed class LinuxSerialPortEnumeratorTests : IDisposable
{
    private readonly string _root;
    private readonly string _sysClassTty;
    private readonly string _devSerialById;

    public LinuxSerialPortEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "onstepx-sysfs-" + Guid.NewGuid().ToString("N"));
        _sysClassTty = Path.Combine(_root, "sys", "class", "tty");
        _devSerialById = Path.Combine(_root, "dev", "serial", "by-id");

        Directory.CreateDirectory(_sysClassTty);
        Directory.CreateDirectory(_devSerialById);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    /// <summary>
    /// Builds the hierarchy the real kernel creates: the tty node points
    /// to the USB interface, and the idVendor and idProduct attributes
    /// are several levels up, on the USB device.
    /// </summary>
    private void CreateUsbSerialDevice(
        string ttyName,
        string vendorHex,
        string productHex,
        string? manufacturer = null,
        string? product = null,
        int depth = 3)
    {
        // The USB node, where the attributes live.
        string usbDevice = Path.Combine(_root, "sys", "devices", "usb1", ttyName + "-usb");
        Directory.CreateDirectory(usbDevice);

        File.WriteAllText(Path.Combine(usbDevice, "idVendor"), vendorHex + "\n");
        File.WriteAllText(Path.Combine(usbDevice, "idProduct"), productHex + "\n");

        if (manufacturer is not null)
        {
            File.WriteAllText(Path.Combine(usbDevice, "manufacturer"), manufacturer + "\n");
        }

        if (product is not null)
        {
            File.WriteAllText(Path.Combine(usbDevice, "product"), product + "\n");
        }

        // The chain of subdirectories separating the tty from the USB node.
        string current = usbDevice;
        for (int i = 0; i < depth; i++)
        {
            current = Path.Combine(current, $"level{i}");
            Directory.CreateDirectory(current);
        }

        // /sys/class/tty/<tty>/device points there.
        string ttyDir = Path.Combine(_sysClassTty, ttyName);
        Directory.CreateDirectory(ttyDir);
        Directory.CreateSymbolicLink(Path.Combine(ttyDir, "device"), current);
    }

    private LinuxSerialPortEnumerator Enumerator(params string[] portNames) =>
        new(_sysClassTty, _devSerialById, () => portNames);

    [Fact]
    public void VendorAndProductAreReadFromSysfs()
    {
        CreateUsbSerialDevice(
            "ttyUSB0", "10c4", "ea60", "Silicon Labs", "CP2102 USB to UART Bridge Controller");

        SerialPortInfo port = Assert.Single(
            Enumerator("/dev/ttyUSB0").Enumerate());

        Assert.Equal("/dev/ttyUSB0", port.PortName);
        Assert.Equal(PortRanking.Vendors.SiliconLabs, port.VendorId);
        Assert.Equal(0xEA60, port.ProductId);
    }

    [Fact]
    public void ManufacturerAndProductAreCombinedIntoTheFriendlyName()
    {
        CreateUsbSerialDevice("ttyUSB0", "1a86", "7523", "QinHeng", "CH340 serial converter");

        SerialPortInfo port = Assert.Single(Enumerator("/dev/ttyUSB0").Enumerate());

        Assert.Equal("QinHeng CH340 serial converter", port.FriendlyName);
    }

    [Fact]
    public void OnlyProductIsEnoughForTheFriendlyName()
    {
        CreateUsbSerialDevice("ttyUSB0", "0403", "6001", manufacturer: null, product: "FT232R USB UART");

        SerialPortInfo port = Assert.Single(Enumerator("/dev/ttyUSB0").Enumerate());

        Assert.Equal("FT232R USB UART", port.FriendlyName);
    }

    [Theory]
    // Attributes can be at different depths depending on the hub and the
    // driver. The tree must be walked up until they are found.
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void AttributesAreFoundAtAnyReasonableDepth(int depth)
    {
        CreateUsbSerialDevice("ttyUSB0", "10c4", "ea60", depth: depth);

        SerialPortInfo port = Assert.Single(Enumerator("/dev/ttyUSB0").Enumerate());

        Assert.Equal(PortRanking.Vendors.SiliconLabs, port.VendorId);
    }

    [Fact]
    public void APortWithNoSysfsEntryStillAppearsWithoutMetadata()
    {
        // A motherboard port has no USB node, but must still be listed.
        SerialPortInfo port = Assert.Single(Enumerator("/dev/ttyS0").Enumerate());

        Assert.Equal("/dev/ttyS0", port.PortName);
        Assert.Null(port.VendorId);
        Assert.Null(port.FriendlyName);
    }

    [Fact]
    public void MalformedHexAttributesDoNotThrow()
    {
        string usbDevice = Path.Combine(_root, "sys", "devices", "usb1", "raro");
        Directory.CreateDirectory(usbDevice);
        File.WriteAllText(Path.Combine(usbDevice, "idVendor"), "notHex\n");
        File.WriteAllText(Path.Combine(usbDevice, "idProduct"), "neither\n");

        string ttyDir = Path.Combine(_sysClassTty, "ttyUSB0");
        Directory.CreateDirectory(ttyDir);
        Directory.CreateSymbolicLink(Path.Combine(ttyDir, "device"), usbDevice);

        SerialPortInfo port = Assert.Single(Enumerator("/dev/ttyUSB0").Enumerate());

        Assert.Null(port.VendorId);
        Assert.Null(port.ProductId);
    }

    [Fact]
    public void SeveralDevicesAreEnumeratedIndependently()
    {
        CreateUsbSerialDevice("ttyUSB0", "10c4", "ea60", "Silicon Labs", "CP2102");
        CreateUsbSerialDevice("ttyUSB1", "1a86", "7523", "QinHeng", "CH340");

        IReadOnlyList<SerialPortInfo> ports =
            Enumerator("/dev/ttyUSB0", "/dev/ttyUSB1").Enumerate();

        Assert.Equal(2, ports.Count);
        Assert.Equal(PortRanking.Vendors.SiliconLabs, ports[0].VendorId);
        Assert.Equal(PortRanking.Vendors.Wch, ports[1].VendorId);
    }

    [Fact]
    public void ByIdLinkProvidesTheNameWhenSysfsHasNone()
    {
        // With no readable USB node, the name of the /dev/serial/by-id
        // link is the only clue, and it carries the model.
        string link = Path.Combine(
            _devSerialById, "usb-Silicon_Labs_CP2102_USB_to_UART_Bridge-if00-port0");

        File.CreateSymbolicLink(link, "/dev/ttyUSB0");

        SerialPortInfo port = Assert.Single(Enumerator("/dev/ttyUSB0").Enumerate());

        Assert.NotNull(port.FriendlyName);
        Assert.Contains("Silicon Labs", port.FriendlyName, StringComparison.Ordinal);
        Assert.Contains("CP2102", port.FriendlyName, StringComparison.Ordinal);

        // And with that name, ranking already prefers it.
        Assert.True(PortRanking.Rank(port).Priority > PortRanking.Rank(
            new SerialPortInfo { PortName = "/dev/ttyUSB1" }).Priority);
    }

    [Fact]
    public void SysfsMetadataTakesPrecedenceOverTheByIdName()
    {
        CreateUsbSerialDevice("ttyUSB0", "10c4", "ea60", "Silicon Labs", "CP2102");

        File.CreateSymbolicLink(
            Path.Combine(_devSerialById, "usb-Other_Name-if00"),
            "/dev/ttyUSB0");

        SerialPortInfo port = Assert.Single(Enumerator("/dev/ttyUSB0").Enumerate());

        Assert.Equal("Silicon Labs CP2102", port.FriendlyName);
    }

    [Fact]
    public void MissingDirectoriesAreHandledCleanly()
    {
        var enumerator = new LinuxSerialPortEnumerator(
            Path.Combine(_root, "does", "not", "exist"),
            Path.Combine(_root, "missing"),
            () => ["/dev/ttyUSB0"]);

        SerialPortInfo port = Assert.Single(enumerator.Enumerate());

        Assert.Equal("/dev/ttyUSB0", port.PortName);
        Assert.Null(port.VendorId);
    }

    [Fact]
    public void NoPortsYieldsAnEmptyList()
    {
        Assert.Empty(Enumerator().Enumerate());
    }

    [Fact]
    public void TheFullFlowRanksARealisticRaspberryPiSetup()
    {
        // Deployment scenario: a Pi with the mount on a CP2102, a
        // motherboard port, and a serial console.
        CreateUsbSerialDevice("ttyUSB0", "10c4", "ea60", "Silicon Labs", "CP2102 USB to UART");

        IReadOnlyList<SerialPortInfo> ranked = PortRanking.Prioritise(
            Enumerator("/dev/ttyUSB0", "/dev/ttyS0", "/dev/ttyAMA0").Enumerate());

        // The CP2102 must come out first, and the other two behind it.
        Assert.Equal("/dev/ttyUSB0", ranked[0].PortName);
        Assert.Equal(3, ranked.Count);
    }
}
