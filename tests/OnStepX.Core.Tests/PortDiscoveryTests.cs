using OnStepX.Core.Discovery;
using OnStepX.Core.Simulation;
using OnStepX.Core.Transport;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>Scripted enumerator, so it does not depend on the machine's hardware.</summary>
internal sealed class FakeEnumerator(params SerialPortInfo[] ports) : ISerialPortEnumerator
{
    public string Description => "scripted enumerator";

    public IReadOnlyList<SerialPortInfo> Enumerate() => ports;
}

/// <summary>Transport that fails to open, like an already busy port.</summary>
internal sealed class UnopenableTransport(string description, Exception failure) : ITransport
{
    public string Description => description;

    public bool IsOpen => false;

    public ValueTask OpenAsync(CancellationToken cancellationToken = default) => throw failure;

    public ValueTask CloseAsync() => ValueTask.CompletedTask;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        throw failure;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        throw failure;

    public void DiscardInputBuffer()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Transport that opens but never answers, like a virtual modem.</summary>
internal sealed class SilentTransport(string description) : ITransport
{
    public string Description => description;

    public bool IsOpen { get; private set; }

    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        IsOpen = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        // Indefinite silence, until the probe deadline cuts it off.
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return 0;
    }

    public void DiscardInputBuffer()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Serial-like transport that can be retuned while open and only answers at
/// one speed, the way a real controller does.
/// </summary>
internal sealed class RetunableTransport(string description, int answersAt) : ITransport
{
    private readonly FakeOnStepDevice _device = new();

    private int _baudRate;

    /// <summary>Opens, which on real hardware is one board reset each.</summary>
    public int OpenCount { get; private set; }

    public string Description => description;

    public bool IsOpen { get; private set; }

    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        OpenCount++;
        IsOpen = true;
        await _device.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CloseAsync()
    {
        IsOpen = false;
        return _device.CloseAsync();
    }

    public bool TrySetBaudRate(int baudRate)
    {
        _baudRate = baudRate;
        return true;
    }

    /// <summary>Speed each write went out at, so retries per speed are visible.</summary>
    public List<int> WriteBauds { get; } = [];

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        WriteBauds.Add(_baudRate);

        return _baudRate == answersAt ? _device.WriteAsync(data, ct) : ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_baudRate != answersAt)
        {
            // Silence at the wrong speed, until the probe deadline cuts it off.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }

        return await _device.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    public void DiscardInputBuffer() => _device.DiscardInputBuffer();

    public ValueTask DisposeAsync() => _device.DisposeAsync();
}

/// <summary>Transport that returns garbage, like the wrong baud rate.</summary>
internal sealed class GarbageTransport(string description, string garbage) : ITransport
{
    private readonly Queue<byte> _pending = new();

    public string Description => description;

    public bool IsOpen { get; private set; }

    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        IsOpen = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(garbage))
        {
            _pending.Enqueue(b);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_pending.Count == 0)
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
        }

        int count = Math.Min(_pending.Count, buffer.Length);
        for (int i = 0; i < count; i++)
        {
            buffer.Span[i] = _pending.Dequeue();
        }

        return count;
    }

    public void DiscardInputBuffer() => _pending.Clear();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class PortDiscoveryTests
{
    private static PortDiscoveryOptions FastOptions(
        bool stopAtFirst = false,
        int[]? bauds = null) => new()
        {
            PreferredBaudRate = 9600,
            BaudRates = bauds ?? [9600, 19200],
            ProbeTimeout = TimeSpan.FromMilliseconds(150),
            MaxConcurrency = 2,
            StopAtFirstMatch = stopAtFirst,
        };

    private static SerialPortInfo Port(string name, string? friendly = null, int? vid = null) =>
        new() { PortName = name, FriendlyName = friendly, VendorId = vid };

    [Fact]
    public async Task TheSpeedSweepRetunesOnePortInsteadOfReopeningItPerSpeed()
    {
        // The regression this exists for: the sweep used to reopen the port for
        // every speed in the list. Each reopen pulses DTR and RTS, which resets
        // the boards wiring those to EN and GPIO0, so the controller was thrown
        // back into booting before it could answer and the sweep concluded
        // there was nothing there, at any speed, while the board audibly
        // restarted throughout.
        var enumerator = new FakeEnumerator(Port("COM7"));

        // Every transport built is kept, not just the last: counting only the
        // last one would pass just as well against the reopening behaviour
        // this test exists to rule out.
        var created = new List<RetunableTransport>();

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) =>
            {
                var transport = new RetunableTransport(port, answersAt: 115200);
                created.Add(transport);

                return transport;
            });

        var options = new PortDiscoveryOptions
        {
            // Deliberately the wrong preferred speed, so the sweep has to walk
            // past two before reaching the one the controller uses.
            PreferredBaudRate = 9600,
            BaudRates = [9600, 19200, 115200],
            ProbeTimeout = TimeSpan.FromMilliseconds(100),
            MaxConcurrency = 1,
        };

        IReadOnlyList<DiscoveredController> found = await discovery.DiscoverAsync(options);

        DiscoveredController controller = Assert.Single(found);
        Assert.Equal(115200, controller.BaudRate);

        // Three speeds tried, one port built and opened once: one board reset
        // for the whole sweep instead of one per speed.
        RetunableTransport only = Assert.Single(created);
        Assert.Equal(1, only.OpenCount);
    }

    [Fact]
    public async Task OnlyTheFirstSpeedPaysForTheBoardBooting()
    {
        // The retry covers a board still booting after the open reset it. A
        // sweep runs over one open port, and retuning has been confirmed on
        // CH340 hardware not to reset the board, so the boot happens at the
        // open and nowhere else. Retrying at every later speed would double
        // the time spent ruling each wrong one out, and that time is what a
        // client's connect timeout runs out of.
        var enumerator = new FakeEnumerator(Port("COM7"));

        var created = new List<RetunableTransport>();

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) =>
            {
                var transport = new RetunableTransport(port, answersAt: 115200);
                created.Add(transport);

                return transport;
            });

        var options = new PortDiscoveryOptions
        {
            PreferredBaudRate = 9600,
            BaudRates = [9600, 19200, 38400, 115200],
            ProbeTimeout = TimeSpan.FromMilliseconds(100),
            MaxConcurrency = 1,
        };

        IReadOnlyList<DiscoveredController> found = await discovery.DiscoverAsync(options);

        Assert.Single(found);

        RetunableTransport only = Assert.Single(created);

        // Two tries at the first speed, one of them the boot allowance. One
        // try each at the speeds after it, because the board is up by then.
        Assert.Equal(2, only.WriteBauds.Count(b => b == 9600));
        Assert.Equal(1, only.WriteBauds.Count(b => b == 19200));
        Assert.Equal(1, only.WriteBauds.Count(b => b == 38400));
    }

    [Fact]
    public async Task ConnectingOpensTheConfiguredPortOnceAndHandsItBackOpen()
    {
        // The regression this exists for: discovery used to close the port it had
        // just talked to, and connecting reopened it. That second open pulses DTR
        // and RTS, resetting the board, so the identity read that came next timed
        // out against a controller busy booting.
        var enumerator = new FakeEnumerator(
            Port("COM7", "CP210x", PortRanking.Vendors.SiliconLabs));

        CountingTransport? opened = null;

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => opened = new CountingTransport());

        await using DiscoveredConnection? connection =
            await discovery.ConnectAsync("COM7", 9600, FastOptions());

        Assert.NotNull(connection);
        Assert.Equal("COM7", connection.Controller.PortName);
        Assert.Equal("On-Step", connection.Controller.ProductName);

        // Still open, and opened exactly once: one connect, one board reset.
        Assert.True(connection.Transport.IsOpen);
        Assert.Equal(1, opened!.OpenCount);
        Assert.Equal(0, opened.DisposeCount);
    }

    [Fact]
    public async Task DisposingTheConnectionClosesThePort()
    {
        var enumerator = new FakeEnumerator(Port("COM7"));

        CountingTransport? opened = null;

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => opened = new CountingTransport());

        DiscoveredConnection? connection =
            await discovery.ConnectAsync("COM7", 9600, FastOptions());

        Assert.NotNull(connection);
        await connection.DisposeAsync();

        Assert.Equal(1, opened!.DisposeCount);
    }

    [Fact]
    public async Task ListingStillClosesEveryPortItOpens()
    {
        // Only connecting keeps a port open. Listing must not leave the
        // controller held, or the setup page would lock out the driver.
        var enumerator = new FakeEnumerator(Port("COM7"));

        CountingTransport? opened = null;

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => opened = new CountingTransport());

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(FastOptions());

        Assert.Single(found);
        Assert.Equal(1, opened!.DisposeCount);
        Assert.False(opened.IsOpen);
    }

    [Fact]
    public async Task ConnectingClosesThePortsThatDidNotAnswer()
    {
        // The configured port is wrong, so the sweep finds the controller
        // elsewhere. The silent port must not be left held open.
        var enumerator = new FakeEnumerator(
            Port("COM3"),
            Port("COM7", "CP210x", PortRanking.Vendors.SiliconLabs));

        var transports = new Dictionary<string, CountingTransport>();

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => transports[port] = new CountingTransport(
                port == "COM7" ? new FakeOnStepDevice() : new SilentTransport(port)));

        await using DiscoveredConnection? connection =
            await discovery.ConnectAsync("COM3", 9600, FastOptions(stopAtFirst: true));

        Assert.NotNull(connection);
        Assert.Equal("COM7", connection.Controller.PortName);
        Assert.True(connection.Transport.IsOpen);

        // The one that answered stays open, the one that did not is closed.
        Assert.Equal(0, transports["COM7"].DisposeCount);
        Assert.True(transports["COM3"].DisposeCount > 0);
    }

    [Fact]
    public async Task FindsTheControllerOnAnArbitraryPort()
    {
        var enumerator = new FakeEnumerator(
            Port("COM3"),
            Port("COM7", "CP210x USB to UART", PortRanking.Vendors.SiliconLabs));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => port == "COM7"
                ? new FakeOnStepDevice()
                : new SilentTransport(port));

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(FastOptions());

        DiscoveredController controller = Assert.Single(found);
        Assert.Equal("COM7", controller.PortName);
        Assert.Equal("On-Step", controller.ProductName);
        Assert.Equal("10.21b", controller.FirmwareVersion);
        Assert.Equal(9600, controller.BaudRate);
    }

    [Fact]
    public async Task AnotherUsbSerialDevicePresentDoesNotConfuseTheSearch()
    {
        // Common scenario: a USB serial camera or keyboard is plugged in.
        var enumerator = new FakeEnumerator(
            Port("COM4", "USB Serial Device", PortRanking.Vendors.Ftdi),
            Port("COM7", "CP210x", PortRanking.Vendors.SiliconLabs));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => port == "COM7"
                ? new FakeOnStepDevice()
                : new GarbageTransport(port, "whatever#"));

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(FastOptions());

        DiscoveredController controller = Assert.Single(found);
        Assert.Equal("COM7", controller.PortName);
    }

    [Fact]
    public async Task APortAlreadyOpenByAnotherProgramIsSkippedWithoutAbortingTheSearch()
    {
        var enumerator = new FakeEnumerator(
            Port("COM3", "CP210x", PortRanking.Vendors.SiliconLabs),
            Port("COM7", "CH340", PortRanking.Vendors.Wch));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => port == "COM3"
                ? new UnopenableTransport(port, new UnauthorizedAccessException("port busy"))
                : new FakeOnStepDevice());

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(FastOptions());

        DiscoveredController controller = Assert.Single(found);
        Assert.Equal("COM7", controller.PortName);
    }

    [Fact]
    public async Task ASilentPortDoesNotHangTheSearch()
    {
        var enumerator = new FakeEnumerator(Port("COM3"), Port("COM7"));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => port == "COM7"
                ? new FakeOnStepDevice()
                : new SilentTransport(port));

        var options = FastOptions();
        DateTime start = DateTime.UtcNow;

        IReadOnlyList<DiscoveredController> found = await discovery.DiscoverAsync(options);

        TimeSpan elapsed = DateTime.UtcNow - start;

        Assert.Single(found);

        // Two baud rates per silent port, with a wide margin so it does
        // not become flaky in CI. What is being checked is that there is a
        // deadline and not an indefinite block.
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed}");
    }

    [Fact]
    public async Task GarbageAtTheWrongBaudRateIsRejectedByTheSecondCommand()
    {
        // A response that could pass for a plausible product name, but
        // whose version is not. The confirmation with :GVN# is what rules
        // it out.
        var enumerator = new FakeEnumerator(Port("COM3"));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => new GarbageTransport(port, "OnStep#garbage#"));

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(FastOptions());

        Assert.Empty(found);
    }

    [Fact]
    public async Task FindsSeveralControllersAndReturnsThemAllInPriorityOrder()
    {
        // With two mounts connected, the user must be able to choose, so
        // it is not acceptable to return only the first one.
        var enumerator = new FakeEnumerator(
            Port("COM9", "CH340", PortRanking.Vendors.Wch),
            Port("COM7", "CP210x", PortRanking.Vendors.SiliconLabs));

        var discovery = new PortDiscovery(enumerator, (port, baud) => new FakeOnStepDevice());

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(FastOptions());

        Assert.Equal(2, found.Count);

        // Silicon Labs has higher priority than WCH, and the order is
        // restored after concurrent execution.
        Assert.Equal("COM7", found[0].PortName);
        Assert.Equal("COM9", found[1].PortName);
    }

    [Fact]
    public async Task StopAtFirstMatchReturnsAsSoonAsSomethingAnswers()
    {
        var enumerator = new FakeEnumerator(
            Port("COM7", "CP210x", PortRanking.Vendors.SiliconLabs),
            Port("COM9", "CH340", PortRanking.Vendors.Wch));

        var discovery = new PortDiscovery(enumerator, (port, baud) => new FakeOnStepDevice());

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(FastOptions(stopAtFirst: true));

        Assert.NotEmpty(found);
    }

    [Fact]
    public async Task StopAtFirstMatchWithMorePortsThanConcurrencySlotsStillReturnsTheMatch()
    {
        // Scenario that genuinely broke: when the first one is found the
        // linked source is cancelled, and the ports still waiting their
        // turn on the semaphore saw their wait cancelled. If that
        // cancellation escapes the lambda, Task.WhenAll rethrows it and
        // DiscoverAsync throws instead of returning what it had already
        // found. This is the normal case when connecting on a machine
        // with several COM ports.
        var enumerator = new FakeEnumerator(
            Port("COM3", "CP210x", PortRanking.Vendors.SiliconLabs),
            Port("COM4", "CH340", PortRanking.Vendors.Wch),
            Port("COM5", "FTDI", PortRanking.Vendors.Ftdi),
            Port("COM6", "USB Serial"),
            Port("COM7", "USB Serial"),
            Port("COM8", "USB Serial"));

        var discovery = new PortDiscovery(enumerator, (port, baud) => new FakeOnStepDevice());

        var options = new PortDiscoveryOptions
        {
            PreferredBaudRate = 9600,
            BaudRates = [9600],
            ProbeTimeout = TimeSpan.FromMilliseconds(150),

            // A single slot, six ports: five stay waiting for their turn.
            MaxConcurrency = 1,
            StopAtFirstMatch = true,
        };

        IReadOnlyList<DiscoveredController> found = await discovery.DiscoverAsync(options);

        Assert.NotEmpty(found);
        Assert.Equal("COM3", found[0].PortName);
    }

    [Fact]
    public async Task ThePreferredBaudRateIsTriedFirst()
    {
        var attempts = new List<int>();
        var enumerator = new FakeEnumerator(Port("COM7"));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) =>
            {
                attempts.Add(baud);
                return baud == 115200 ? new FakeOnStepDevice() : new SilentTransport(port);
            });

        var options = new PortDiscoveryOptions
        {
            PreferredBaudRate = 115200,
            BaudRates = [9600, 19200, 115200],
            ProbeTimeout = TimeSpan.FromMilliseconds(100),
            MaxConcurrency = 1,
        };

        IReadOnlyList<DiscoveredController> found = await discovery.DiscoverAsync(options);

        Assert.Single(found);
        Assert.Equal(115200, attempts[0]);

        // And when it hits on the first try, the others are not tried.
        Assert.Single(attempts);
    }

    [Fact]
    public async Task TheBaudSweepReachesTheRateTheMountActuallyUses()
    {
        var enumerator = new FakeEnumerator(Port("COM7"));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) => baud == 19200 ? new FakeOnStepDevice() : new SilentTransport(port));

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(FastOptions());

        DiscoveredController controller = Assert.Single(found);
        Assert.Equal(19200, controller.BaudRate);
    }

    [Fact]
    public async Task ExcludedPortsAreNeverOpened()
    {
        var opened = new List<string>();
        var enumerator = new FakeEnumerator(Port("COM3"), Port("COM7"));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) =>
            {
                opened.Add(port);
                return new FakeOnStepDevice();
            });

        var options = FastOptions() with { ExcludedPorts = ["COM3"] };

        await discovery.DiscoverAsync(options);

        Assert.DoesNotContain("COM3", opened);
        Assert.Contains("COM7", opened);
    }

    [Fact]
    public async Task BluetoothPortsAreNeverOpenedEvenIfTheyAreTheOnlyOnes()
    {
        var opened = new List<string>();
        var enumerator = new FakeEnumerator(
            Port("COM5", "Standard Serial over Bluetooth link"));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) =>
            {
                opened.Add(port);
                return new FakeOnStepDevice();
            });

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(FastOptions());

        Assert.Empty(found);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task NoPortsAtAllIsHandledCleanly()
    {
        var discovery = new PortDiscovery(new FakeEnumerator(), (port, baud) => new FakeOnStepDevice());

        Assert.Empty(await discovery.DiscoverAsync(FastOptions()));
    }

    [Fact]
    public async Task ProgressIsReported()
    {
        var reports = new List<PortDiscoveryProgress>();
        var enumerator = new FakeEnumerator(Port("COM3"), Port("COM7"));

        var discovery = new PortDiscovery(enumerator, (port, baud) => new FakeOnStepDevice());

        await discovery.DiscoverAsync(
            FastOptions(),
            new Progress<PortDiscoveryProgress>(p => reports.Add(p)));

        // Progress is dispatched asynchronously, so a margin is given.
        await Task.Delay(200);

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(2, r.PortsTotal));
    }

    [Fact]
    public async Task CancellationByTheUserIsPropagated()
    {
        var enumerator = new FakeEnumerator(Port("COM3"), Port("COM7"));

        var discovery = new PortDiscovery(enumerator, (port, baud) => new SilentTransport(port));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discovery.DiscoverAsync(
                FastOptions() with { ProbeTimeout = TimeSpan.FromSeconds(30) },
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ProbingASpecificPortAndBaudRateWorks()
    {
        var discovery = new PortDiscovery(
            new FakeEnumerator(),
            (port, baud) => new FakeOnStepDevice());

        DiscoveredController? found = await discovery.ProbeAsync(
            "COM7", 9600, FastOptions());

        Assert.NotNull(found);
        Assert.Equal("COM7", found.PortName);
    }

    [Fact]
    public void ListPortsDoesNotOpenAnything()
    {
        bool opened = false;
        var enumerator = new FakeEnumerator(
            Port("COM3", "Standard Serial over Bluetooth link"),
            Port("COM7", "CP210x", PortRanking.Vendors.SiliconLabs));

        var discovery = new PortDiscovery(
            enumerator,
            (port, baud) =>
            {
                opened = true;
                return new FakeOnStepDevice();
            });

        IReadOnlyList<SerialPortInfo> ports = discovery.ListPorts();

        Assert.False(opened);
        Assert.Equal(2, ports.Count);
        Assert.Contains(ports, p => !p.IsCandidate);
    }
}

public class ProbeValidationTests
{
    [Theory]
    [InlineData("On-Step", true)]
    [InlineData("OnStep", true)]
    [InlineData("OnStepX", true)]
    // Forks and custom names are accepted: rejecting them would leave
    // valid mounts without autodiscovery. The strong filter is the version.
    [InlineData("MyMount", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void ProductNameValidationAcceptsPrintableShortText(string? product, bool expected)
    {
        Assert.Equal(expected, PortDiscovery.LooksLikeOnStep(product));
    }

    [Fact]
    public void ProductNameWithControlCharactersIsRejected()
    {
        // Typical garbage from reading at the wrong baud rate: bytes with
        // the high bit set and control characters. They are written
        // escaped on purpose, because literals would be invisible in the
        // file.
        Assert.False(PortDiscovery.LooksLikeOnStep("OnStep\u0000"));
        Assert.False(PortDiscovery.LooksLikeOnStep("OnStep\u0007"));
        Assert.False(PortDiscovery.LooksLikeOnStep("\u00ff\u00fe"));
        Assert.False(PortDiscovery.LooksLikeOnStep("\u0001\u0002\u0003"));
    }

    [Fact]
    public void OverlyLongProductNameIsRejected()
    {
        Assert.False(PortDiscovery.LooksLikeOnStep(new string('x', 33)));
    }

    [Theory]
    [InlineData("10.21b", true)]
    [InlineData("4.24s", true)]
    [InlineData("3.16", true)]
    [InlineData("10.21", true)]
    // Without a dot it is not an OnStep version.
    [InlineData("1021b", false)]
    // Must start with a digit.
    [InlineData("v10.21", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void VersionValidationIsWhatRejectsFalsePositives(string? version, bool expected)
    {
        Assert.Equal(expected, PortDiscovery.LooksLikeVersion(version));
    }

    [Fact]
    public void VersionWithSeparatorsThatOnStepNeverUsesIsRejected()
    {
        Assert.False(PortDiscovery.LooksLikeVersion("10-21"));
        Assert.False(PortDiscovery.LooksLikeVersion("10,21"));
        Assert.False(PortDiscovery.LooksLikeVersion("10:21"));
    }
}
