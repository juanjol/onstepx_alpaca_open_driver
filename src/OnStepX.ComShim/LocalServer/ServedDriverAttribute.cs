using System;
using ASCOM.Common;

namespace OnStepX.ComShim.LocalServer
{
    /// <summary>
    /// Marks a class as one of the drivers this local server hands out to COM
    /// clients.
    /// </summary>
    /// <remarks>
    /// Discovery is driven by this attribute rather than by naming convention
    /// because the assembly also contains COM visible helper types (the rate
    /// collections a telescope has to return) that must never be registered as
    /// drivers. The device type travels inside the attribute instead of being
    /// derived from the class name, since the ASCOM Profile API takes the enum
    /// and a name based lookup would turn a typo into a driver silently
    /// missing from the Chooser.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class ServedDriverAttribute : Attribute
    {
        /// <summary>Declares the Chooser entry for a driver class.</summary>
        /// <param name="chooserName">Name shown in the ASCOM Chooser.</param>
        /// <param name="deviceType">ASCOM device type the class implements.</param>
        public ServedDriverAttribute(string chooserName, DeviceTypes deviceType)
        {
            ChooserName = chooserName;
            DeviceType = deviceType;
        }

        /// <summary>Name shown to the user in the ASCOM Chooser.</summary>
        public string ChooserName { get; }

        /// <summary>ASCOM device type, used to register the Profile entry.</summary>
        public DeviceTypes DeviceType { get; }
    }
}
