using ASCOM.Alpaca;
using OnStepX.Core.Config;

namespace OnStepX.AlpacaServer;

/// <summary>
/// Adapts this driver's settings to what <c>ASCOM.Alpaca.Razor</c> expects.
/// </summary>
/// <remarks>
/// The official REST layer reads its configuration through this interface. Implementing
/// it on top of <see cref="OnStepXSettings"/> avoids keeping two copies of the same
/// settings, which would inevitably drift apart.
/// </remarks>
public sealed class AlpacaConfiguration(Func<OnStepXSettings> settingsProvider) : IAlpacaConfiguration
{
    private ServerSettings Server => settingsProvider().Server;

    /// <inheritdoc />
    public bool RunInStrictAlpacaMode => Server.StrictAlpacaMode;

    /// <inheritdoc />
    public bool PreventRemoteDisconnects => Server.PreventRemoteDisconnects;

    /// <inheritdoc />
    public string ServerName => "OnStepX ASCOM";

    /// <inheritdoc />
    public string Manufacturer => "OnStepX ASCOM";

    /// <inheritdoc />
    public string ServerVersion =>
        typeof(AlpacaConfiguration).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// <inheritdoc />
    public string Location => Server.Location;

    /// <summary>
    /// There is no camera in this driver, so binary image download does not apply.
    /// </summary>
    public bool AllowImageBytesDownload => false;

    /// <inheritdoc />
    public bool AllowDiscovery => Server.AllowDiscovery;

    /// <inheritdoc />
    public int ServerPort => Server.Port;

    /// <inheritdoc />
    public bool AllowRemoteAccess => Server.AllowRemoteAccess;

    /// <inheritdoc />
    public bool LocalRespondOnlyToLocalHost => !Server.AllowRemoteAccess;

    /// <inheritdoc />
    public bool RunSwagger => Server.RunSwagger;
}
