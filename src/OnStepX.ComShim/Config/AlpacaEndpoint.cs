using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using ASCOM.Alpaca.Clients;
using ASCOM.Common.Alpaca;
using OnStepX.ComShim.LocalServer;

namespace OnStepX.ComShim.Config
{
    /// <summary>
    /// Builds Alpaca clients pointed at the OnStepX Alpaca server running on
    /// this machine.
    /// </summary>
    /// <remarks>
    /// The shim does not have settings of its own. It reads the port out of the
    /// very file the Alpaca server writes, so there is a single source of truth
    /// for the connection and changing the port in the setup page is enough for
    /// COM clients to follow. The path convention is duplicated from
    /// <c>OnStepX.Core.Config.SettingsStore</c> because this project targets
    /// net48 and cannot reference a net8.0 assembly. Only the Windows branch is
    /// duplicated, since a COM local server never runs anywhere else.
    /// </remarks>
    internal static class AlpacaEndpoint
    {
        /// <summary>Loopback only: the shim and the server always share a machine.</summary>
        private const string Host = "127.0.0.1";

        /// <summary>Matches the default in <c>ServerSettings.Port</c>.</summary>
        private const int DefaultPort = 11111;

        private static int _clientNumber;

        /// <summary>
        /// Creates an Alpaca client for one device of the local server.
        /// </summary>
        /// <typeparam name="TDevice">Alpaca client class to create.</typeparam>
        /// <param name="deviceNumber">Alpaca device number to talk to.</param>
        internal static TDevice CreateClient<TDevice>(int deviceNumber)
            where TDevice : AlpacaDeviceBaseClass, new()
        {
            int port = ReadPort();

            ShimLog.Write(
                "AlpacaEndpoint",
                $"Creating a {typeof(TDevice).Name} against http://{Host}:{port}, device {deviceNumber}");

            AlpacaConfiguration configuration = new AlpacaConfiguration
            {
                ServiceType = ServiceType.Http,
                IpAddressString = Host,
                PortNumber = port,
                RemoteDeviceNumber = deviceNumber,

                // Every COM object gets its own client number so the server can
                // tell two clients apart, which is what its connection counting
                // relies on to know when the mount can really be released.
                ClientNumber = NextClientNumber(),

                UserAgentProductName = "OnStepX.ComShim",
                UserAgentProductVersion = ShimVersion(),
            };

            return AlpacaClient.GetDevice<TDevice>(configuration);
        }

        /// <summary>
        /// Reads <c>server.port</c> from the shared settings file, falling back
        /// to the default whenever anything is missing or unreadable.
        /// </summary>
        /// <remarks>
        /// A COM client has no way to show a configuration error, so a broken
        /// or absent settings file must not stop activation. Trying the default
        /// port at least connects to a server left on its factory settings.
        /// </remarks>
        private static int ReadPort()
        {
            try
            {
                string path = SettingsPath();

                if (!File.Exists(path))
                {
                    return DefaultPort;
                }

                using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    if (!document.RootElement.TryGetProperty("server", out JsonElement server))
                    {
                        return DefaultPort;
                    }

                    if (!server.TryGetProperty("port", out JsonElement port))
                    {
                        return DefaultPort;
                    }

                    if (!port.TryGetInt32(out int value) || value <= 0 || value > 65535)
                    {
                        return DefaultPort;
                    }

                    return value;
                }
            }
            catch (Exception exception)
            {
                ShimLog.Write("AlpacaEndpoint", $"Could not read the settings file: {exception.Message}");
                return DefaultPort;
            }
        }

        /// <summary>URL of the Alpaca server's setup page.</summary>
        internal static string SetupUrl()
        {
            return $"http://{Host}:{ReadPort()}/";
        }

        /// <summary>
        /// Same location <c>SettingsStore.DefaultPath()</c> uses on Windows.
        /// </summary>
        private static string SettingsPath()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OnStepX ASCOM");

            return Path.Combine(directory, "settings.json");
        }

        private static uint NextClientNumber()
        {
            return (uint)Interlocked.Increment(ref _clientNumber);
        }

        private static string ShimVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0" : version.ToString();
        }
    }
}
