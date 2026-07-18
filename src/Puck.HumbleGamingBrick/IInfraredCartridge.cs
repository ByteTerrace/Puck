using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// A cartridge whose mapper exposes an infrared window (HuC1/HuC3) and therefore shares the machine's one
/// <see cref="IInfrared"/> transceiver rather than modelling a private, always-dark IR line. The component factory hands
/// the machine's transceiver in after loading such a cartridge — the same TryAdd/inject seam the camera
/// (<see cref="ICameraSensor"/>) and tilt (<see cref="ITiltSensor"/>) cartridges use for their sensors.
/// </summary>
public interface IInfraredCartridge {
    /// <summary>Sets the machine's infrared transceiver the cartridge's IR window drives and reads.</summary>
    IInfrared? Infrared { set; }
}
