using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OnStepX.ComShim.LocalServer
{
    /// <summary>
    /// A driver class this local server serves, together with everything the
    /// registry and the ASCOM Profile need to know about it.
    /// </summary>
    internal sealed class ServedDriver
    {
        internal ServedDriver(Type type, ServedDriverAttribute declaration)
        {
            Type = type;
            ChooserName = declaration.ChooserName;
            DeviceType = declaration.DeviceType;
            ClassId = Marshal.GenerateGuidForType(type).ToString("B");
            ProgId = Marshal.GenerateProgIdForType(type);
        }

        internal Type Type { get; }

        internal string ChooserName { get; }

        internal ASCOM.Common.DeviceTypes DeviceType { get; }

        /// <summary>CLSID in registry form, braces included.</summary>
        internal string ClassId { get; }

        /// <summary>ProgID clients see in the ASCOM Chooser.</summary>
        internal string ProgId { get; }
    }

    /// <summary>
    /// Finds the driver classes contained in this executable.
    /// </summary>
    /// <remarks>
    /// Reflection over the executing assembly, rather than a hardcoded list,
    /// keeps registration and activation from ever disagreeing about which
    /// drivers exist: both go through here.
    /// </remarks>
    internal static class ServedDrivers
    {
        private static readonly object Gate = new object();

        private static List<ServedDriver> _drivers;

        /// <summary>Returns the served drivers, discovering them on first use.</summary>
        internal static IReadOnlyList<ServedDriver> All()
        {
            lock (Gate)
            {
                if (_drivers != null)
                {
                    return _drivers;
                }

                _drivers = new List<ServedDriver>();

                foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
                {
                    ServedDriverAttribute declaration =
                        (ServedDriverAttribute)Attribute.GetCustomAttribute(type, typeof(ServedDriverAttribute));

                    if (declaration == null)
                    {
                        continue;
                    }

                    ServedDriver driver = new ServedDriver(type, declaration);
                    _drivers.Add(driver);

                    ShimLog.Write(
                        "ServedDrivers",
                        $"Found {driver.Type.Name}: ProgID {driver.ProgId}, CLSID {driver.ClassId}");
                }

                return _drivers;
            }
        }
    }
}
