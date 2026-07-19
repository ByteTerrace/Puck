namespace Puck.Forge.Framework;

/// <summary>
/// The facade a game builds itself against: one constructor wires the shared <see cref="Sm83Emitter"/>, the data
/// window, and every module (input, shadow OAM, background queue, PRNG, battery save, text, state machine, sound);
/// <see cref="BuildRom"/> then assembles the whole cartridge in the fixed order — prologue + VBlank handler, boot
/// (hardware bring-up → sound boot → save load → interrupt arming), the <c>halt</c>-synced main loop (input tick →
/// state dispatch → sound tick), the module library subroutines, the game's own library, and the state bodies —
/// and hands the routine + data to <see cref="FrameworkCartridge"/>. A game's job shrinks to: add data tables,
/// define states, emit game subroutines.
/// </summary>
internal sealed class GameFramework {
    private readonly RomTable m_dmaTrampoline;
    private readonly ISoundDriver m_sound;

    /// <summary>Creates the framework and its modules.</summary>
    /// <param name="fontTileBase">The tile id where the game places <see cref="TextModule.BuildFontTiles"/>.</param>
    /// <param name="saveDefaultPayload">The battery-save payload's ROM defaults (also fixes the payload size; ≤ 72 bytes).</param>
    /// <param name="saveVersion">The save block's version byte.</param>
    /// <param name="sound">The sound driver, or <see langword="null"/> for <see cref="NoOpSoundDriver"/>.</param>
    public GameFramework(byte fontTileBase, byte[] saveDefaultPayload, byte saveVersion, ISoundDriver? sound = null) {
        ArgumentNullException.ThrowIfNull(saveDefaultPayload);

        Emitter = new Sm83Emitter();
        Bg = new BgModule(emitter: Emitter);
        Text = new TextModule(emitter: Emitter, bg: Bg, fontTileBase: fontTileBase);
        Data = new RomDataBuilder(text: Text);
        Assets = new AssetLinker(data: Data);
        Input = new InputModule(emitter: Emitter);
        Oam = new OamManager(emitter: Emitter);
        Prng = new PrngModule(emitter: Emitter);
        States = new GameStateMachine(emitter: Emitter);
        m_dmaTrampoline = Data.Add(name: "dma-trampoline", bytes: FrameworkKernel.BuildDmaTrampolineBlob());
        Save = new SaveModule(emitter: Emitter, defaults: Data.Add(name: "save-defaults", bytes: saveDefaultPayload), version: saveVersion);
        Victory = new VictoryModule(emitter: Emitter);
        m_sound = (sound ?? new NoOpSoundDriver());
    }

    /// <summary>The shared routine emitter.</summary>
    public Sm83Emitter Emitter { get; }
    /// <summary>The asset linker (the composed tile bank, the palette slots, and <c>PBAK</c> relocation) —
    /// <see cref="GameManifest.Link"/> drives it declaratively; bespoke games may drive it directly.</summary>
    public AssetLinker Assets { get; }
    /// <summary>The cartridge data window.</summary>
    public RomDataBuilder Data { get; }
    /// <summary>The background queue and bulk-paint module.</summary>
    public BgModule Bg { get; }
    /// <summary>The input pipeline.</summary>
    public InputModule Input { get; }
    /// <summary>The shadow-OAM allocator.</summary>
    public OamManager Oam { get; }
    /// <summary>The PRNG.</summary>
    public PrngModule Prng { get; }
    /// <summary>The battery-save module.</summary>
    public SaveModule Save { get; }
    /// <summary>The 128-bit meta-victory module: a game calls <see cref="VictoryModule.EmitStoreShare"/> at its win
    /// edge (its <c>StateGameOver</c> enter) to converge this cabinet's SRAM win region on the host-seeded share.</summary>
    public VictoryModule Victory { get; }
    /// <summary>The font and text printers.</summary>
    public TextModule Text { get; }
    /// <summary>The game state machine.</summary>
    public GameStateMachine States { get; }
    /// <summary>The sound driver (games trigger effects through it so a real driver drops in later).</summary>
    public ISoundDriver Sound => m_sound;

    /// <summary>Assembles the finished cartridge.</summary>
    /// <param name="title">The header title.</param>
    /// <param name="bootSpec">The video/boot description.</param>
    /// <param name="emitGameLibrary">Emits the game's own shared subroutines (after the module libraries, before the
    /// state bodies), or <see langword="null"/> when the game keeps everything in its state callbacks.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public byte[] BuildRom(string title, FrameworkBootSpec bootSpec, Action<Sm83Emitter>? emitGameLibrary = null) {
        var bootLabel = Emitter.NewLabel();
        var mainLoop = Emitter.NewLabel();

        // The fixed prologue: jp boot at 0x0150, the VBlank handler at 0x0153 (the 0x0040 vector's target).
        FrameworkKernel.EmitPrologue(emitter: Emitter, bootLabel: bootLabel);

        // Boot: hardware bring-up (LCD off), the sound driver's setup, the save mirror load, the win-region reset, then
        // interrupts on. The victory-region reset runs AFTER the save load so a persisted <c>.sav</c> whose top-16 SRAM
        // bytes still carry a previous session's share can never auto-fire the meta gate on reboot — it is re-earned.
        Emitter.MarkLabel(label: bootLabel);
        FrameworkKernel.EmitBootPrologue(emitter: Emitter, spec: bootSpec, dmaTrampoline: m_dmaTrampoline);
        m_sound.EmitBoot(emitter: Emitter);
        Save.EmitLoad();
        VictoryModule.EmitBootReset(emitter: Emitter);
        FrameworkKernel.EmitBootEpilogue(emitter: Emitter, spec: bootSpec);

        // The main loop: one pass per displayed frame, woken by the VBlank handler.
        Emitter.MarkLabel(label: mainLoop);
        FrameworkKernel.EmitHaltWait(emitter: Emitter);
        Input.EmitTick();
        States.EmitFrameDispatch();
        m_sound.EmitFrameTick(emitter: Emitter);
        Emitter.JumpAbsolute(label: mainLoop);

        // The library: module subroutines, the game's own subroutines, then the state enter/tick bodies.
        Bg.EmitLibrary();
        Input.EmitLibrary();
        Prng.EmitLibrary();
        Save.EmitLibrary();
        Victory.EmitLibrary();
        Text.EmitLibrary();
        m_sound.EmitLibrary(emitter: Emitter);
        emitGameLibrary?.Invoke(Emitter);
        States.EmitLibrary();

        return FrameworkCartridge.Build(title: title, routine: Emitter.ToArray(baseAddress: Hw.EntryAddress), data: Data.ToArray());
    }
}
