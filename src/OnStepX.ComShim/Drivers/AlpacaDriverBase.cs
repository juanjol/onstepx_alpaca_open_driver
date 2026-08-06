using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ASCOM.DeviceInterface;
using OnStepX.ComShim.Config;
using OnStepX.ComShim.LocalServer;
using Library = ASCOM.Common.DeviceInterfaces;

namespace OnStepX.ComShim.Drivers
{
    /// <summary>
    /// Members every ASCOM device shares, forwarded to an Alpaca client.
    /// </summary>
    /// <remarks>
    /// The Alpaca client library already implements the whole device contract,
    /// so the drivers add nothing but the trip across the COM boundary. That
    /// boundary is where the two type systems disagree: the library speaks in
    /// <c>ASCOM.Common.DeviceInterfaces</c> types, aliased here as
    /// <c>Library</c>, while COM clients expect the <c>ASCOM.DeviceInterface</c>
    /// ones the Platform installs. Enums line up value for value and can be
    /// cast, but collections have to be rebuilt.
    /// <para>
    /// The class does not declare <see cref="IDisposable"/> even though it
    /// exposes <c>Dispose</c>. With <c>ClassInterfaceType.None</c> a COM
    /// client's <c>IDispatch</c> comes from the first interface the class
    /// implements, and leaving <c>IDisposable</c> in the running would risk
    /// clients getting that instead of the device interface.
    /// </para>
    /// </remarks>
    [ComVisible(false)]
    public abstract class AlpacaDriverBase : ReferenceCountedObjectBase
    {
        private readonly Library.IAscomDeviceV2 _device;

        /// <summary>Wraps an already created Alpaca client.</summary>
        protected AlpacaDriverBase(Library.IAscomDeviceV2 device)
        {
            _device = device;
        }

        /// <summary>The Alpaca client every member forwards to.</summary>
        protected Library.IAscomDeviceV2 Device => _device;

        /// <summary>Connected state, in the synchronous pre Platform 7 sense.</summary>
        public bool Connected
        {
            get => _device.Connected;
            set => _device.Connected = value;
        }

        /// <summary>True while an asynchronous connect or disconnect is running.</summary>
        public bool Connecting => _device.Connecting;

        /// <summary>Description reported by the Alpaca server.</summary>
        public string Description => _device.Description;

        /// <summary>Driver information reported by the Alpaca server.</summary>
        public string DriverInfo => _device.DriverInfo;

        /// <summary>Driver version reported by the Alpaca server.</summary>
        public string DriverVersion => _device.DriverVersion;

        /// <summary>Interface version reported by the Alpaca server.</summary>
        public short InterfaceVersion => _device.InterfaceVersion;

        /// <summary>Device name reported by the Alpaca server.</summary>
        public string Name => _device.Name;

        /// <summary>
        /// Actions the device supports.
        /// </summary>
        /// <remarks>
        /// COM clients expect an <see cref="ArrayList"/>, the only list type
        /// the Platform interfaces ever used, so the generic list the Alpaca
        /// client returns has to be copied into one.
        /// </remarks>
        public ArrayList SupportedActions => new ArrayList(_device.SupportedActions.ToArray());

        /// <summary>
        /// Operational state of the device in a single call.
        /// </summary>
        /// <remarks>
        /// The state values also need rebuilding: the Alpaca client produces
        /// <c>Library.StateValue</c> instances, while COM clients only know the
        /// identically shaped but unrelated <c>ASCOM.DeviceInterface.StateValue</c>
        /// the Platform installs.
        /// </remarks>
        public IStateValueCollection DeviceState
        {
            get
            {
                List<IStateValue> values = new List<IStateValue>();

                foreach (Library.StateValue value in _device.DeviceState)
                {
                    values.Add(new StateValue(value.Name, value.Value));
                }

                return new StateValueCollection(values);
            }
        }

        /// <summary>Starts an asynchronous connection.</summary>
        public void Connect()
        {
            _device.Connect();
        }

        /// <summary>Starts an asynchronous disconnection.</summary>
        public void Disconnect()
        {
            _device.Disconnect();
        }

        /// <summary>Invokes a device specific action.</summary>
        public string Action(string ActionName, string ActionParameters)
        {
            return _device.Action(ActionName, ActionParameters);
        }

        /// <summary>Sends a raw command and ignores the reply.</summary>
        public void CommandBlind(string Command, bool Raw = false)
        {
            _device.CommandBlind(Command, Raw);
        }

        /// <summary>Sends a raw command and returns its boolean reply.</summary>
        public bool CommandBool(string Command, bool Raw = false)
        {
            return _device.CommandBool(Command, Raw);
        }

        /// <summary>Sends a raw command and returns its string reply.</summary>
        public string CommandString(string Command, bool Raw = false)
        {
            return _device.CommandString(Command, Raw);
        }

        /// <summary>
        /// Does nothing on purpose: the Alpaca client is released when the COM
        /// object itself goes away.
        /// </summary>
        /// <remarks>
        /// Disposing the Alpaca client here would throw away the HTTP client it
        /// talks through while the COM object stays perfectly alive, because a
        /// client calling <c>Dispose</c> does not have to release its reference
        /// straight afterwards. Any further call would then fail with an error
        /// that says nothing about ASCOM. Waiting for the finaliser costs one
        /// garbage collection cycle and removes the whole problem.
        /// </remarks>
        public void Dispose()
        {
        }

        /// <summary>Releases the Alpaca client once nothing holds this object.</summary>
        ~AlpacaDriverBase()
        {
            try
            {
                _device.Dispose();
            }
            catch (Exception exception)
            {
                // Nothing can be done about a failure here, and throwing from a
                // finaliser would take the whole process down with it.
                ShimLog.Write("Dispose", $"Ignoring an error while releasing the client: {exception.Message}");
            }
        }

        /// <summary>
        /// Opens the Alpaca server's setup page.
        /// </summary>
        /// <remarks>
        /// The shim has nothing of its own to configure. Everything, including
        /// the port it reads to find the server, is set in that page, so a
        /// second dialogue here would only be a way to disagree with it.
        /// </remarks>
        public void SetupDialog()
        {
            string url = AlpacaEndpoint.SetupUrl();

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                ShimLog.Write("SetupDialog", $"Could not open {url}: {exception.Message}");

                MessageBox.Show(
                    $"OnStepX is configured in the Alpaca server's setup page:{Environment.NewLine}{url}",
                    "OnStepX COM Shim",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
    }
}
