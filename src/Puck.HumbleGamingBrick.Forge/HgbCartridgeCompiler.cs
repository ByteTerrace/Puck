using Puck.Assets.Documents;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

using Puck.State;

namespace Puck.HumbleGamingBrick.Forge;

/// <summary>Compiles cartridge documents into native CGB machine code and graphics.</summary>
/// <remarks>
/// Work-RAM layout above <c>FrameworkMemoryMap.GameRam</c>:
/// 0xC200..0xC27F variables, 0xC280 prior held input, 0xC281 operand spill, 0xC282 discard sink, 0xC283 the frame's scene snapshot, 0xC284..0xDFFF arrays, spanning the fixed page and the switchable bank pinned at boot.
/// </remarks>
public sealed class HgbCartridgeCompiler : ICartridgeCompiler {
    private const ushort ArrayBaseAddress = 0xC284;
    private const ushort ArrayLimitAddress = 0xE000;
    private const ushort HeldInputAddress = 0xC280;
    private const ushort ScratchAddress = 0xC281;
    private const ushort VoidAddress = 0xC282;
    // The declared scene variable, read once before any rule evaluates. Every guard on it compares against THIS byte,
    // so a rule that writes the variable changes which scene runs next frame and never opens a second one in this.
    private const ushort SceneAddress = 0xC283;
    /// <summary>The mid-picture walk's cursor; the row triples follow it.</summary>
    private const ushort RasterCursorAddress = FrameworkMemoryMap.Scratch;
    /// <summary>The first row triple: scanline, horizontal scroll, vertical scroll.</summary>
    private const ushort RasterRowAddress = FrameworkMemoryMap.Scratch + 1;

    /// <inheritdoc />
    public string EngineId => "gaming-brick";

    /// <inheritdoc />
    public string Target => "cgb";

    // The document names voices; the driver numbers them.
    private static SoundVoice Voice(string name) => name switch {
        AudioEffectDocument.VoiceNoise => SoundVoice.Noise,
        AudioEffectDocument.VoiceWave => SoundVoice.Wave,
        AudioEffectDocument.VoicePulse2 => SoundVoice.Pulse2,
        _ => SoundVoice.Pulse1,
    };

    /// <inheritdoc />
    public CartridgeCompilation Compile(CartridgeDocument document) {
        var source = CartridgeDocuments.Canonicalize(document: document);
        document = source.Document;
        if (document.Target != Target) {
            throw new ArgumentException(message: "This compiler requires target cgb.", paramName: nameof(document));
        }

        var layout = new CartridgeStateLayout(document);
        var variables = new Dictionary<string, uint>(comparer: StringComparer.Ordinal);
        var widths = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        foreach (var (name, slot) in layout.Slots) {
            variables[name] = (uint)FrameworkMemoryMap.GameRam + (uint)slot.Offset;
            widths[name] = slot.Width;
        }

        var arrays = new Dictionary<string, uint>(comparer: StringComparer.Ordinal);
        var lengths = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var next = (uint)ArrayBaseAddress;
        foreach (var array in document.Arrays) {
            arrays[key: array.Name] = next;
            lengths[key: array.Name] = array.Initial.Length;
            next += (uint)array.Initial.Length;
        }
        if (next > ArrayLimitAddress) {
            throw new ArgumentException(message: $"Arrays need {next - ArrayBaseAddress} bytes; work RAM holds {ArrayLimitAddress - ArrayBaseAddress}.", paramName: nameof(document));
        }

        // The payload is the declared variables then the declared arrays, each mapped to its run of mirror bytes.
        var persisted = new List<(uint Address, int Length)>();
        var defaults = new List<byte>();
        if (document.Save is { } declared) {
            foreach (var name in declared.Variables) {
                persisted.Add(item: (variables[name], layout.Slots[name].Width));
                defaults.AddRange(layout.InitialBytes(name));
            }

            foreach (var name in declared.Arrays) {
                var array = document.Arrays.First(predicate: candidate => candidate.Name == name);
                persisted.Add(item: (arrays[name], array.Initial.Length));
                defaults.AddRange(collection: array.Initial.Select(selector: static value => (byte)value));
            }
        }

        var emitter = new Sm83Emitter();
        var arithmetic = new Sm83CartridgeArithmetic(emitter: emitter);
        var background = new BgModule(emitter: emitter);
        var data = new RomDataBuilder(text: new TextModule(emitter: emitter, bg: background, fontTileBase: 0));
        var input = new InputModule(emitter: emitter);
        var backgroundPalettes = data.Add(name: "bg-palettes", bytes: CartridgeGraphics.PaletteBank(bank: document.Palettes.Background));
        var objectPalettes = data.Add(name: "obj-palettes", bytes: CartridgeGraphics.PaletteBank(bank: document.Palettes.Object));
        // Copied into video memory once at boot, so these may ride in a switchable bank when the fixed window is full.
        var banked = new Sm83BankedData();
        var tileBytes = CartridgeGraphics.Tiles(document: document);
        var tilePlacement = Place(name: "tiles", bytes: tileBytes);
        var mapPlacement = Place(name: "map", bytes: document.Map.Select(selector: static tile => (byte)tile).ToArray());
        // Video bank one holds one attribute byte per cell; the low three bits select the background palette.
        var attributePlacement = Place(name: "bg-attributes", bytes: (document.MapPalettes ?? new int[1024]).Select(selector: static entry => (byte)(entry & 0x07)).ToArray());
        var fadesToBlack = data.Add(name: "fade-black", bytes: FadeBanks(document: document, toWhite: false));
        var fadesToWhite = data.Add(name: "fade-white", bytes: FadeBanks(document: document, toWhite: true));
        var windowMap = document.Window is null ? default : Place(name: "window-map", bytes: document.Window.Map.Select(selector: static tile => (byte)tile).ToArray());
        var windowAttributes = document.Window is null ? default : Place(name: "window-attributes", bytes: (document.Window.MapPalettes ?? new int[1024]).Select(selector: static entry => (byte)(entry & 0x07)).ToArray());
        var trampoline = data.Add(name: "dma", bytes: FrameworkKernel.BuildDmaTrampolineBlob());
        var screens = document.Screens.ToDictionary(
            keySelector: static screen => screen.Name,
            elementSelector: screen => (
                Table: data.Add(name: $"screen-{screen.Name}", bytes: screen.Tiles.Select(selector: static tile => (byte)tile).ToArray()),
                Shades: screen.Palettes is { } palettes ? data.Add(name: $"screen-{screen.Name}-palettes", bytes: palettes.Select(selector: static shade => (byte)(shade & 0x07)).ToArray()) : (RomTable?)null,
                screen.Width,
                Height: screen.Tiles.Length / screen.Width),
            comparer: StringComparer.Ordinal);
        // Arrays pack contiguously from ArrayBaseAddress, so one ROM table seeds every one of them.
        var arrayInitial = document.Arrays.SelectMany(selector: static array => array.Initial.Select(selector: static value => (byte)value)).ToArray();
        var arrayData = data.Add(name: "arrays", bytes: arrayInitial.Length == 0 ? [0] : arrayInitial);
        // A sound is a list of voice parts: a track has one per voice it occupies, an effect exactly one.
        var sounds = document.Sounds.ToDictionary(
            keySelector: static sound => sound.Name,
            elementSelector: sound => sound.Music is { } music
                ? (IsMusic: true, Parts: music.Select(selector: part => (
                    Table: data.Add(name: $"music-{sound.Name}-{part.Voice}", bytes: AudioDocumentCompiler.CompileMusicVoice(document: AudioCanonicalizer.Normalize(document: part.Part), voice: part.Voice)),
                    Voice: Voice(name: part.Voice),
                    Pattern: Waveform(levels: part.Waveform, name: $"wave-{sound.Name}-{part.Voice}"))).ToArray())
                : (IsMusic: false, Parts: new[] { (
                    Table: data.Add(name: $"effect-{sound.Name}", bytes: AudioDocumentCompiler.CompileEffect(effect: sound.Effect!, frames: sound.Frames!.Value)),
                    Voice: Voice(name: sound.Effect!.Voice ?? AudioEffectDocument.VoicePulse1),
                    Pattern: Waveform(levels: sound.Waveform, name: $"wave-{sound.Name}")) }),
            comparer: StringComparer.Ordinal);

        // Every voice any track occupies, which is what a stop step silences.
        var musicVoices = document.Sounds
            .SelectMany(selector: static sound => sound.Music ?? [])
            .Select(selector: static part => Voice(name: part.Voice))
            .Distinct()
            .Order()
            .ToArray();

        RomTable? Waveform(int[]? levels, string name) => levels is null
            ? null
            : data.Add(name: name, bytes: Enumerable.Range(start: 0, count: 16).Select(selector: index => (byte)((levels[index * 2] << 4) | levels[(index * 2) + 1])).ToArray());
        var audio = document.Sounds.Length == 0 ? null : new ApuSoundDriver();
        var save = document.Save is null ? null : new SaveModule(emitter: emitter, defaults: data.Add(name: "save-defaults", bytes: [.. defaults]), version: (byte)document.Save.Version);
        var boot = emitter.NewLabel();
        var loop = emitter.NewLabel();
        var rasterHandler = emitter.NewLabel();
        var spec = new FrameworkBootSpec(
            BgPalettes: backgroundPalettes, ObjPalettes: objectPalettes,
            Tiles: new RomTable(Address: tilePlacement.Address, Length: tileBytes.Length),
            TileByteCount: tileBytes.Length,
            InitialMap: new RomTable(Address: mapPlacement.Address, Length: 0x400),
            Lcdc: BaseControl(document: document), InitialState: 0,
            BgAttributes: new RomTable(Address: attributePlacement.Address, Length: 0x400),
            SelectTileBank: Select(placement: tilePlacement),
            SelectMapBank: Select(placement: mapPlacement),
            SelectAttributeBank: Select(placement: attributePlacement),
            RestoreFixedBank: banked.Banks.Count == 0 ? null : static emitter => Sm83BankedData.EmitSelect(emitter: emitter, bank: 1),
            RasterRows: document.Raster.Length);
        FrameworkKernel.EmitPrologue(emitter: emitter, bootLabel: boot, rasterRearm: document.Raster.Length == 0 ? null : EmitRasterRearm);
        emitter.MarkLabel(label: boot);
        FrameworkKernel.EmitBootPrologue(emitter: emitter, spec: spec, dmaTrampoline: trampoline);
        Initialize();
        InitializeWindow();
        audio?.EmitBoot(emitter: emitter);
        FrameworkKernel.EmitBootEpilogue(emitter: emitter, spec: spec);
        emitter.MarkLabel(label: loop);
        FrameworkKernel.EmitHaltWait(emitter: emitter);
        emitter.LoadAFromAddress(address: FrameworkMemoryMap.InputHeld);
        emitter.StoreAToAddress(address: HeldInputAddress);
        input.EmitTick();
        audio?.EmitFrameTick(emitter: emitter);
        Frame();
        emitter.JumpAbsolute(label: loop);
        if (document.Raster.Length != 0) {
            EmitRasterHandler();
        }

        input.EmitLibrary();
        arithmetic.EmitLibrary();
        background.EmitLibrary();
        save?.EmitLibrary();
        audio?.EmitLibrary(emitter: emitter);
        var routine = emitter.ToArray(baseAddress: Hw.EntryAddress);
        byte[] rom;
        try {
            rom = FrameworkCartridge.Build(
            title: document.Title, routine: routine, data: data.ToArray(), banks: banked.Banks,
            clock: document.Clock is not null,
            // A forged cartridge is this house's, and says so on the screen the machine shows before it hands over.
            logo: CartridgeHeader.HouseLogo.ToArray(),
            statHandlerAddress: document.Raster.Length == 0 ? (ushort)0 : emitter.LabelAddress(label: rasterHandler, baseAddress: Hw.EntryAddress));
        } catch (Exception overrun) when (overrun is ArgumentException or ArgumentOutOfRangeException) {
            throw new CartridgeCapacityException(message: $"The document does not fit the Color machine's cartridge: {overrun.Message}", innerException: overrun);
        }

        return new CartridgeCompilation(Rom: rom, SourceHash: source.Hash, Target: Target, Variables: variables, Arrays: arrays);

        // Keeps a payload in the fixed window while it fits, so a small cartridge stays a two-bank image.
        Sm83BankedData.Placement Place(string name, byte[] bytes) {
            if (data.BytesUsed + bytes.Length <= FrameworkCartridge.BankSize) {
                return new Sm83BankedData.Placement(Bank: 1, Address: data.Add(name: name, bytes: bytes).Address);
            }

            return banked.Add(bytes: bytes);
        }
        static Action<Sm83Emitter>? Select(Sm83BankedData.Placement placement) =>
            placement.Bank == 1 ? null : emitter => Sm83BankedData.EmitSelect(emitter: emitter, bank: placement.Bank);

        // The window's own map and attributes are copied beside the background's, then it is positioned each frame.
        void InitializeWindow() {
            if (document.Window is null) {
                return;
            }

            Select(placement: windowMap)?.Invoke(obj: emitter);
            FrameworkKernel.EmitBlockCopy(emitter: emitter, sourceAddress: windowMap.Address, destinationAddress: Hw.VramWindowMap, byteCount: 0x400);
            emitter.LoadAImmediate(value: 0x01);
            emitter.StoreAToHighPage(port: Hw.PortVramBank);
            Select(placement: windowAttributes)?.Invoke(obj: emitter);
            FrameworkKernel.EmitBlockCopy(emitter: emitter, sourceAddress: windowAttributes.Address, destinationAddress: Hw.VramWindowMap, byteCount: 0x400);
            emitter.XorA();
            emitter.StoreAToHighPage(port: Hw.PortVramBank);
            Sm83BankedData.EmitSelect(emitter: emitter, bank: 1);
        }
        static byte BaseControl(CartridgeDocument document) =>
            (byte)(0x93 | (document.TallSprites ? 0x04 : 0x00) | (document.Window is null ? 0x00 : 0x60));

        void Initialize() {
            foreach (var variable in document.Variables) {
                emitter.LoadAImmediate(value: (byte)variable.Initial);
                emitter.StoreAToAddress(address: (ushort)variables[variable.Name]);
                if (variable.Width != 2) {
                    continue;
                }

                emitter.LoadAImmediate(value: (byte)(variable.Initial >> 8));
                emitter.StoreAToAddress(address: (ushort)(variables[variable.Name] + 1));
            }

            if (arrayInitial.Length != 0) {
                FrameworkKernel.EmitBlockCopy(emitter: emitter, sourceAddress: arrayData.Address, destinationAddress: ArrayBaseAddress, byteCount: (ushort)arrayInitial.Length);
            }
        }
        void Frame() {
            if (document.Scene is { } scene) {
                emitter.LoadAFromAddress(address: (ushort)variables[scene]);
                emitter.StoreAToAddress(address: SceneAddress);
            }

            foreach (var rule in document.Rules) {
                var end = emitter.NewLabel();
                Gate(predicate: rule.When, fail: end);
                Statements(statements: rule.Body, breakLabel: -1);
                emitter.MarkLabel(label: end);
            }
            Load(expression: document.ScrollX); emitter.StoreAToAddress(address: 0xFF43);
            Load(expression: document.ScrollY); emitter.StoreAToAddress(address: 0xFF42);
            EmitRasterUpdate();
            if (document.Window is { } panel) {
                var hidden = emitter.NewLabel();
                var placed = emitter.NewLabel();
                Load(expression: panel.Visible);
                emitter.ArithmeticImmediate(op: AluOp.Compare, value: 0);
                emitter.JumpAbsolute(condition: Condition.Zero, label: hidden);
                Load(expression: panel.Y);
                emitter.StoreAToHighPage(port: Hw.PortWindowY);
                // The horizontal register reads seven past the panel's left edge.
                Load(expression: panel.X);
                emitter.ArithmeticImmediate(op: AluOp.Add, value: 7);
                emitter.StoreAToHighPage(port: Hw.PortWindowX);
                emitter.JumpAbsolute(label: placed);
                // Parking it below the last scanline is how the hardware hides it.
                emitter.MarkLabel(label: hidden);
                emitter.LoadAImmediate(value: 144);
                emitter.StoreAToHighPage(port: Hw.PortWindowY);
                emitter.MarkLabel(label: placed);
            }
            for (var i = 0; i < document.Sprites.Length; ++i) {
                var sprite = document.Sprites[i];
                var hide = emitter.NewLabel(); var done = emitter.NewLabel();
                Load(expression: sprite.Visible); emitter.ArithmeticImmediate(op: AluOp.Compare, value: 0);
                emitter.JumpAbsolute(condition: Condition.Zero, label: hide);
                Load(expression: sprite.Tile);
                if (document.Tiles.Length < 256) { emitter.ArithmeticImmediate(op: AluOp.Compare, value: (byte)document.Tiles.Length); emitter.JumpAbsolute(condition: Condition.NoCarry, label: hide); }
                emitter.StoreAToAddress(address: (ushort)(0xC102 + i * 4));
                EmitAttributes(sprite: sprite, index: i);
                Load(expression: sprite.X); emitter.ArithmeticImmediate(op: AluOp.Compare, value: 160); emitter.JumpAbsolute(condition: Condition.NoCarry, label: hide);
                emitter.ArithmeticImmediate(op: AluOp.Add, value: 8); emitter.StoreAToAddress(address: (ushort)(0xC101 + i * 4));
                Load(expression: sprite.Y); emitter.ArithmeticImmediate(op: AluOp.Compare, value: 144); emitter.JumpAbsolute(condition: Condition.NoCarry, label: hide);
                emitter.ArithmeticImmediate(op: AluOp.Add, value: 16); emitter.StoreAToAddress(address: (ushort)(0xC100 + i * 4));
                emitter.JumpAbsolute(label: done);
                emitter.MarkLabel(label: hide); emitter.XorA(); emitter.StoreAToAddress(address: (ushort)(0xC100 + i * 4));
                emitter.MarkLabel(label: done);
            }
        }

        // Jumps to fail when the gate misses; falls through when it holds. An absent gate always holds.
        void Gate(ActionPredicate? predicate, int fail) {
            switch (predicate) {
                case null:
                    return;
                case ActionPredicate.All all:
                    foreach (var inner in all.Predicates) { Gate(predicate: inner, fail: fail); }

                    return;
                case ActionPredicate.Any any: {
                        var holds = emitter.NewLabel();

                        foreach (var inner in any.Predicates) {
                            var next = emitter.NewLabel();

                            Gate(predicate: inner, fail: next);
                            emitter.JumpAbsolute(label: holds);
                            emitter.MarkLabel(label: next);
                        }

                        emitter.JumpAbsolute(label: fail);
                        emitter.MarkLabel(label: holds);

                        return;
                    }
                case ActionPredicate.Not not: {
                        // The inner gate jumps away when it MISSES, which is exactly when this one holds.
                        var holds = emitter.NewLabel();

                        Gate(predicate: not.Predicate, fail: holds);
                        emitter.JumpAbsolute(label: fail);
                        emitter.MarkLabel(label: holds);

                        return;
                    }
                default:
                    Compare(compare: (ActionPredicate.CompareValue)predicate, fail: fail);

                    return;
            }
        }

        void Compare(ActionPredicate.CompareValue compare, int fail) {
            if (WideOperand(expression: compare.Left) || WideOperand(expression: compare.Right)) {
                // Sixteen bits compare a byte at a time. > and <= swap their operands so every case reads the
                // borrow out of one subtraction chain rather than needing a signed test.
                var swap = compare.Comparison is (ActionStateComparison.Greater or ActionStateComparison.LessOrEqual);

                LoadWide(expression: (swap ? compare.Right : compare.Left), pair: Reg16.Hl, guard: true);
                emitter.Push(pair: StackPair.Hl);
                LoadWide(expression: (swap ? compare.Left : compare.Right), pair: Reg16.De, guard: true);
                emitter.Pop(pair: StackPair.Hl);
                if (compare.Comparison is (ActionStateComparison.Equal or ActionStateComparison.NotEqual)) {
                    var differs = emitter.NewLabel();

                    emitter.Load(destination: Reg8.A, source: Reg8.L);
                    emitter.Arithmetic(op: AluOp.Compare, source: Reg8.E);
                    emitter.JumpAbsolute(condition: Condition.NotZero, label: compare.Comparison == ActionStateComparison.Equal ? fail : differs);
                    emitter.Load(destination: Reg8.A, source: Reg8.H);
                    emitter.Arithmetic(op: AluOp.Compare, source: Reg8.D);
                    if (compare.Comparison is ActionStateComparison.Equal) {
                        emitter.JumpAbsolute(condition: Condition.NotZero, label: fail);
                        emitter.MarkLabel(label: differs);
                    } else {
                        emitter.JumpAbsolute(condition: Condition.NotZero, label: differs);
                        emitter.JumpAbsolute(label: fail);
                        emitter.MarkLabel(label: differs);
                    }

                    return;
                }

                emitter.Load(destination: Reg8.A, source: Reg8.L);
                emitter.Arithmetic(op: AluOp.Subtract, source: Reg8.E);
                emitter.Load(destination: Reg8.A, source: Reg8.H);
                emitter.Arithmetic(op: AluOp.SubtractWithCarry, source: Reg8.D);
                // Carry out of the chain is the borrow: set means the first operand is the smaller one.
                emitter.JumpAbsolute(
                    condition: ((compare.Comparison is (ActionStateComparison.Less or ActionStateComparison.Greater)) ? Condition.NoCarry : Condition.Carry),
                    label: fail);

                return;
            }

            // Popping the saved left operand into B leaves the right one in A, which is the operand order > and <=
            // need; every other comparison wants the left one in A, so it swaps them back through the accumulator.
            var reversed = compare.Comparison is (ActionStateComparison.Greater or ActionStateComparison.LessOrEqual);

            Load(expression: compare.Left, guard: true);
            emitter.Push(pair: StackPair.Af);
            Load(expression: compare.Right, guard: true);
            if (reversed) {
                emitter.Pop(pair: StackPair.Bc);
            } else {
                emitter.Load(destination: Reg8.B, source: Reg8.A);
                emitter.Pop(pair: StackPair.Af);
            }

            emitter.Arithmetic(op: AluOp.Compare, source: Reg8.B);
            emitter.JumpAbsolute(condition: compare.Comparison switch {
                ActionStateComparison.Equal => Condition.NotZero,
                ActionStateComparison.NotEqual => Condition.Zero,
                ActionStateComparison.Less or ActionStateComparison.Greater => Condition.NoCarry,
                _ => Condition.Carry,
            }, label: fail);
        }

        // breakLabel is the enclosing loop's exit, or -1 outside any loop; validation has already refused a stray break.
        void Statements(CartridgeStatement[] statements, int breakLabel) {
            foreach (var statement in statements) {
                switch (statement.Kind) {
                    case "set":
                        Act(action: statement);
                        break;
                    case "if": {
                            var otherwise = emitter.NewLabel();
                            Gate(predicate: statement.When!, fail: otherwise);
                            Statements(statements: statement.Then!, breakLabel: breakLabel);
                            if (statement.Else is { } alternative) {
                                var joined = emitter.NewLabel();
                                emitter.JumpAbsolute(label: joined);
                                emitter.MarkLabel(label: otherwise);
                                Statements(statements: alternative, breakLabel: breakLabel);
                                emitter.MarkLabel(label: joined);
                            } else {
                                emitter.MarkLabel(label: otherwise);
                            }

                            break;
                        }
                    case "repeat": {
                            // A break leaves the index at the iteration that broke; normal completion leaves it at count.
                            var address = (ushort)variables[statement.Index!];
                            var top = emitter.NewLabel();
                            var exit = emitter.NewLabel();
                            emitter.XorA();
                            emitter.StoreAToAddress(address: address);
                            emitter.MarkLabel(label: top);
                            emitter.LoadAFromAddress(address: address);
                            emitter.ArithmeticImmediate(op: AluOp.Compare, value: (byte)statement.Count!.Value);
                            emitter.JumpAbsolute(condition: Condition.NoCarry, label: exit);
                            Statements(statements: statement.Body!, breakLabel: exit);
                            emitter.LoadAFromAddress(address: address);
                            emitter.ArithmeticImmediate(op: AluOp.Add, value: 1);
                            emitter.StoreAToAddress(address: address);
                            emitter.JumpAbsolute(label: top);
                            emitter.MarkLabel(label: exit);
                            break;
                        }
                    case "play": {
                            var sound = sounds[statement.Sound!];
                            foreach (var part in sound.Parts) {
                                // The wave voice plays through a pattern, which must be in place before the voice starts.
                                if (part.Pattern is { } pattern) {
                                    ApuSoundDriver.EmitWavePatternLoad(emitter: emitter, pattern: pattern);
                                }

                                if (sound.IsMusic) {
                                    ApuSoundDriver.EmitMusicStart(emitter: emitter, stream: part.Table, voice: part.Voice);
                                } else {
                                    ApuSoundDriver.EmitEffectStart(emitter: emitter, stream: part.Table, voice: part.Voice);
                                }
                            }

                            break;
                        }
                    case "stop":
                        foreach (var voice in musicVoices) {
                            ApuSoundDriver.EmitVoiceStop(emitter: emitter, voice: voice);
                        }

                        break;
                    case "fade": {
                            // Pick the baked bank for this step, then republish both palette banks from it.
                            var backgroundBytes = document.Palettes.Background.Sum(selector: static palette => palette.Length) * 2;
                            var objectBytes = document.Palettes.Object.Sum(selector: static palette => palette.Length) * 2;
                            var table = statement.Toward == "white" ? fadesToWhite : fadesToBlack;
                            var clamped = emitter.NewLabel();
                            var placed = emitter.NewLabel();
                            var stride = emitter.NewLabel();
                            Load(expression: statement.Amount!);
                            emitter.ArithmeticImmediate(op: AluOp.Compare, value: CartridgeLimits.FadeSteps + 1);
                            emitter.JumpRelative(condition: Condition.Carry, label: clamped);
                            emitter.LoadAImmediate(value: CartridgeLimits.FadeSteps);
                            emitter.MarkLabel(label: clamped);
                            emitter.Load(destination: Reg8.B, source: Reg8.A);
                            emitter.LoadImmediate(pair: Reg16.Hl, value: table.Address);
                            emitter.LoadImmediate(pair: Reg16.De, value: (ushort)backgroundBytes);
                            emitter.MarkLabel(label: stride);
                            emitter.Load(destination: Reg8.A, source: Reg8.B);
                            emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
                            emitter.JumpRelative(condition: Condition.Zero, label: placed);
                            emitter.AddToHl(pair: Reg16.De);
                            emitter.Decrement(register: Reg8.B);
                            emitter.JumpRelative(label: stride);
                            emitter.MarkLabel(label: placed);
                            FrameworkKernel.EmitPaletteRepublish(emitter: emitter, indexPort: Hw.PortBgPaletteIndex, dataPort: Hw.PortBgPaletteData, byteCount: backgroundBytes);
                            // The object table follows the background one, stepped by its own stride.
                            emitter.LoadImmediate(pair: Reg16.Hl, value: (ushort)(table.Address + ((CartridgeLimits.FadeSteps + 1) * backgroundBytes)));
                            emitter.LoadImmediate(pair: Reg16.De, value: (ushort)objectBytes);
                            Load(expression: statement.Amount!);
                            emitter.ArithmeticImmediate(op: AluOp.Compare, value: CartridgeLimits.FadeSteps + 1);
                            var clampedObject = emitter.NewLabel();
                            var placedObject = emitter.NewLabel();
                            var strideObject = emitter.NewLabel();
                            emitter.JumpRelative(condition: Condition.Carry, label: clampedObject);
                            emitter.LoadAImmediate(value: CartridgeLimits.FadeSteps);
                            emitter.MarkLabel(label: clampedObject);
                            emitter.Load(destination: Reg8.B, source: Reg8.A);
                            emitter.MarkLabel(label: strideObject);
                            emitter.Load(destination: Reg8.A, source: Reg8.B);
                            emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
                            emitter.JumpRelative(condition: Condition.Zero, label: placedObject);
                            emitter.AddToHl(pair: Reg16.De);
                            emitter.Decrement(register: Reg8.B);
                            emitter.JumpRelative(label: strideObject);
                            emitter.MarkLabel(label: placedObject);
                            FrameworkKernel.EmitPaletteRepublish(emitter: emitter, indexPort: Hw.PortObjPaletteIndex, dataPort: Hw.PortObjPaletteData, byteCount: objectBytes);
                            break;
                        }
                    case "clock": {
                            // Latching copies the running clock into the readable registers; without it a read can
                            // catch the counter mid-carry and report a moment that never happened.
                            emitter.LoadAImmediate(value: 0x00);
                            emitter.StoreAToAddress(address: 0x6000);
                            emitter.LoadAImmediate(value: 0x01);
                            emitter.StoreAToAddress(address: 0x6000);
                            emitter.LoadAImmediate(value: 0x0A);
                            emitter.StoreAToAddress(address: 0x0000);
                            foreach (var (name, register) in new[] {
                            (document.Clock!.Seconds, 0x08), (document.Clock.Minutes, 0x09),
                            (document.Clock.Hours, 0x0A), (document.Clock.Days, 0x0B),
                        }) {
                                if (name is null) {
                                    continue;
                                }

                                emitter.LoadAImmediate(value: (byte)register);
                                emitter.StoreAToAddress(address: 0x4000);
                                emitter.LoadAFromAddress(address: 0xA000);
                                emitter.StoreAToAddress(address: (ushort)variables[name]);
                            }

                            emitter.XorA();
                            emitter.StoreAToAddress(address: 0x0000);
                            break;
                        }
                    case "save": {
                            // Gather the live state into the mirror, then let the module write it through with a checksum.
                            var offset = 0;
                            foreach (var (address, length) in persisted) {
                                CopyRun(source: (ushort)address, destination: (ushort)(FrameworkMemoryMap.SaveMirror + offset), length: length);
                                offset += length;
                            }

                            save!.EmitStore();
                            break;
                        }
                    case "load": {
                            // The module fills the mirror from a valid block, or from the authored defaults when it is not.
                            save!.EmitLoad();
                            var offset = 0;
                            foreach (var (address, length) in persisted) {
                                CopyRun(source: (ushort)(FrameworkMemoryMap.SaveMirror + offset), destination: (ushort)address, length: length);
                                offset += length;
                            }

                            break;
                        }
                    case "map": {
                            // The queue push takes DE = cell address, A = tile. Row and column are runtime values, so the
                            // address is built as 0x9800 + row * 32 + column with the row's low three bits carried into H.
                            Load(expression: statement.Row!);
                            emitter.ArithmeticImmediate(op: AluOp.And, value: 31);
                            emitter.Load(destination: Reg8.L, source: Reg8.A);
                            emitter.LoadImmediate(destination: Reg8.H, value: 0);
                            emitter.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.L);
                            emitter.Shift(op: ShiftOp.RotateLeft, register: Reg8.H);
                            emitter.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.L);
                            emitter.Shift(op: ShiftOp.RotateLeft, register: Reg8.H);
                            emitter.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.L);
                            emitter.Shift(op: ShiftOp.RotateLeft, register: Reg8.H);
                            emitter.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.L);
                            emitter.Shift(op: ShiftOp.RotateLeft, register: Reg8.H);
                            emitter.Shift(op: ShiftOp.ShiftLeftArithmetic, register: Reg8.L);
                            emitter.Shift(op: ShiftOp.RotateLeft, register: Reg8.H);
                            Load(expression: statement.Column!);
                            emitter.ArithmeticImmediate(op: AluOp.And, value: 31);
                            emitter.Arithmetic(op: AluOp.Add, source: Reg8.L);
                            emitter.Load(destination: Reg8.E, source: Reg8.A);
                            emitter.Load(destination: Reg8.A, source: Reg8.H);
                            emitter.ArithmeticImmediate(op: AluOp.Add, value: Hw.VramBackgroundMap >> 8);
                            emitter.Load(destination: Reg8.D, source: Reg8.A);
                            emitter.Push(pair: StackPair.De);
                            if (statement.Palette is { } shade) {
                                Load(expression: shade);
                                emitter.ArithmeticImmediate(op: AluOp.And, value: 0x07);
                            } else {
                                emitter.XorA();
                            }

                            emitter.Load(destination: Reg8.C, source: Reg8.A);
                            Load(expression: statement.Tile!);
                            emitter.Pop(pair: StackPair.De);
                            background.EmitQueuePush();
                            break;
                        }
                    case "blit": {
                            var screen = screens[statement.Screen!];
                            background.EmitLcdOff();
                            background.EmitQueueClear();
                            background.EmitPaintRect(sourceAddress: screen.Table.Address, row: CartridgeExpressions.Whole(expression: statement.Row)!.Value, column: CartridgeExpressions.Whole(expression: statement.Column)!.Value, width: screen.Width, height: screen.Height);
                            if (screen.Shades is { } shades) {
                                // The colour of a cell lives at the same address in the other video-memory bank.
                                emitter.LoadAImmediate(value: 0x01);
                                emitter.StoreAToHighPage(port: Hw.PortVramBank);
                                background.EmitPaintRect(sourceAddress: shades.Address, row: CartridgeExpressions.Whole(expression: statement.Row)!.Value, column: CartridgeExpressions.Whole(expression: statement.Column)!.Value, width: screen.Width, height: screen.Height);
                                emitter.XorA();
                                emitter.StoreAToHighPage(port: Hw.PortVramBank);
                            }

                            background.EmitLcdOn(lcdc: BaseControl(document: document));
                            break;
                        }
                    default:
                        emitter.JumpAbsolute(label: breakLabel);
                        break;
                }
            }
        }

        // A plain assignment spills the operand and recovers it once the destination address is known; every other operation keeps
        // the operand in B and the destination address on the stack, so an indexed destination is computed exactly once.
        void Act(CartridgeStatement action) {
            // A wide slot is two bytes, so its assignment and its add/subtract run through the pair registers instead of
            // the accumulator. Validation has already refused every other operation against one.
            if (WideTarget(target: action.Target) || WideOperand(expression: action.Value)) {
                LoadWide(expression: action.Value!, pair: Reg16.De);
                if (action.Operation is null) {
                    StoreWide(target: action.Target!, pair: Reg16.De);

                    return;
                }

                if (action.Operation is ExpressionOp.Add) {
                    LoadWide(expression: CartridgeExpressions.Of(state: action.Target!.State), pair: Reg16.Hl);
                    emitter.AddToHl(pair: Reg16.De);
                    StoreWide(target: action.Target!, pair: Reg16.Hl);

                    return;
                }

                // Subtract has no sixteen-bit form on this processor: the low byte borrows into the high one.
                var wideAddress = (ushort)variables[action.Target!.State];

                emitter.LoadAFromAddress(address: wideAddress);
                emitter.Arithmetic(op: AluOp.Subtract, source: Reg8.E);
                emitter.StoreAToAddress(address: wideAddress);
                emitter.LoadAFromAddress(address: (ushort)(wideAddress + 1));
                emitter.Arithmetic(op: AluOp.SubtractWithCarry, source: Reg8.D);
                emitter.StoreAToAddress(address: (ushort)(wideAddress + 1));

                return;
            }

            Address(target: action.Target!);
            emitter.Push(pair: StackPair.Hl);
            Load(expression: action.Value!);
            if (action.Operation is null) {
                emitter.Pop(pair: StackPair.Hl);
                emitter.Load(destination: Reg8.Memory, source: Reg8.A);

                return;
            }

            emitter.Load(destination: Reg8.B, source: Reg8.A);
            emitter.Pop(pair: StackPair.Hl);
            emitter.Load(destination: Reg8.A, source: Reg8.Memory);
            switch (action.Operation) {
                case ExpressionOp.Multiply: arithmetic.EmitMultiply(); break;
                case ExpressionOp.Divide: arithmetic.EmitDivide(); break;
                case ExpressionOp.Modulo: arithmetic.EmitDivide(); emitter.Load(destination: Reg8.A, source: Reg8.C); break;
                case ExpressionOp.ShiftLeft: arithmetic.EmitShiftLeft(); break;
                case ExpressionOp.ShiftRight: arithmetic.EmitShiftRight(); break;
                default:
                    emitter.Arithmetic(op: action.Operation switch {
                        ExpressionOp.Add => AluOp.Add,
                        ExpressionOp.Subtract => AluOp.Subtract,
                        ExpressionOp.BitAnd => AluOp.And,
                        ExpressionOp.BitOr => AluOp.Or,
                        _ => AluOp.Xor,
                    }, source: Reg8.B);
                    break;
            }
            emitter.Load(destination: Reg8.Memory, source: Reg8.A);
        }

        void CopyRun(ushort source, ushort destination, int length) {
            if (length == 1) {
                emitter.LoadAFromAddress(address: source);
                emitter.StoreAToAddress(address: destination);
                return;
            }

            FrameworkKernel.EmitBlockCopy(emitter: emitter, sourceAddress: source, destinationAddress: destination, byteCount: (ushort)length);
        }

        // The object attribute byte: palette in bits 0..2, horizontal mirror at 5, vertical at 6, behind-background at 7.
        void EmitAttributes(CartridgeSprite sprite, int index) {
            if (sprite.Palette is null && sprite.FlipX is null && sprite.FlipY is null && sprite.BehindBackground is null) {
                return;
            }

            emitter.XorA();
            emitter.StoreAToAddress(address: ScratchAddress);
            if (sprite.Palette is { } slot) {
                Load(expression: slot);
                emitter.ArithmeticImmediate(op: AluOp.And, value: 0x07);
                emitter.StoreAToAddress(address: ScratchAddress);
            }

            foreach (var (flag, bit) in new[] { (sprite.FlipX, 5), (sprite.FlipY, 6), (sprite.BehindBackground, 7) }) {
                if (flag is null) {
                    continue;
                }

                var skip = emitter.NewLabel();
                Load(expression: flag);
                emitter.ArithmeticImmediate(op: AluOp.Compare, value: 0);
                emitter.JumpRelative(condition: Condition.Zero, label: skip);
                emitter.LoadAFromAddress(address: ScratchAddress);
                emitter.ArithmeticImmediate(op: AluOp.Or, value: (byte)(1 << bit));
                emitter.StoreAToAddress(address: ScratchAddress);
                emitter.MarkLabel(label: skip);
            }

            emitter.LoadAFromAddress(address: ScratchAddress);
            emitter.StoreAToAddress(address: (ushort)(0xC103 + index * 4));
        }

        // The two banks sit back to back, which is the order the palette ports expect them republished in.
        static byte[] FadeBanks(CartridgeDocument document, bool toWhite) => [
            .. CartridgeGraphics.FadeTable(bank: document.Palettes.Background, steps: CartridgeLimits.FadeSteps, toWhite: toWhite),
            .. CartridgeGraphics.FadeTable(bank: document.Palettes.Object, steps: CartridgeLimits.FadeSteps, toWhite: toWhite),
        ];


        // Each frame republishes the rows, so a document can drive them from ordinary state.
        void EmitRasterUpdate() {
            for (var index = 0; index < document.Raster.Length; ++index) {
                var row = document.Raster[index];
                // A line early: the handler runs during the line above and writes in its horizontal blank.
                emitter.LoadAImmediate(value: (byte)(row.Line - 1));
                emitter.StoreAToAddress(address: (ushort)(RasterRowAddress + (index * 3)));
                Load(expression: row.ScrollX);
                emitter.StoreAToAddress(address: (ushort)(RasterRowAddress + (index * 3) + 1));
                Load(expression: row.ScrollY);
                emitter.StoreAToAddress(address: (ushort)(RasterRowAddress + (index * 3) + 2));
            }
        }

        // Rearming points the compare register at the first row again, ready for the next picture.
        void EmitRasterRearm(Sm83Emitter target) {
            target.XorA();
            target.StoreAToAddress(address: RasterCursorAddress);
            target.LoadAFromAddress(address: RasterRowAddress);
            target.StoreAToHighPage(port: Hw.PortLineCompare);
        }

        // The status handler: apply the row the cursor names, then aim the compare register at the next one.
        void EmitRasterHandler() {
            var done = emitter.NewLabel();
            var parked = emitter.NewLabel();
            emitter.MarkLabel(label: rasterHandler);
            emitter.Push(pair: StackPair.Af);
            emitter.Push(pair: StackPair.Hl);
            emitter.Push(pair: StackPair.De);

            // HL = row base + cursor * 3, reached by adding rather than multiplying.
            emitter.LoadAFromAddress(address: RasterCursorAddress);
            emitter.Load(destination: Reg8.B, source: Reg8.A);
            emitter.LoadImmediate(pair: Reg16.Hl, value: RasterRowAddress);
            emitter.LoadImmediate(pair: Reg16.De, value: 3);
            var stride = emitter.NewLabel();
            var placed = emitter.NewLabel();
            emitter.MarkLabel(label: stride);
            emitter.Load(destination: Reg8.A, source: Reg8.B);
            emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
            emitter.JumpRelative(condition: Condition.Zero, label: placed);
            emitter.AddToHl(pair: Reg16.De);
            emitter.Decrement(register: Reg8.B);
            emitter.JumpRelative(label: stride);
            emitter.MarkLabel(label: placed);

            // The picture latches the horizontal scroll when it starts drawing a line, so wait out the drawing and
            // write in the blank that follows it.
            var blank = emitter.NewLabel();
            emitter.MarkLabel(label: blank);
            emitter.LoadAFromHighPage(port: Hw.PortLcdStatus);
            emitter.ArithmeticImmediate(op: AluOp.And, value: 0x03);
            emitter.JumpRelative(condition: Condition.NotZero, label: blank);

            emitter.Increment(pair: Reg16.Hl);
            emitter.LoadAFromHlIncrement();
            emitter.StoreAToHighPage(port: 0x43);
            emitter.Load(destination: Reg8.A, source: Reg8.Memory);
            emitter.StoreAToHighPage(port: 0x42);

            emitter.LoadAFromAddress(address: RasterCursorAddress);
            emitter.ArithmeticImmediate(op: AluOp.Add, value: 1);
            emitter.StoreAToAddress(address: RasterCursorAddress);
            emitter.ArithmeticImmediate(op: AluOp.Compare, value: (byte)document.Raster.Length);
            emitter.JumpRelative(condition: Condition.NoCarry, label: parked);

            // Aim at the next row's scanline.
            emitter.Load(destination: Reg8.B, source: Reg8.A);
            emitter.LoadImmediate(pair: Reg16.Hl, value: RasterRowAddress);
            emitter.LoadImmediate(pair: Reg16.De, value: 3);
            var next = emitter.NewLabel();
            var reached = emitter.NewLabel();
            emitter.MarkLabel(label: next);
            emitter.Load(destination: Reg8.A, source: Reg8.B);
            emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
            emitter.JumpRelative(condition: Condition.Zero, label: reached);
            emitter.AddToHl(pair: Reg16.De);
            emitter.Decrement(register: Reg8.B);
            emitter.JumpRelative(label: next);
            emitter.MarkLabel(label: reached);
            emitter.Load(destination: Reg8.A, source: Reg8.Memory);
            emitter.StoreAToHighPage(port: Hw.PortLineCompare);
            emitter.JumpRelative(label: done);

            // Past the last row nothing more should fire, so park the compare beyond the picture.
            emitter.MarkLabel(label: parked);
            emitter.LoadAImmediate(value: 200);
            emitter.StoreAToHighPage(port: Hw.PortLineCompare);

            emitter.MarkLabel(label: done);
            emitter.Pop(pair: StackPair.De);
            emitter.Pop(pair: StackPair.Hl);
            emitter.Pop(pair: StackPair.Af);
            emitter.ReturnFromInterrupt();
        }

        // A wide operand is a bare read of a two-byte slot: that is the only shape the pair registers handle, and
        // the only shape validation admits where a pair is read.
        bool WideOperand(ValueExpression? expression) => ((Bare(expression: expression) is { } name) && widths.TryGetValue(key: name, value: out var width) && (width == 2));

        bool WideTarget(CartridgeTarget? target) => ((target is { Key: null }) && widths.TryGetValue(key: target.State, value: out var width) && (width == 2));

        static string? Bare(ValueExpression? expression) =>
            ((expression?.Tokens is [ValueToken.State { Key: null } state]) ? state.Name : null);

        // Reads an operand as sixteen bits into the given pair. A narrow operand zero-extends, so a wide slot and a
        // byte compare and combine on the same terms.
        void LoadWide(ValueExpression expression, Reg16 pair, bool guard = false) {
            var low = ((pair == Reg16.Hl) ? Reg8.L : Reg8.E);
            var high = ((pair == Reg16.Hl) ? Reg8.H : Reg8.D);

            if (CartridgeExpressions.Whole(expression: expression) is { } constant) {
                emitter.LoadImmediate(pair: pair, value: (ushort)constant);

                return;
            }

            if (Bare(expression: expression) is { } name && variables.TryGetValue(name, out var variableAddress)) {
                var address = guard && name == document.Scene ? SceneAddress : (ushort)variableAddress;

                emitter.LoadAFromAddress(address: address);
                emitter.Load(destination: low, source: Reg8.A);
                if (widths[name] == 2) {
                    emitter.LoadAFromAddress(address: (ushort)(address + 1));
                    emitter.Load(destination: high, source: Reg8.A);

                    return;
                }
            } else {
                Load(expression: expression, guard: guard);
                emitter.Load(destination: low, source: Reg8.A);
            }

            emitter.XorA();
            emitter.Load(destination: high, source: Reg8.A);
        }

        void StoreWide(CartridgeTarget target, Reg16 pair) {
            var low = ((pair == Reg16.Hl) ? Reg8.L : Reg8.E);
            var high = ((pair == Reg16.Hl) ? Reg8.H : Reg8.D);
            var address = (ushort)variables[target.State];

            emitter.Load(destination: Reg8.A, source: low);
            emitter.StoreAToAddress(address: address);
            emitter.Load(destination: Reg8.A, source: high);
            emitter.StoreAToAddress(address: (ushort)(address + 1));
        }

        // Leaves the expression's value in the accumulator. Operands still in flight live on the machine stack, which
        // is what the validated depth bounds; B and C are scratch and carry nothing across a call. Inside a gate every
        // read of the declared scene answers from the frame's snapshot rather than live state, at any nesting.
        void Load(ValueExpression expression, bool guard = false) {
            var depth = 0;

            foreach (var token in expression.Tokens) {
                if (token is ValueToken.Constant or ValueToken.State) {
                    if (depth > 0) {
                        emitter.Push(pair: StackPair.Af);
                    }

                    if (token is ValueToken.Constant constant) {
                        emitter.LoadAImmediate(value: (byte)(int)constant.Value);
                    } else {
                        Read(state: (ValueToken.State)token, guard: guard);
                    }

                    ++depth;

                    continue;
                }

                var operation = ExpressionVocabulary.Operation(token: token)!.Value;
                var arity = ExpressionVocabulary.Arity(operation: operation);

                switch (arity) {
                    case 1: Unary(operation: operation); break;
                    case 2: Binary(operation: operation); break;
                    default: Ternary(operation: operation); break;
                }

                depth -= (arity - 1);
            }
        }

        void Read(ValueToken.State state, bool guard) {
            if (CartridgeExpressions.TryKey(name: state.Name, button: out var button, mode: out var mode)) {
                Button(button: button, mode: mode);

                return;
            }

            if (CartridgeExpressions.Index(key: state.Key) is { } index) {
                Element(array: state.Name, index: index, guard: guard);
                emitter.Load(destination: Reg8.A, source: Reg8.Memory);

                return;
            }

            emitter.LoadAFromAddress(address: ((guard && (state.Name == document.Scene)) ? SceneAddress : (ushort)variables[state.Name]));
        }

        // Leaves 1 in the accumulator while the button satisfies the mode, and 0 otherwise.
        void Button(string button, string mode) {
            var zero = emitter.NewLabel();
            var done = emitter.NewLabel();

            emitter.LoadAFromAddress(address: ((mode == "pressed") ? FrameworkMemoryMap.InputPressed : FrameworkMemoryMap.InputHeld));
            if (mode == "released") {
                emitter.ComplementA();
                emitter.Load(destination: Reg8.B, source: Reg8.A);
                emitter.LoadAFromAddress(address: HeldInputAddress);
                emitter.Arithmetic(op: AluOp.And, source: Reg8.B);
            }

            emitter.ArithmeticImmediate(op: AluOp.And, value: Key(key: button));
            emitter.JumpRelative(condition: Condition.Zero, label: zero);
            emitter.LoadAImmediate(value: 1);
            emitter.JumpRelative(label: done);
            emitter.MarkLabel(label: zero);
            emitter.XorA();
            emitter.MarkLabel(label: done);
        }

        void Unary(ExpressionOp operation) {
            switch (operation) {
                case ExpressionOp.BitNot:
                    emitter.ComplementA();

                    return;
                case ExpressionOp.Negate:
                    emitter.Load(destination: Reg8.B, source: Reg8.A);
                    emitter.XorA();
                    emitter.Arithmetic(op: AluOp.Subtract, source: Reg8.B);

                    return;
                default: {
                        // Every value is unsigned, so a sign is 0 or 1 and never -1.
                        var zero = emitter.NewLabel();
                        var done = emitter.NewLabel();

                        emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
                        emitter.JumpRelative(condition: Condition.Zero, label: zero);
                        emitter.LoadAImmediate(value: 1);
                        emitter.JumpRelative(label: done);
                        emitter.MarkLabel(label: zero);
                        emitter.XorA();
                        emitter.MarkLabel(label: done);

                        return;
                    }
            }
        }

        // The right operand is in the accumulator and the left one on the stack. Popping into B keeps the right one
        // where it is, which is the order > and <= read; everything else wants the left one in the accumulator.
        void Binary(ExpressionOp operation) {
            var reversed = operation is (ExpressionOp.Greater or ExpressionOp.LessOrEqual);

            if (reversed) {
                emitter.Pop(pair: StackPair.Bc);
            } else {
                emitter.Load(destination: Reg8.B, source: Reg8.A);
                emitter.Pop(pair: StackPair.Af);
            }

            switch (operation) {
                case ExpressionOp.Multiply: arithmetic.EmitMultiply(); return;
                case ExpressionOp.Divide: arithmetic.EmitDivide(); return;
                case ExpressionOp.Modulo: arithmetic.EmitDivide(); emitter.Load(destination: Reg8.A, source: Reg8.C); return;
                case ExpressionOp.ShiftLeft: arithmetic.EmitShiftLeft(); return;
                case ExpressionOp.ShiftRight: arithmetic.EmitShiftRight(); return;
                case ExpressionOp.Add: emitter.Arithmetic(op: AluOp.Add, source: Reg8.B); return;
                case ExpressionOp.Subtract: emitter.Arithmetic(op: AluOp.Subtract, source: Reg8.B); return;
                case ExpressionOp.BitAnd: emitter.Arithmetic(op: AluOp.And, source: Reg8.B); return;
                case ExpressionOp.BitOr: emitter.Arithmetic(op: AluOp.Or, source: Reg8.B); return;
                case ExpressionOp.BitXor: emitter.Arithmetic(op: AluOp.Xor, source: Reg8.B); return;
                case ExpressionOp.Minimum:
                case ExpressionOp.Maximum: {
                        var keep = emitter.NewLabel();

                        emitter.Arithmetic(op: AluOp.Compare, source: Reg8.B);
                        emitter.JumpRelative(condition: ((operation == ExpressionOp.Minimum) ? Condition.Carry : Condition.NoCarry), label: keep);
                        emitter.Load(destination: Reg8.A, source: Reg8.B);
                        emitter.MarkLabel(label: keep);

                        return;
                    }
                default: {
                        var set = emitter.NewLabel();
                        var done = emitter.NewLabel();

                        emitter.Arithmetic(op: AluOp.Compare, source: Reg8.B);
                        emitter.JumpRelative(condition: operation switch {
                            ExpressionOp.Equal => Condition.Zero,
                            ExpressionOp.NotEqual => Condition.NotZero,
                            ExpressionOp.Less or ExpressionOp.Greater => Condition.Carry,
                            _ => Condition.NoCarry,
                        }, label: set);
                        emitter.XorA();
                        emitter.JumpRelative(label: done);
                        emitter.MarkLabel(label: set);
                        emitter.LoadAImmediate(value: 1);
                        emitter.MarkLabel(label: done);

                        return;
                    }
            }
        }

        // Three operands: the last is in the accumulator and the first two are on the stack, deepest first.
        void Ternary(ExpressionOp operation) {
            emitter.Load(destination: Reg8.C, source: Reg8.A);
            emitter.Pop(pair: StackPair.Af);
            emitter.Load(destination: Reg8.B, source: Reg8.A);
            emitter.Pop(pair: StackPair.Af);
            if (operation == ExpressionOp.Select) {
                var done = emitter.NewLabel();

                emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
                emitter.Load(destination: Reg8.A, source: Reg8.B);
                emitter.JumpRelative(condition: Condition.NotZero, label: done);
                emitter.Load(destination: Reg8.A, source: Reg8.C);
                emitter.MarkLabel(label: done);

                return;
            }

            var above = emitter.NewLabel();
            var below = emitter.NewLabel();

            emitter.Arithmetic(op: AluOp.Compare, source: Reg8.B);
            emitter.JumpRelative(condition: Condition.NoCarry, label: above);
            emitter.Load(destination: Reg8.A, source: Reg8.B);
            emitter.MarkLabel(label: above);
            emitter.Arithmetic(op: AluOp.Compare, source: Reg8.C);
            emitter.JumpRelative(condition: Condition.Carry, label: below);
            emitter.JumpRelative(condition: Condition.Zero, label: below);
            emitter.Load(destination: Reg8.A, source: Reg8.C);
            emitter.MarkLabel(label: below);
        }

        void Address(CartridgeTarget target) {
            if (CartridgeExpressions.Index(key: target.Key) is { } index) {
                Element(array: target.State, index: index);

                return;
            }

            emitter.LoadImmediate(pair: Reg16.Hl, value: (ushort)variables[target.State]);
        }

        // Leaves HL at the addressed element, or at the zeroed discard sink when the index is past the declared length.
        void Element(string array, ValueExpression index, bool guard = false) {
            var length = lengths[key: array];
            var done = emitter.NewLabel();
            var inside = emitter.NewLabel();
            Load(expression: index, guard: guard);
            if (length < CartridgeLimits.ArrayLength) {
                emitter.ArithmeticImmediate(op: AluOp.Compare, value: (byte)length);
                emitter.JumpRelative(condition: Condition.Carry, label: inside);
                emitter.XorA();
                emitter.LoadImmediate(pair: Reg16.Hl, value: VoidAddress);
                emitter.Load(destination: Reg8.Memory, source: Reg8.A);
                emitter.JumpRelative(label: done);
            }

            emitter.MarkLabel(label: inside);
            emitter.Load(destination: Reg8.L, source: Reg8.A);
            emitter.LoadImmediate(destination: Reg8.H, value: 0);
            emitter.LoadImmediate(pair: Reg16.De, value: (ushort)arrays[array]);
            emitter.AddToHl(pair: Reg16.De);
            emitter.MarkLabel(label: done);
        }
    }

    private static byte Key(string key) => key switch {
        "right" => 1,
        "left" => 2,
        "up" => 4,
        "down" => 8,
        "a" => 16,
        "b" => 32,
        "select" => 64,
        "start" => 128,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(key)),
    };
}
