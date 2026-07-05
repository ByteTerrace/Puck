namespace Puck.HumbleGamingBrick;

/// <summary>
/// A component whose model-derived capability gates can be re-pushed while the machine is running — the seam a live
/// DMG&lt;-&gt;CGB&lt;-&gt;AGB swap fans out through. An implementor updates ONLY its cached model-capability fields
/// (<c>m_supportsColor</c> / <c>m_isColor</c> and the PPU's derived color-path flags), re-derived idempotently from
/// the model and the immutable cartridge header. It must NOT re-seed boot-handoff state — the CPU register handoff,
/// the timer's DIV prediction, the APU boot-beep phase, the PPU's LCD position / palette-RAM whitening — all of which
/// belong to the constructor's power-on path alone; re-running them mid-flight would clobber live state and break
/// determinism. <see cref="ApplyModel"/> is called at construction-equivalent points only: a live swap and a snapshot
/// restore (where the model is re-derived after every component has loaded its own bytes).
/// </summary>
public interface IModeSwitchable {
    /// <summary>Re-derives this component's model-capability gates for <paramref name="model"/>. Idempotent; safe to
    /// call any number of times. Never touches emulated timeline state (registers, RAM, clock).</summary>
    /// <param name="model">The console model to gate for.</param>
    void ApplyModel(ConsoleModel model);
}
