namespace Puck.Abstractions.Machines;

/// <summary>
/// Optional haptic-feedback capability for an <see cref="IScreenMachine"/> whose cartridge drives a rumble motor — the
/// neutral seam a controller-haptics sink drains, mirroring <see cref="IAudioMachine"/>'s optional-capability shape
/// exactly (attach-at-construction, zero cost when no consumer asked for it). The motor line is presentation-side
/// feedback, never state-affecting: a machine's simulation state never depends on whether (or how) a consumer reads
/// this, so it carries no snapshot state of its own here — the underlying LATCH the value is sampled from (a cartridge
/// register bit) is ordinary snapshot state on the core, exactly like every other mapper register.
/// </summary>
public interface IFeedbackMachine {
    /// <summary>Gets the cartridge's current motor level, 0..1, sampled once per completed step. Most modeled hardware
    /// (an MBC5 rumble variant, a GBA rumble GPIO pin) is on/off only, so the value is typically exactly 0 or 1; a
    /// future PWM-capable device may report intermediate levels. Always 0 on a machine whose loaded cartridge has no
    /// rumble hardware.</summary>
    float MotorLevel { get; }
}
