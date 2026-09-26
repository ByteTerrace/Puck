using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The light gun: a photodiode accessory wired to the machine's infrared receive line, so a game reads it through RP
/// (<c>0xFF56</c>) bit 1 or a HuC1/HuC3 IR window exactly as it reads a peer's light. The gun senses light while its
/// aim lands on the screen and the LCD pixel under the aim is lit. A host records one aim per input segment through
/// <see cref="Aim"/>, held constant until the next, like <see cref="IJoypad"/>'s held buttons; the gun's trigger is an
/// ordinary joypad button.
/// </summary>
public interface ILightGun {
    /// <summary>Gets a value indicating whether the gun's photodiode sees light: its aim lands on the screen and the
    /// pixel under the aim, as the LCD shows it right now, is at least half bright.</summary>
    bool SensesLight { get; }

    /// <summary>Records where the gun is aimed, held until the next call.</summary>
    /// <param name="pointer">The aim, as a fraction of the screen; <see cref="MachinePointer.Off"/> when the gun points
    /// away from the screen.</param>
    void Aim(MachinePointer pointer);
}
