namespace Puck.Forge.Framework;

/// <summary>
/// The framework's sound seam: a driver EMITS the SM83 code for its hooks — one-time hardware setup at boot, a
/// once-per-frame tick from the main loop, fire-and-forget effects the game triggers by id, and
/// <see cref="EmitLibrary"/> for its subroutines beside the other module libraries. The driver's DATA rides the
/// game's manifest like every other table: <see cref="SoundTables.DefineIn"/> declares the catalog streams, and the
/// real driver (<see cref="ApuSoundDriver"/>) resolves them from the linked manifest through
/// <see cref="ApuSoundDriver.Bind"/> after <see cref="GameManifest.Link"/>. The call sites are wired into the kernel
/// and the games from day one, so swapping drivers touches no game logic; <see cref="NoOpSoundDriver"/> keeps a
/// cartridge silent.
/// </summary>
internal interface ISoundDriver {
    /// <summary>Emits the boot-time hardware setup (runs once, LCD off, after the work-RAM clear, before the main
    /// loop).</summary>
    /// <param name="emitter">The routine emitter.</param>
    void EmitBoot(Sm83Emitter emitter);
    /// <summary>Emits the once-per-frame driver tick (runs in the main loop, after the state dispatch).</summary>
    /// <param name="emitter">The routine emitter.</param>
    void EmitFrameTick(Sm83Emitter emitter);
    /// <summary>Emits a trigger for the driver-defined effect <paramref name="effectId"/> at the current point.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="effectId">The driver-defined effect id.</param>
    void EmitEffect(Sm83Emitter emitter, byte effectId);
    /// <summary>Emits the driver's library subroutines (called once, beside the other module libraries).</summary>
    /// <param name="emitter">The routine emitter.</param>
    void EmitLibrary(Sm83Emitter emitter);
}

/// <summary>The silent driver: boot masters the APU off (NR52 = 0), every other hook emits nothing.</summary>
internal sealed class NoOpSoundDriver : ISoundDriver {
    /// <inheritdoc/>
    public void EmitBoot(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.XorA();
        emitter.StoreAToHighPage(port: Hw.PortSoundOnOff);
    }

    /// <inheritdoc/>
    public void EmitFrameTick(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);
    }

    /// <inheritdoc/>
    public void EmitEffect(Sm83Emitter emitter, byte effectId) {
        ArgumentNullException.ThrowIfNull(emitter);
    }

    /// <inheritdoc/>
    public void EmitLibrary(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);
    }
}
