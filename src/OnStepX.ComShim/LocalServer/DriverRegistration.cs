using System;
using System.IO;
using System.Windows.Forms;
using ASCOM.Com;
using Microsoft.Win32;

namespace OnStepX.ComShim.LocalServer
{
    /// <summary>
    /// Writes and removes the registry and ASCOM Profile entries that make the
    /// served drivers reachable from COM clients.
    /// </summary>
    /// <remarks>
    /// This is done by hand instead of with <c>regasm</c> on purpose: regasm
    /// would write <c>InProcServer32</c> entries, which tell COM to load the
    /// assembly inside the client process and would defeat the whole point of
    /// a local server.
    /// </remarks>
    internal static class DriverRegistration
    {
        /// <summary>
        /// AppID shared by every driver in this executable, so COM keeps a
        /// single instance of the process for all of them.
        /// </summary>
        /// <remarks>
        /// Fixed for good. Changing it would orphan the registration on every
        /// machine that already installed the shim.
        /// </remarks>
        private const string AppId = "{258e2319-c146-4d13-b735-d3e03a98db48}";

        /// <summary>
        /// Component category that marks a CLSID as an ASCOM driver. Defined by
        /// the ASCOM Platform, not by us.
        /// </summary>
        private const string AscomDriverCategory = "{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}";

        /// <summary>Creates every registry and Profile entry. Needs administrator rights.</summary>
        internal static void Register()
        {
            string executablePath = Application.ExecutablePath;
            string executableName = Path.GetFileName(executablePath);

            ShimLog.Write("Register", $"Registering {executablePath}");

            using (RegistryKey appIdKey = Registry.ClassesRoot.CreateSubKey($"APPID\\{AppId}"))
            {
                appIdKey.SetValue(null, "OnStepX ASCOM COM Shim");
                appIdKey.SetValue("AppID", AppId);
                appIdKey.SetValue("AuthenticationLevel", 1, RegistryValueKind.DWord);

                // Without this a client running elevated and one running
                // normally would each get their own copy of the server, and
                // the two copies would fight over the same mount.
                appIdKey.SetValue("RunAs", "Interactive User", RegistryValueKind.String);
            }

            using (RegistryKey executableKey = Registry.ClassesRoot.CreateSubKey($"APPID\\{executableName}"))
            {
                executableKey.SetValue("AppID", AppId);
            }

            foreach (ServedDriver driver in ServedDrivers.All())
            {
                ShimLog.Write("Register", $"Registering {driver.ProgId} ({driver.ClassId})");

                using (RegistryKey classKey = Registry.ClassesRoot.CreateSubKey($"CLSID\\{driver.ClassId}"))
                {
                    classKey.SetValue(null, driver.ChooserName);
                    classKey.SetValue("AppId", AppId);

                    using (RegistryKey categoriesKey = classKey.CreateSubKey("Implemented Categories"))
                    {
                        categoriesKey.CreateSubKey(AscomDriverCategory).Dispose();
                    }

                    using (RegistryKey progIdKey = classKey.CreateSubKey("ProgId"))
                    {
                        progIdKey.SetValue(null, driver.ProgId);
                    }

                    classKey.CreateSubKey("Programmable").Dispose();

                    using (RegistryKey localServerKey = classKey.CreateSubKey("LocalServer32"))
                    {
                        localServerKey.SetValue(null, executablePath);
                    }
                }

                using (RegistryKey progIdKey = Registry.ClassesRoot.CreateSubKey(driver.ProgId))
                {
                    progIdKey.SetValue(null, driver.ChooserName);

                    using (RegistryKey classIdKey = progIdKey.CreateSubKey("CLSID"))
                    {
                        classIdKey.SetValue(null, driver.ClassId);
                    }
                }

                Profile.Register(driver.DeviceType, driver.ProgId, driver.ChooserName);
            }

            ShimLog.Write("Register", "Registration finished");
        }

        /// <summary>Removes every entry <see cref="Register"/> created. Needs administrator rights.</summary>
        internal static void Unregister()
        {
            string executableName = Path.GetFileName(Application.ExecutablePath);

            ShimLog.Write("Unregister", "Removing registration");

            foreach (ServedDriver driver in ServedDrivers.All())
            {
                ShimLog.Write("Unregister", $"Removing {driver.ProgId} ({driver.ClassId})");

                DeleteKey($"{driver.ProgId}\\CLSID");
                DeleteKey(driver.ProgId);

                DeleteKey($"CLSID\\{driver.ClassId}\\Implemented Categories\\{AscomDriverCategory}");
                DeleteKey($"CLSID\\{driver.ClassId}\\Implemented Categories");
                DeleteKey($"CLSID\\{driver.ClassId}\\ProgId");
                DeleteKey($"CLSID\\{driver.ClassId}\\Programmable");
                DeleteKey($"CLSID\\{driver.ClassId}\\LocalServer32");
                DeleteKey($"CLSID\\{driver.ClassId}");

                try
                {
                    Profile.UnRegister(driver.DeviceType, driver.ProgId);
                }
                catch (Exception exception)
                {
                    // The Profile entry may already be gone, for instance when
                    // an uninstall follows a failed install. It is not a reason
                    // to leave the rest of the registration behind.
                    ShimLog.Write("Unregister", $"Could not remove the Profile entry: {exception.Message}");
                }
            }

            DeleteKey($"APPID\\{executableName}");
            DeleteKey($"APPID\\{AppId}");

            ShimLog.Write("Unregister", "Registration removed");
        }

        private static void DeleteKey(string path)
        {
            try
            {
                Registry.ClassesRoot.DeleteSubKey(path, false);
            }
            catch (Exception exception)
            {
                ShimLog.Write("Unregister", $"Could not remove HKCR\\{path}: {exception.Message}");
            }
        }
    }
}
