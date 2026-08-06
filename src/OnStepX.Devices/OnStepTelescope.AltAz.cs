using ASCOM;
using Microsoft.Extensions.Logging;
using OnStepX.Core.Astronomy;
using OnStepX.Core.Devices;
using OnStepX.Core.Protocol;

namespace OnStepX.Devices;

/// <summary>
/// Horizontal coordinate slews, which need convergence on an equatorial mount.
/// </summary>
/// <remarks>
/// <para>
/// An equatorial mount slews to a fixed right ascension and declination. Converting
/// the requested altitude and azimuth once, at the moment of the request, means the
/// mount aims at the sky position that <b>was</b> there when the command arrived. By
/// the time it gets there the sky has turned, so it lands short. A ten second slew
/// leaves roughly 150 arcseconds of error, far outside the ten arcseconds a
/// conformance check allows.
/// </para>
/// <para>
/// The fix is to re-aim: convert again from the current sidereal time and slew the
/// remaining distance. Each pass is much shorter than the last, so the error collapses
/// quickly. The loop runs in the background and keeps <c>Slewing</c> true throughout,
/// because ASCOM promises that when <c>Slewing</c> goes false the mount has arrived.
/// </para>
/// </remarks>
public sealed partial class OnStepTelescope
{
    /// <summary>Arc seconds of error considered close enough to stop re-aiming.</summary>
    private const double AltAzToleranceArcseconds = 5.0;

    /// <summary>
    /// Cap on re-aim passes. Convergence is fast, so hitting this means something else
    /// is wrong and looping forever would be worse than reporting arrival.
    /// </summary>
    private const int MaxAltAzPasses = 6;

    private volatile bool _altAzConverging;
    private Task? _altAzTask;

    /// <summary>An alt az slew is still re-aiming.</summary>
    private bool IsConvergingOnAltAz => _altAzConverging;

    /// <summary>Starts an asynchronous slew to horizontal coordinates.</summary>
    public void SlewToAltAzAsync(double azimuth, double altitude)
    {
        ValidateAzimuth(azimuth);
        ValidateAltitude(altitude);
        RequireUnparked();

        ClearAxisMotion();

        // First pass runs inline so that an immediately rejected slew, for example one
        // below the horizon limit, throws from this call rather than disappearing into
        // a background task where the client would never see it.
        StartAltAzPass(azimuth, altitude);

        _altAzConverging = true;
        _altAzTask = Task.Run(() => ConvergeOnAltAzAsync(azimuth, altitude));
    }

    /// <summary>Slews to horizontal coordinates and waits for it to finish.</summary>
    public void SlewToAltAz(double azimuth, double altitude)
    {
        SlewToAltAzAsync(azimuth, altitude);
        WaitForSlewToComplete();
    }

    /// <summary>
    /// Converts the requested horizontal coordinates using the mount's current
    /// sidereal time and starts an equatorial slew towards them.
    /// </summary>
    private void StartAltAzPass(double azimuth, double altitude)
    {
        MountSnapshot snapshot = ValidSnapshot;
        double latitude = SiteLatitude;

        (double ra, double dec) = Coordinates.HorizontalToEquatorial(
            altitude, azimuth, latitude, snapshot.SiderealTime);

        RunCommandAndRefresh(async () =>
        {
            await SetTargetAsync(ra, dec, CancellationToken.None).ConfigureAwait(false);

            GotoResult result = await Channel
                .GetGotoResultAsync("MS", CancellationToken.None)
                .ConfigureAwait(false);

            ThrowIfGotoRejected(result);
        });
    }

    private async Task ConvergeOnAltAzAsync(double azimuth, double altitude)
    {
        try
        {
            for (int pass = 0; pass < MaxAltAzPasses; pass++)
            {
                await WaitForGotoAsync().ConfigureAwait(false);

                MountSnapshot snapshot = _poller.Current;

                double altitudeError = Math.Abs(snapshot.Altitude - altitude);
                double azimuthError = Math.Abs(AngleDifference(snapshot.Azimuth, azimuth));

                double worstArcseconds = Math.Max(altitudeError, azimuthError) * 3600.0;

                if (worstArcseconds <= AltAzToleranceArcseconds)
                {
                    Logger.LogDebug(
                        "Alt az slew converged after {Passes} pass(es), {Error} arcsec",
                        pass + 1, worstArcseconds.ToString("0.0"));
                    return;
                }

                Logger.LogDebug(
                    "Alt az re-aim {Pass}: still {Error} arcsec out",
                    pass + 1, worstArcseconds.ToString("0.0"));

                StartAltAzPass(azimuth, altitude);
            }

            Logger.LogWarning(
                "Alt az slew stopped re-aiming after {Passes} passes without converging",
                MaxAltAzPasses);
        }
        catch (Exception ex)
        {
            // The client already got a successful return from the first pass, so all
            // that is left is to record why re-aiming stopped.
            Logger.LogWarning(ex, "Alt az convergence stopped early");
        }
        finally
        {
            _altAzConverging = false;
        }
    }

    /// <summary>Waits for the mount's own goto to finish.</summary>
    private async Task WaitForGotoAsync()
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);

        while (true)
        {
            MountSnapshot snapshot = await _poller
                .RefreshNowAsync(CancellationToken.None)
                .ConfigureAwait(false);

            if (!snapshot.IsSlewing)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new DriverException(
                    "The mount was still slewing after 10 minutes during an alt az slew.");
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    /// <summary>Shortest signed difference between two angles, in degrees.</summary>
    private static double AngleDifference(double a, double b)
    {
        double difference = (a - b) % 360.0;

        if (difference > 180.0)
        {
            difference -= 360.0;
        }
        else if (difference < -180.0)
        {
            difference += 360.0;
        }

        return difference;
    }
}
