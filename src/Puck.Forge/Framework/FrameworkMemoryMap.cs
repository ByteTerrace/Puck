namespace Puck.Forge.Framework;

/// <summary>
/// The framework's fixed work-RAM/high-RAM layout. Everything below <see cref="GameRam"/> belongs to the framework
/// (the VBlank handler, the input pipeline, the PRNG, the state machine, the background write queue, the save mirror,
/// and the shadow OAM page the HRAM DMA trampoline streams from); a game owns <see cref="GameRam"/> upward. The
/// stack grows down from <see cref="StackTop"/>.
/// </summary>
internal static class FrameworkMemoryMap {
    /// <summary>The low byte of the free-running 16-bit frame counter (incremented by the VBlank handler).</summary>
    public const ushort FrameCounter = 0xC000;
    /// <summary>The high byte of the frame counter.</summary>
    public const ushort FrameCounterHigh = 0xC001;
    /// <summary>The main loop's copy of the frame counter's low byte — the halt-wait spins until they differ.</summary>
    public const ushort LastFrame = 0xC002;

    /// <summary>The active-high held-button byte the game reads (scripted during an attract override).</summary>
    public const ushort InputHeld = 0xC003;
    /// <summary>The newly-pressed edges this frame (<c>held &amp; ~previous</c>).</summary>
    public const ushort InputPressed = 0xC004;
    /// <summary>Last frame's held byte (the edge detector's memory).</summary>
    public const ushort InputPrevious = 0xC005;
    /// <summary>The RAW hardware joypad read — real buttons even while a script overrides <see cref="InputHeld"/>.</summary>
    public const ushort InputRaw = 0xC006;

    /// <summary>The low byte of the 16-bit LCG PRNG state.</summary>
    public const ushort PrngState = 0xC007;
    /// <summary>The high byte of the PRNG state (also the LCG's output byte).</summary>
    public const ushort PrngStateHigh = 0xC008;

    /// <summary>The current state id of the game state machine.</summary>
    public const ushort GameState = 0xC009;
    /// <summary>The requested state id, consumed at the next frame dispatch; <c>0xFF</c> = none.</summary>
    public const ushort PendingState = 0xC00A;

    /// <summary>The number of queued background-map writes (drained by the VBlank handler).</summary>
    public const ushort VramQueueCount = 0xC00B;
    /// <summary>The queue entries: <see cref="VramQueueCapacity"/> × (address-high, address-low, tile).</summary>
    public const ushort VramQueue = 0xC00C;
    /// <summary>The queue capacity; a push beyond it is dropped.</summary>
    public const int VramQueueCapacity = 24;

    /// <summary>Non-zero while an input script (attract mode) overrides the held byte.</summary>
    public const ushort ScriptOverride = 0xC054;
    /// <summary>The low byte of the script read pointer.</summary>
    public const ushort ScriptPointer = 0xC055;
    /// <summary>The high byte of the script read pointer.</summary>
    public const ushort ScriptPointerHigh = 0xC056;
    /// <summary>Frames left before the script advances to its next (buttons, frames) pair.</summary>
    public const ushort ScriptFramesLeft = 0xC057;
    /// <summary>Set to 1 when the script reaches its <c>0xFF</c> terminator.</summary>
    public const ushort ScriptEnded = 0xC058;
    /// <summary>The buttons byte of the script's current pair.</summary>
    public const ushort ScriptButtons = 0xC059;

    /// <summary>The battery-save payload's work-RAM mirror (up to <see cref="SaveMirrorCapacity"/> bytes).</summary>
    public const ushort SaveMirror = 0xC060;
    /// <summary>The mirror capacity in bytes.</summary>
    public const int SaveMirrorCapacity = 72;

    /// <summary>The music sequencer's read-pointer low byte (high byte zero = no music playing).</summary>
    public const ushort SoundMusicPointer = 0xC0A8;
    /// <summary>The music sequencer's read-pointer high byte.</summary>
    public const ushort SoundMusicPointerHigh = 0xC0A9;
    /// <summary>The music pattern's start-address low byte (the loop restart target).</summary>
    public const ushort SoundMusicStart = 0xC0AA;
    /// <summary>The music pattern's start-address high byte.</summary>
    public const ushort SoundMusicStartHigh = 0xC0AB;
    /// <summary>Frames left before the music sequencer advances to its next event.</summary>
    public const ushort SoundMusicWait = 0xC0AC;
    /// <summary>The pulse SFX voice's read-pointer low byte (high byte zero = idle).</summary>
    public const ushort SoundPulsePointer = 0xC0AD;
    /// <summary>The pulse SFX voice's read-pointer high byte.</summary>
    public const ushort SoundPulsePointerHigh = 0xC0AE;
    /// <summary>Frames left before the pulse SFX voice advances to its next step.</summary>
    public const ushort SoundPulseWait = 0xC0AF;
    /// <summary>The noise SFX voice's read-pointer low byte (high byte zero = idle).</summary>
    public const ushort SoundNoisePointer = 0xC0B0;
    /// <summary>The noise SFX voice's read-pointer high byte.</summary>
    public const ushort SoundNoisePointerHigh = 0xC0B1;
    /// <summary>Frames left before the noise SFX voice advances to its next step.</summary>
    public const ushort SoundNoiseWait = 0xC0B2;

    /// <summary>Framework scratch (0xC0B4..0xC0EF), free for module-internal temporaries.</summary>
    public const ushort Scratch = 0xC0B4;

    /// <summary>The 16-byte "victory share" source slot (0xC0F0..0xC0FF): the host SEEDS this cabinet's authored 128-bit
    /// meta victory share here at boot (a per-cabinet <see cref="Sm83Emitter"/>-invisible poke, like the mode-swap boot
    /// shim), and <see cref="VictoryModule"/> copies it verbatim into the top-16 SRAM win region on the game's win edge.
    /// It is DELIBERATELY EXCLUDED from the boot work-RAM clear (the block-fill splits around it in
    /// <see cref="FrameworkKernel.EmitBootPrologue"/>) so the host's seed survives the game's boot; a game never reads or
    /// writes it (games own <see cref="GameRam"/> upward). A cabinet with no seeded share leaves it all-zero — the game
    /// then converges the region on zero, which the room XOR never mistakes for a real share group.</summary>
    public const ushort VictoryShareSource = 0xC0F0;
    /// <summary>The victory-share source slot's width (a 128-bit gate = 16 bytes; matches <c>VictoryGate.RegionByteCount</c>).</summary>
    public const int VictoryShareByteCount = 16;

    /// <summary>The 160-byte shadow OAM page the HRAM trampoline DMA-copies to the hardware OAM every VBlank.</summary>
    public const ushort ShadowOam = 0xC100;

    /// <summary>The first game-owned work-RAM byte; the framework never touches this page or above.</summary>
    public const ushort GameRam = 0xC200;

    /// <summary>Where the 10-byte OAM DMA trampoline is copied at boot (HRAM stays readable during the transfer).</summary>
    public const ushort DmaTrampoline = 0xFF80;
    /// <summary>The initial stack pointer.</summary>
    public const ushort StackTop = 0xFFFE;
}
