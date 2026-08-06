using AscomLevel = ASCOM.Common.Interfaces.LogLevel;
using IAscomLogger = ASCOM.Common.Interfaces.ILogger;
using IMelLogger = Microsoft.Extensions.Logging.ILogger;
using MelLevel = Microsoft.Extensions.Logging.LogLevel;

namespace OnStepX.AlpacaServer;

/// <summary>
/// Bridges the ASCOM logging interface onto
/// <c>Microsoft.Extensions.Logging</c>.
/// </summary>
/// <remarks>
/// The official Alpaca REST layer writes through
/// <see cref="ASCOM.Common.Interfaces.ILogger"/>, which is its own interface and
/// unrelated to the .NET one. Routing both through a single sink keeps protocol
/// traces and web traces interleaved in real order, which is exactly what you need
/// when diagnosing a connection problem.
/// </remarks>
public sealed class AscomLoggerAdapter(IMelLogger inner) : IAscomLogger
{
    private AscomLevel _minimum = AscomLevel.Information;

    /// <inheritdoc />
    public AscomLevel LoggingLevel => _minimum;

    /// <inheritdoc />
    public void SetMinimumLoggingLevel(AscomLevel level) => _minimum = level;

    /// <inheritdoc />
    public void Log(AscomLevel level, string message)
    {
        if (level < _minimum)
        {
            return;
        }

        inner.Log(Translate(level), "{Message}", message);
    }

    private static MelLevel Translate(AscomLevel level) => level switch
    {
        AscomLevel.Verbose => MelLevel.Trace,
        AscomLevel.Debug => MelLevel.Debug,
        AscomLevel.Information => MelLevel.Information,
        AscomLevel.Warning => MelLevel.Warning,
        AscomLevel.Error => MelLevel.Error,
        AscomLevel.Fatal => MelLevel.Critical,
        _ => MelLevel.Information,
    };
}
