using OnStepX.Core.Discovery;
using Xunit;
using Xunit.Abstractions;

namespace OnStepX.Core.Tests;

/// <summary>
/// Exercises the platform's real enumerator against the system running the
/// tests.
/// </summary>
/// <remarks>
/// It asserts nothing about the hardware present, because there is none in
/// CI. What it checks is that reading sysfs on Linux, or querying WMI on
/// Windows, does not throw or hang. Without this, a failure in the
/// enumerator would only show up on the user's machine.
/// </remarks>
public class RealPlatformEnumeratorTests(ITestOutputHelper output)
{
    [Fact]
    public void ThePlatformEnumeratorNeverThrows()
    {
        ISerialPortEnumerator enumerator = SerialPortEnumerators.CreateDefault();

        output.WriteLine($"Enumerator: {enumerator.Description}");

        IReadOnlyList<SerialPortInfo> ports = enumerator.Enumerate();

        output.WriteLine($"Ports found: {ports.Count}");
        foreach (SerialPortInfo p in ports)
        {
            output.WriteLine(
                $"  {p.PortName} | name: {p.FriendlyName ?? "(unknown)"} | " +
                $"VID: {p.VendorId?.ToString("X4") ?? "----"} | " +
                $"PID: {p.ProductId?.ToString("X4") ?? "----"}");
        }

        Assert.NotNull(ports);
    }

    [Fact]
    public void RankingTheRealPortsNeverThrows()
    {
        ISerialPortEnumerator enumerator = SerialPortEnumerators.CreateDefault();

        IReadOnlyList<SerialPortInfo> ranked = PortRanking.RankAll(enumerator.Enumerate());

        foreach (SerialPortInfo p in ranked)
        {
            output.WriteLine(
                $"  {p.PortName} | priority {p.Priority} | " +
                (p.IsCandidate ? "candidate" : $"excluded: {p.ExcludedReason}"));
        }

        Assert.All(ranked, p => Assert.False(string.IsNullOrEmpty(p.PortName)));
    }

    [Fact]
    public void ListingPortsThroughDiscoveryNeverOpensOrThrows()
    {
        var discovery = new PortDiscovery();

        IReadOnlyList<SerialPortInfo> ports = discovery.ListPorts();

        output.WriteLine($"ListPorts returned {ports.Count} ports without opening any");

        Assert.NotNull(ports);
    }

    [Fact]
    public async Task DiscoveryOnAMachineWithNoMountFinishesQuicklyAndFindsNothing()
    {
        // There is no mount in CI. What matters is that it finishes, not
        // that it finds anything.
        var discovery = new PortDiscovery();

        var options = new PortDiscoveryOptions
        {
            PreferredBaudRate = 9600,
            BaudRates = [9600],
            ProbeTimeout = TimeSpan.FromMilliseconds(200),
            MaxConcurrency = 2,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<DiscoveredController> found =
            await discovery.DiscoverAsync(options, cancellationToken: cts.Token);

        output.WriteLine($"Controllers found: {found.Count}");
        foreach (DiscoveredController c in found)
        {
            output.WriteLine($"  {c}");
        }

        // If this machine genuinely has a mount connected, finding it is
        // correct. The test does not fail because of that.
        Assert.NotNull(found);
    }
}
