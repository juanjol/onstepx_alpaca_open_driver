using Microsoft.AspNetCore.Components;
using OnStepX.Core.Config;

namespace OnStepX.AlpacaServer.Shared;

/// <summary>
/// Shared behaviour of the setup pages: editing driver settings and running operations
/// against the controller.
/// </summary>
/// <remarks>
/// <para>
/// Editing works on a <b>deep copy</b> that only reaches the running configuration when the
/// user saves. Binding the live settings directly would be worse than untidy: a record's
/// nested sections are shared by reference, so a half typed baud rate would already be in
/// force for the next connection attempt and there would be no way to cancel.
/// </para>
/// <para>
/// Operations against the controller borrow the shared connection per call through
/// <see cref="SetupSession"/> and never hold it. A browser tab that vanishes must not leave
/// the serial port open.
/// </para>
/// </remarks>
public abstract class SetupPageBase : ComponentBase, IDisposable
{
    private CancellationTokenSource? _cancellation;

    /// <summary>
    /// This page's access to the controller. One per page, so that two browser tabs hold the
    /// connection independently and neither can release it under the other.
    /// </summary>
    protected SetupSession Session { get; } = new();

    /// <summary>Working copy of the driver settings, edited by the form.</summary>
    protected OnStepXSettings Working { get; private set; } = new();

    /// <summary>An operation against the controller is under way.</summary>
    protected bool Busy { get; private set; }

    /// <summary>Last message about an operation against the controller.</summary>
    protected string? Status { get; private set; }

    /// <summary>The last controller message was a failure.</summary>
    protected bool Failed { get; private set; }

    /// <summary>
    /// Last message about the driver settings file, shown in the save bar.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Status"/> on purpose. The two report different things, and
    /// showing "written to the controller" next to a Save button is exactly the confusion
    /// this whole page layout exists to avoid.
    /// </remarks>
    protected string? SaveStatus { get; private set; }

    /// <summary>The last settings message was a failure.</summary>
    protected bool SaveFailed { get; private set; }

    /// <summary>The working copy differs from the settings in force.</summary>
    /// <remarks>
    /// Compared as serialized text rather than field by field, which keeps this correct as the
    /// settings model grows instead of quietly missing a new field. It does serialize both
    /// objects on every render, which is a fraction of a millisecond on a settings file of this
    /// size, so it is not worth caching a baseline that could then go stale when a client's
    /// sync writes to the settings behind the page's back.
    /// </remarks>
    protected bool IsDirty =>
        !string.Equals(
            SettingsStore.Export(Working, includePassword: true),
            SettingsStore.Export(ServerRuntime.Settings, includePassword: true),
            StringComparison.Ordinal);

    /// <inheritdoc />
    protected override void OnInitialized() => Working = SettingsStore.Clone(ServerRuntime.Settings);

    /// <summary>Saves the working copy and makes it the configuration in force.</summary>
    protected virtual void Save()
    {
        try
        {
            ServerRuntime.UpdateSettings(SettingsStore.Clone(Working));

            // Reload from what actually got stored, so the form shows the effect of any
            // normalisation rather than what was typed.
            Working = SettingsStore.Clone(ServerRuntime.Settings);

            ReportSave("Settings saved.", failed: false);
        }
        catch (Exception ex)
        {
            ReportSave($"Could not save the settings: {ex.Message}", failed: true);
        }
    }

    /// <summary>Throws the working copy away and starts again from what is in force.</summary>
    protected void Revert()
    {
        Working = SettingsStore.Clone(ServerRuntime.Settings);
        ReportSave("Changes discarded.", failed: false);
    }

    /// <summary>Shows a message about an operation against the controller.</summary>
    protected void Report(string? message, bool failed)
    {
        Status = message;
        Failed = failed;
    }

    /// <summary>Shows a message about the driver settings file.</summary>
    protected void ReportSave(string? message, bool failed)
    {
        SaveStatus = message;
        SaveFailed = failed;
    }

    /// <summary>
    /// Runs an operation against the controller, showing progress and turning any failure
    /// into a message rather than an unhandled exception that kills the circuit.
    /// </summary>
    /// <param name="operation">Work to do, with the connection already open.</param>
    /// <param name="success">Message on success, or null to leave the current one.</param>
    protected async Task RunAsync(Func<CancellationToken, Task> operation, string? success = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Busy)
        {
            return;
        }

        Busy = true;
        Report(null, failed: false);

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();

        try
        {
            await Session.RunAsync(operation, _cancellation.Token);

            if (success is not null)
            {
                Report(success, failed: false);
            }
        }
        catch (Exception ex)
        {
            Report(SetupSession.Describe(ex), failed: true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Cancels whatever is running.</summary>
    protected void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// Redraws from a callback that may be running on a thread pool thread.
    /// </summary>
    /// <remarks>
    /// Progress from port discovery arrives on whatever thread the probe finished on.
    /// Calling <c>StateHasChanged</c> there does nothing visible, so the progress list
    /// would silently never move.
    /// </remarks>
    protected Task RedrawAsync() => InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public virtual void Dispose()
    {
        // A page can be navigated away from mid operation. Cancelling here stops the work,
        // and SetupSession releases the connection in its own finally either way.
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;

        GC.SuppressFinalize(this);
    }
}
