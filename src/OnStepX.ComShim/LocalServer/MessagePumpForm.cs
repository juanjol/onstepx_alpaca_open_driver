using System.Windows.Forms;

namespace OnStepX.ComShim.LocalServer
{
    /// <summary>
    /// Invisible window whose only job is to own the Windows message loop.
    /// </summary>
    /// <remarks>
    /// COM calls into a single threaded apartment are delivered as window
    /// messages, so without a pump the served drivers would never receive
    /// anything. <see cref="SetVisibleCore"/> is overridden because
    /// <c>Application.Run(form)</c> makes its main form visible, which would
    /// pop an empty window on the user's desktop.
    /// </remarks>
    internal sealed class MessagePumpForm : Form
    {
        /// <summary>Creates the window without showing it anywhere.</summary>
        internal MessagePumpForm()
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            FormBorderStyle = FormBorderStyle.None;
            Text = "OnStepX COM Shim";
        }

        /// <inheritdoc />
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }
    }
}
