namespace Puck.HumbleGamingBrick.Forge.Framework;

/// <summary>
/// The framework's fixed work-RAM/high-RAM layout. Everything below <see cref="GameRam"/> belongs to the framework
/// (the VBlank handler, the input pipeline, the PRNG, the state machine, the background write queue, the save mirror,
/// and the shadow OAM page the HRAM DMA trampoline streams from); a game owns <see cref="GameRam"/> upward. The
/// stack grows down from <see cref="StackTop"/>.
/// </summary>
public static class FrameworkMemoryMap {
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
    /// <summary>The raw hardware joypad read — real buttons even while a script overrides <see cref="InputHeld"/>.</summary>
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
    /// <summary>
    /// The queue entries: <see cref="VramQueueCapacity"/> × (address-high, address-low, tile, attribute). The
    /// attribute rides in the entry rather than in a queue of its own because the drain walks the run twice — once
    /// per video-memory bank — and one pointer per pass is what the processor has registers for.
    /// </summary>
    public const ushort VramQueue = 0xC00C;
    /// <summary>The queue capacity; a push beyond it is dropped.</summary>
    public const int VramQueueCapacity = 24;
    /// <summary>The bytes one queue entry occupies.</summary>
    public const int VramQueueEntrySize = 4;
    /// <summary>Non-zero while an input script (attract mode) overrides the held byte.</summary>
    public const ushort ScriptOverride = 0xC06C;
    /// <summary>The low byte of the script read pointer.</summary>
    public const ushort ScriptPointer = 0xC06D;
    /// <summary>The high byte of the script read pointer.</summary>
    public const ushort ScriptPointerHigh = 0xC06E;
    /// <summary>Frames left before the script advances to its next (buttons, frames) pair.</summary>
    public const ushort ScriptFramesLeft = 0xC06F;
    /// <summary>Set to 1 when the script reaches its <c>0xFF</c> terminator.</summary>
    public const ushort ScriptEnded = 0xC070;
    /// <summary>The buttons byte of the script's current pair.</summary>
    public const ushort ScriptButtons = 0xC071;
    /// <summary>The battery-save payload's work-RAM mirror (up to <see cref="SaveMirrorCapacity"/> bytes).</summary>
    public const ushort SaveMirror = 0xC074;
    /// <summary>The mirror capacity in bytes.</summary>
    public const int SaveMirrorCapacity = 72;
    /// <summary>
    /// The four sound voices' sequencer state: <see cref="SoundVoiceCount"/> blocks of
    /// <see cref="SoundVoiceStateSize"/> bytes in the order pulse one, pulse two, wave, noise — read pointer low and
    /// high, loop-start low and high, then the frames left before the voice advances.
    /// </summary>
    /// <remarks>
    /// Every voice carries a loop start, and that is the only thing separating music from a one-shot: a voice whose
    /// loop-start high byte is zero stops and mutes at its stream's terminator, and one carrying a start rewinds to
    /// it. Nothing about a voice reserves it for either, so a document decides which voices its music occupies.
    /// </remarks>
    public const ushort SoundVoiceState = 0xC0BC;
    /// <summary>The number of sequencer voices.</summary>
    public const int SoundVoiceCount = 4;
    /// <summary>One voice's state size: pointer, loop start, wait.</summary>
    public const int SoundVoiceStateSize = 5;
    /// <summary>Pulse one's sequencer state.</summary>
    public const ushort SoundPulse1State = SoundVoiceState;
    /// <summary>Pulse two's sequencer state.</summary>
    public const ushort SoundPulse2State = SoundVoiceState + SoundVoiceStateSize;
    /// <summary>The wave voice's sequencer state.</summary>
    public const ushort SoundWaveState = SoundVoiceState + (2 * SoundVoiceStateSize);
    /// <summary>The noise voice's sequencer state.</summary>
    public const ushort SoundNoiseState = SoundVoiceState + (3 * SoundVoiceStateSize);
    /// <summary>A voice block's read-pointer low byte (its high byte is the next one; zero means idle).</summary>
    public const int SoundVoicePointerOffset = 0;
    /// <summary>A voice block's loop-start low byte (its high byte is the next one; zero means one-shot).</summary>
    public const int SoundVoiceStartOffset = 2;
    /// <summary>A voice block's wait counter.</summary>
    public const int SoundVoiceWaitOffset = 4;
    /// <summary>Framework scratch (0xC0D0..0xC0EF), free for module-internal temporaries.</summary>
    public const ushort Scratch = 0xC0D0;
    /// <summary>The 16-byte "victory share" source slot (0xC0F0..0xC0FF): the host seeds this cabinet's authored 128-bit
    /// meta victory share here at boot (a per-cabinet <see cref="Sm83Emitter"/>-invisible poke, like the mode-swap boot
    /// shim), and <see cref="VictoryModule"/> copies it verbatim into the top-16 SRAM win region on the game's win edge.
    /// It is deliberately excluded from the boot work-RAM clear (the block-fill splits around it in
    /// <see cref="FrameworkKernel.EmitBootPrologue"/>) so the host's seed survives the game's boot; a game never reads or
    /// writes it (games own <see cref="GameRam"/> upward). A cabinet with no seeded share leaves it all-zero — the game
    /// then converges the region on zero, which the room XOR never mistakes for a real share group.</summary>
    public const ushort VictoryShareSource = 0xC0F0;
    /// <summary>The victory-share source slot's width: a 128-bit gate = 16 bytes. This was pinned to a host-side
    /// <c>VictoryGate.RegionByteCount</c> that no longer exists (see <see cref="VictoryModule"/>), so the width is
    /// self-standing now and a future reader must match it rather than the reverse.</summary>
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
