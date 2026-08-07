using OnStepX.Core.Config;
using OnStepX.Core.Hardware;
using OnStepX.Core.Simulation;
using OnStepX.Core.Transport;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// Transport that counts opens and closes, to verify reference counting.
/// </summary>
/// <remarks>
/// Opens are also controller resets on real hardware: every open of a serial
/// port pulses DTR and RTS, which resets the boards that wire those lines to
/// EN and GPIO0. Counting them is how the discovery tests pin down that
/// connecting opens the port once.
/// </remarks>
internal sealed class CountingTransport(ITransport? inner = null) : ITransport
{
    private readonly ITransport _inner = inner ?? new FakeOnStepDevice();

    public int OpenCount { get; private set; }

    public int DisposeCount { get; private set; }

    public string Description => "counting transport";

    public bool IsOpen => _inner.IsOpen;

    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        OpenCount++;
        await _inner.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CloseAsync() => _inner.CloseAsync();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        _inner.WriteAsync(data, ct);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        _inner.ReadAsync(buffer, ct);

    public void DiscardInputBuffer() => _inner.DiscardInputBuffer();

    public async ValueTask DisposeAsync()
    {
        DisposeCount++;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}

public class OnStepXConnectionTests
{
    private static OnStepXSettings SimulatedSettings() => new()
    {
        Connection = new ConnectionSettings
        {
            Kind = TransportKind.Simulated,
            TimeoutMilliseconds = 2000,
            UseErrorCorrection = true,
        },
    };

    [Fact]
    public async Task WarmingUpLeavesAWorkingSessionAlone()
    {
        // The warm up exists to find the port before a client asks, but it
        // finds it by opening ports. Doing that underneath a client that
        // already holds one is how a working session gets broken.
        var settings = SimulatedSettings();
        settings.Connection.Kind = TransportKind.Serial;
        settings.Connection.AutoDiscoverPort = true;
        settings.Connection.PortName = "COM7";

        var transport = new CountingTransport();

        await using var connection = new OnStepXConnection(
            () => settings,
            () => transport,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OnStepXConnection>.Instance);

        await connection.ConnectAsync("Telescope");
        Assert.True(connection.IsConnected);

        await connection.WarmUpAsync();

        // Still connected, over the same port, opened no further times.
        Assert.True(connection.IsConnected);
        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(0, transport.DisposeCount);
        Assert.Equal("COM7", settings.Connection.PortName);
    }

    [Fact]
    public async Task ConnectingReadsTheControllerIdentity()
    {
        await using var connection = new OnStepXConnection(SimulatedSettings);

        await connection.ConnectAsync("Telescope");

        Assert.True(connection.IsConnected);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.NotNull(connection.Identity);
        Assert.Equal("On-Step", connection.Identity.ProductName);
        Assert.Equal("10.21b", connection.Identity.FirmwareVersion);
        Assert.Equal("OnStepX 10.21b", connection.Identity.FullName);
    }

    [Fact]
    public async Task TheFirstDeviceOpensTheTransportAndTheOthersReuseIt()
    {
        var transport = new CountingTransport();
        await using var connection = new OnStepXConnection(SimulatedSettings, () => transport);

        await connection.ConnectAsync("Telescope");
        await connection.ConnectAsync("Focuser");
        await connection.ConnectAsync("Rotator");
        await connection.ConnectAsync("ObservingConditions");

        // This is the key property: a single port for the four devices.
        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(0, transport.DisposeCount);
        Assert.Equal(4, connection.ConnectedDevices.Count);
    }

    [Fact]
    public async Task OnlyTheLastDeviceToDisconnectClosesTheTransport()
    {
        var transport = new CountingTransport();
        await using var connection = new OnStepXConnection(SimulatedSettings, () => transport);

        await connection.ConnectAsync("Telescope");
        await connection.ConnectAsync("Focuser");

        await connection.DisconnectAsync("Telescope");

        // The focuser is still connected, so the port stays open.
        Assert.True(connection.IsConnected);
        Assert.Equal(0, transport.DisposeCount);

        await connection.DisconnectAsync("Focuser");

        Assert.False(connection.IsConnected);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task ConnectingTheSameDeviceTwiceIsIdempotent()
    {
        // Many clients are not symmetric: they call Connected = true several
        // times and false only once. If the count were per call, the port
        // would stay open forever.
        var transport = new CountingTransport();
        await using var connection = new OnStepXConnection(SimulatedSettings, () => transport);

        await connection.ConnectAsync("Telescope");
        await connection.ConnectAsync("Telescope");
        await connection.ConnectAsync("Telescope");

        Assert.Single(connection.ConnectedDevices);

        await connection.DisconnectAsync("Telescope");

        Assert.False(connection.IsConnected);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task DisconnectingADeviceThatWasNeverConnectedIsHarmless()
    {
        var transport = new CountingTransport();
        await using var connection = new OnStepXConnection(SimulatedSettings, () => transport);

        await connection.ConnectAsync("Telescope");

        await connection.DisconnectAsync("Focuser");

        // The focuser was never connected, so the mount stays connected.
        Assert.True(connection.IsConnected);
        Assert.Equal(0, transport.DisposeCount);
    }

    [Fact]
    public async Task DisconnectingWithNothingConnectedDoesNotThrow()
    {
        await using var connection = new OnStepXConnection(SimulatedSettings);

        await connection.DisconnectAsync("Telescope");

        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task DeviceMembershipIsQueryable()
    {
        await using var connection = new OnStepXConnection(SimulatedSettings);

        await connection.ConnectAsync("Telescope");

        Assert.True(connection.IsDeviceConnected("Telescope"));
        Assert.False(connection.IsDeviceConnected("Focuser"));

        // The name is case insensitive: different clients write it differently.
        Assert.True(connection.IsDeviceConnected("telescope"));
    }

    [Fact]
    public async Task AccessingTheChannelWithoutConnectingThrowsAClearError()
    {
        await using var connection = new OnStepXConnection(SimulatedSettings);

        var ex = Assert.Throws<InvalidOperationException>(() => connection.Channel);

        Assert.Contains("No connection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedConnectionLeavesNoDeviceRegisteredSoTheNextAttemptRetries()
    {
        // If a failed attempt left the device registered, the next one
        // would believe a connection already existed and would return a
        // nonexistent channel.
        var settings = new OnStepXSettings
        {
            Connection = new ConnectionSettings { Kind = TransportKind.Simulated },
        };

        await using var connection = new OnStepXConnection(
            () => settings,
            () => new UnopenableTransport("broken", new IOException("no device")));

        await Assert.ThrowsAsync<IOException>(() => connection.ConnectAsync("Telescope"));

        Assert.Equal(ConnectionState.Failed, connection.State);
        Assert.False(connection.IsConnected);
        Assert.Empty(connection.ConnectedDevices);
        Assert.NotNull(connection.LastError);
        Assert.False(connection.IsDeviceConnected("Telescope"));
    }

    [Fact]
    public async Task ReconnectingAfterAFailureWorks()
    {
        bool fail = true;
        var settings = SimulatedSettings();

        await using var connection = new OnStepXConnection(
            () => settings,
            () => fail
                ? new UnopenableTransport("broken", new IOException("no device"))
                : new FakeOnStepDevice());

        await Assert.ThrowsAsync<IOException>(() => connection.ConnectAsync("Telescope"));

        fail = false;
        await connection.ConnectAsync("Telescope");

        Assert.True(connection.IsConnected);
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Null(connection.LastError);
    }

    [Fact]
    public async Task TheSharedChannelSerialisesConcurrentDeviceTraffic()
    {
        // This is what makes an external hub unnecessary: four devices
        // talking at the same time over a single port without desynchronizing.
        await using var connection = new OnStepXConnection(SimulatedSettings);

        await connection.ConnectAsync("Telescope");
        await connection.ConnectAsync("Focuser");
        await connection.ConnectAsync("Rotator");
        await connection.ConnectAsync("ObservingConditions");

        // Connecting already consumes commands to read the controller
        // identity, so the increment is measured and not the total.
        long baseline = connection.Channel.TransactionCount;

        Task<string>[] traffic =
        [
            .. Enumerable.Range(0, 10).Select(_ => connection.Channel.GetStringAsync("GVP")),
            .. Enumerable.Range(0, 10).Select(_ => connection.Channel.GetStringAsync("GVN")),
            .. Enumerable.Range(0, 10).Select(_ => connection.Channel.GetStringAsync("GU")),
        ];

        string[] results = await Task.WhenAll(traffic);

        // Each command returns its own value, with no mixing.
        Assert.Equal(10, results.Count(r => r == "On-Step"));
        Assert.Equal(10, results.Count(r => r == "10.21b"));
        Assert.Equal(30, connection.Channel.TransactionCount - baseline);
    }

    [Fact]
    public async Task SettingsAreReadAtConnectTimeSoChangesApplyOnReconnect()
    {
        var settings = SimulatedSettings();
        settings.Connection.UseErrorCorrection = false;

        await using var connection = new OnStepXConnection(() => settings);

        await connection.ConnectAsync("Telescope");
        Assert.False(connection.Channel.Options.UseErrorCorrection);
        await connection.DisconnectAsync("Telescope");

        settings.Connection.UseErrorCorrection = true;

        await connection.ConnectAsync("Telescope");
        Assert.True(connection.Channel.Options.UseErrorCorrection);
    }

    [Fact]
    public async Task DisposingClosesEverythingEvenWithDevicesStillConnected()
    {
        var transport = new CountingTransport();
        var connection = new OnStepXConnection(SimulatedSettings, () => transport);

        await connection.ConnectAsync("Telescope");
        await connection.ConnectAsync("Focuser");

        await connection.DisposeAsync();

        Assert.Equal(1, transport.DisposeCount);
        Assert.False(connection.IsConnected);
        Assert.Empty(connection.ConnectedDevices);
    }

    [Fact]
    public async Task UsingAConnectionAfterDisposeThrows()
    {
        var connection = new OnStepXConnection(SimulatedSettings);
        await connection.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.ConnectAsync("Telescope"));
    }

    [Fact]
    public async Task SimulatedTransportNeedsNoHardwareAtAll()
    {
        // This is what allows ConformU to pass on Linux without a mount.
        var settings = new OnStepXSettings
        {
            Connection = new ConnectionSettings { Kind = TransportKind.Simulated },
        };

        await using var connection = new OnStepXConnection(() => settings);

        await connection.ConnectAsync("Telescope");

        Assert.True(connection.IsConnected);
        Assert.Equal("Simulated OnStepX", connection.Identity!.TransportDescription);
    }
}
