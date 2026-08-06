using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using OnStepX.ComShim.LocalServer;

namespace OnStepX.ComShim
{
    /// <summary>
    /// Entry point of the COM local server.
    /// </summary>
    /// <remarks>
    /// The shim never talks to a mount. It publishes the OnStepX devices as COM
    /// drivers and forwards every call to the Alpaca server on this machine, so
    /// that clients which only speak COM can drive hardware the Alpaca server
    /// owns. The process is normally started by COM itself, on demand, when a
    /// client activates one of the registered CLSIDs.
    /// </remarks>
    internal static class Program
    {
        private const string DialogTitle = "OnStepX COM Shim";

        private static readonly TimeSpan CollectionInterval = TimeSpan.FromSeconds(10);

        [STAThread]
        private static int Main(string[] args)
        {
            ShimLog.Start();
            ShimLog.Write("Main", $"Starting with arguments: {string.Join(" ", args)}");

            try
            {
                switch (ParseCommand(args))
                {
                    case Command.Register:
                        return RunElevated(DriverRegistration.Register, "-register", "register");

                    case Command.Unregister:
                        return RunElevated(DriverRegistration.Unregister, "-unregister", "unregister");

                    case Command.Embedding:
                        return Serve(startedByCom: true);

                    case Command.Serve:
                        return Serve(startedByCom: false);

                    default:
                        MessageBox.Show(
                            $"Unknown argument.{Environment.NewLine}"
                            + "Valid arguments are -embedding, -register (-regserver) and -unregister (-unregserver).",
                            DialogTitle,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Exclamation);
                        return 1;
                }
            }
            catch (Exception exception)
            {
                ShimLog.Write("Main", $"Unhandled exception: {exception}");

                MessageBox.Show(
                    exception.ToString(),
                    DialogTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);

                return 1;
            }
            finally
            {
                ShimLog.Write("Main", "Exiting");
                ShimLog.Stop();
            }
        }

        private enum Command
        {
            /// <summary>Started by COM to serve a client that just activated a CLSID.</summary>
            Embedding,

            /// <summary>Started by hand, with no client waiting.</summary>
            Serve,

            Register,

            Unregister,

            Unknown,
        }

        private static Command ParseCommand(string[] args)
        {
            if (args.Length == 0)
            {
                return Command.Serve;
            }

            // COM passes the switch with a leading dash, installers and users
            // tend to type a slash, and the -regserver spelling comes from the
            // VB6 servers this replaces. All of them have to work.
            switch (args[0].TrimStart('-', '/').ToLowerInvariant())
            {
                case "embedding":
                    return Command.Embedding;

                case "register":
                case "regserver":
                    return Command.Register;

                case "unregister":
                case "unregserver":
                    return Command.Unregister;

                default:
                    return Command.Unknown;
            }
        }

        /// <summary>
        /// Runs a registration action, restarting elevated first if needed.
        /// </summary>
        /// <remarks>
        /// Both actions write under <c>HKEY_CLASSES_ROOT</c>, which needs
        /// administrator rights. When the installer calls the shim it is
        /// already elevated and this is a straight call, so the prompt only
        /// appears for someone running the executable by hand.
        /// </remarks>
        private static int RunElevated(Action action, string argument, string description)
        {
            if (!IsAdministrator())
            {
                return Elevate(argument, description);
            }

            try
            {
                action();
                return 0;
            }
            catch (Exception exception)
            {
                ShimLog.Write("RunElevated", $"Failed to {description}: {exception}");

                MessageBox.Show(
                    $"Could not {description} the OnStepX COM drivers:{Environment.NewLine}{exception.Message}",
                    DialogTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);

                return 1;
            }
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static int Elevate(string argument, string description)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = argument,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = true,
                Verb = "runas",
            };

            try
            {
                ShimLog.Write("Elevate", $"Restarting elevated to {description}");

                using (Process elevated = Process.Start(startInfo))
                {
                    if (elevated == null)
                    {
                        return 1;
                    }

                    // Waiting lets an installer see the real result instead of
                    // a success from the process that only asked for elevation.
                    elevated.WaitForExit();
                    return elevated.ExitCode;
                }
            }
            catch (Win32Exception exception)
            {
                // The usual case here is the user dismissing the elevation
                // prompt, which is a refusal rather than a failure.
                ShimLog.Write("Elevate", $"Elevation was refused: {exception.Message}");

                MessageBox.Show(
                    $"The OnStepX COM drivers were not {description}ed because elevation was not granted.",
                    DialogTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return 1;
            }
        }

        /// <summary>
        /// Serves COM clients until nothing is using the server any more.
        /// </summary>
        /// <remarks>
        /// A process started by hand never closes itself: there is no client
        /// whose disconnection would justify it, so it waits invisibly until it
        /// is killed. Only a process COM started shuts down when it falls idle.
        /// </remarks>
        private static int Serve(bool startedByCom)
        {
            ServerState.CaptureMainThread(startedByCom);
            Thread.CurrentThread.Name = "OnStepX ComShim main thread";

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            List<ClassFactory> factories = new List<ClassFactory>();

            foreach (ServedDriver driver in ServedDrivers.All())
            {
                ClassFactory factory = new ClassFactory(driver.Type);

                if (!factory.Register())
                {
                    ShimLog.Write("Serve", $"Could not register the class factory for {driver.Type.Name}");

                    MessageBox.Show(
                        $"Could not register the class factory for {driver.Type.Name}.",
                        DialogTitle,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Stop);

                    RevokeAll(factories);
                    return 1;
                }

                factories.Add(factory);
            }

            ClassFactory.ResumeAll();
            ShimLog.Write("Serve", $"{factories.Count} class factories are live");

            GarbageCollector collector = new GarbageCollector(CollectionInterval);
            collector.Start();

            try
            {
                Application.Run(new MessagePumpForm());
            }
            finally
            {
                RevokeAll(factories);
                collector.Stop();
            }

            return 0;
        }

        private static void RevokeAll(List<ClassFactory> factories)
        {
            // Suspending first closes the window in which COM could hand out a
            // new object while the factories are being taken down one by one.
            ClassFactory.SuspendAll();

            foreach (ClassFactory factory in factories)
            {
                factory.Revoke();
            }
        }
    }
}
