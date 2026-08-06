namespace OnStepX.Core.Discovery;

/// <summary>
/// Enumerates the system's serial ports with whatever metadata can be
/// obtained.
/// </summary>
/// <remarks>
/// This sits behind an interface so that all the classification and
/// probing logic can be tested with an injected enumerator, without
/// depending on the hardware present on the machine running the tests.
/// </remarks>
public interface ISerialPortEnumerator
{
    /// <summary>Description of the enumeration method, for traces.</summary>
    string Description { get; }

    /// <summary>Enumerates the ports. Never throws: on failure, returns whatever it can.</summary>
    IReadOnlyList<SerialPortInfo> Enumerate();
}
