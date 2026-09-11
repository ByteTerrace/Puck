using Puck.Assets.Documents;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.HumbleGamingBrick.Forge;

/// <summary>Compiles cartridge documents into native CGB machine code and graphics.</summary>
/// <remarks>
/// Work-RAM layout above <c>FrameworkMemoryMap.GameRam</c>:
/// 0xC200..0xC23F variables, 0xC240 prior held input, 0xC241 operand spill, 0xC242 discard sink, 0xC243..0xDFFF arrays, spanning the fixed page and the switchable bank pinned at boot.
/// </remarks>
public sealed class HgbCartridgeCompiler : ICartridgeCompiler {
    private const ushort ArrayBaseAddress = 0xC243;
    private const ushort ArrayLimitAddress = 0xE000;
    private const ushort HeldInputAddress = 0xC240;
    private const ushort ScratchAddress = 0xC241;
    private const ushort VoidAddress = 0xC242;
    /// <summary>The mid-picture walk's cursor; the row triples follow it.</summary>
    private const ushort RasterCursorAddress = FrameworkMemoryMap.Scratch;
    /// <summary>The first row triple: scanline, horizontal scroll, vertical scroll.</summary>
    private const ushort RasterRowAddress = FrameworkMemoryMap.Scratch + 1;

    /// <inheritdoc />
    public string Target => "cgb";

    /// <inheritdoc />
    public CartridgeCompilation Compile(CartridgeDocument document) {
        var source = CartridgeDocuments.Canonicalize(document: document);
        document = source.Document;
        if (document.Target != Target) {
            throw new ArgumentException(message: "This compiler requires target cgb.", paramName: nameof(document));
        }

        var variables = document.Variables.Select(selector: (v, i) => (v.Name, Address: (uint)(FrameworkMemoryMap.GameRam + i))).ToDictionary(keySelector: v => v.Name, elementSelector: v => v.Address, comparer: StringComparer.Ordinal);
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
                persisted.Add(item: (variables[name], 1));
                defaults.Add(item: (byte)document.Variables.First(predicate: variable => variable.Name == name).Initial);
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
            elementSelector: screen => (Table: data.Add(name: $"screen-{screen.Name}", bytes: screen.Tiles.Select(selector: static tile => (byte)tile).ToArray()), screen.Width, Height: screen.Tiles.Length / screen.Width),
            comparer: StringComparer.Ordinal);
        // Arrays pack contiguously from ArrayBaseAddress, so one ROM table seeds every one of them.
        var arrayInitial = document.Arrays.SelectMany(selector: static array => array.Initial.Select(selector: static value => (byte)value)).ToArray();
        var arrayData = data.Add(name: "arrays", bytes: arrayInitial.Length == 0 ? [0] : arrayInitial);
        var sounds = document.Sounds.ToDictionary(
            keySelector: static sound => sound.Name,
            elementSelector: sound => sound.Music is { } music
                ? (Table: data.Add(name: $"music-{sound.Name}", bytes: AudioDocumentCompiler.CompileMusicLoop(document: AudioCanonicalizer.Normalize(document: music))), IsMusic: true, Voice: SoundVoice.Pulse, Pattern: (RomTable?)null)
                : (Table: data.Add(name: $"effect-{sound.Name}", bytes: AudioDocumentCompiler.CompileEffect(effect: sound.Effect!, frames: sound.Frames!.Value)),
                    IsMusic: false,
                    Voice: sound.Effect!.Voice switch { AudioEffectDocument.VoiceNoise => SoundVoice.Noise, AudioEffectDocument.VoiceWave => SoundVoice.Wave, _ => SoundVoice.Pulse },
                    Pattern: sound.Waveform is { } levels
                        ? data.Add(name: $"wave-{sound.Name}", bytes: Enumerable.Range(start: 0, count: 16).Select(selector: index => (byte)((levels[index * 2] << 4) | levels[(index * 2) + 1])).ToArray())
                        : null),
            comparer: StringComparer.Ordinal);
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
        var rom = FrameworkCartridge.Build(
            title: document.Title, routine: routine, data: data.ToArray(), banks: banked.Banks,
            clock: document.Clock is not null,
            statHandlerAddress: document.Raster.Length == 0 ? (ushort)0 : emitter.LabelAddress(label: rasterHandler, baseAddress: Hw.EntryAddress));
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
            }

            if (arrayInitial.Length != 0) {
                FrameworkKernel.EmitBlockCopy(emitter: emitter, sourceAddress: arrayData.Address, destinationAddress: ArrayBaseAddress, byteCount: (ushort)arrayInitial.Length);
            }
        }
        void Frame() {
            foreach (var rule in document.Rules) {
                var end = emitter.NewLabel();
                Conditions(conditions: rule.When, fail: end);
                Statements(statements: rule.Body, breakLabel: -1);
                emitter.MarkLabel(label: end);
            }
            Load(value: document.ScrollX); emitter.StoreAToAddress(address: 0xFF43);
            Load(value: document.ScrollY); emitter.StoreAToAddress(address: 0xFF42);
            EmitRasterUpdate();
            if (document.Window is { } panel) {
                var hidden = emitter.NewLabel();
                var placed = emitter.NewLabel();
                Load(value: panel.Visible);
                emitter.ArithmeticImmediate(op: AluOp.Compare, value: 0);
                emitter.JumpAbsolute(condition: Condition.Zero, label: hidden);
                Load(value: panel.Y);
                emitter.StoreAToHighPage(port: Hw.PortWindowY);
                // The horizontal register reads seven past the panel's left edge.
                Load(value: panel.X);
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
                Load(value: sprite.Visible); emitter.ArithmeticImmediate(op: AluOp.Compare, value: 0);
                emitter.JumpAbsolute(condition: Condition.Zero, label: hide);
                Load(value: sprite.Tile);
                if (document.Tiles.Length < 256) { emitter.ArithmeticImmediate(op: AluOp.Compare, value: (byte)document.Tiles.Length); emitter.JumpAbsolute(condition: Condition.NoCarry, label: hide); }
                emitter.StoreAToAddress(address: (ushort)(0xC102 + i * 4));
                EmitAttributes(sprite: sprite, index: i);
                Load(value: sprite.X); emitter.ArithmeticImmediate(op: AluOp.Compare, value: 160); emitter.JumpAbsolute(condition: Condition.NoCarry, label: hide);
                emitter.ArithmeticImmediate(op: AluOp.Add, value: 8); emitter.StoreAToAddress(address: (ushort)(0xC101 + i * 4));
                Load(value: sprite.Y); emitter.ArithmeticImmediate(op: AluOp.Compare, value: 144); emitter.JumpAbsolute(condition: Condition.NoCarry, label: hide);
                emitter.ArithmeticImmediate(op: AluOp.Add, value: 16); emitter.StoreAToAddress(address: (ushort)(0xC100 + i * 4));
                emitter.JumpAbsolute(label: done);
                emitter.MarkLabel(label: hide); emitter.XorA(); emitter.StoreAToAddress(address: (ushort)(0xC100 + i * 4));
                emitter.MarkLabel(label: done);
            }
        }

        // Jumps to fail when any condition misses; falls through when all hold.
        void Conditions(CartridgeCondition[] conditions, int fail) {
            foreach (var condition in conditions) {
                if (condition.Kind == "key") {
                    emitter.LoadAFromAddress(address: condition.Mode == "pressed" ? FrameworkMemoryMap.InputPressed : FrameworkMemoryMap.InputHeld);
                    if (condition.Mode == "released") {
                        emitter.ComplementA();
                        emitter.Load(destination: Reg8.B, source: Reg8.A);
                        emitter.LoadAFromAddress(address: HeldInputAddress);
                        emitter.Arithmetic(op: AluOp.And, source: Reg8.B);
                    }
                    emitter.ArithmeticImmediate(op: AluOp.And, value: Key(key: condition.Key!));
                    emitter.JumpAbsolute(condition: Condition.Zero, label: fail);
                } else {
                    // Reverse > and <= so all comparisons use carry/zero without signed arithmetic.
                    var reverse = condition.Comparison is "gt" or "le";
                    Load(value: reverse ? condition.Left! : condition.Right!);
                    emitter.Load(destination: Reg8.B, source: Reg8.A);
                    Load(value: reverse ? condition.Right! : condition.Left!);
                    emitter.Arithmetic(op: AluOp.Compare, source: Reg8.B);
                    emitter.JumpAbsolute(condition: condition.Comparison switch {
                        "eq" => Condition.NotZero,
                        "ne" => Condition.Zero,
                        "lt" or "gt" => Condition.NoCarry,
                        _ => Condition.Carry,
                    }, label: fail);
                }
            }
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
                            Conditions(conditions: statement.When!, fail: otherwise);
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
                            if (sound.IsMusic) {
                                ApuSoundDriver.EmitMusicStart(emitter: emitter, stream: sound.Table);
                            } else {
                                // The wave voice plays through a pattern, which must be in place before the voice starts.
                                if (sound.Pattern is { } pattern) {
                                    ApuSoundDriver.EmitWavePatternLoad(emitter: emitter, pattern: pattern);
                                }

                                ApuSoundDriver.EmitEffectStart(emitter: emitter, stream: sound.Table, voice: sound.Voice);
                            }

                            break;
                        }
                    case "stop":
                        ApuSoundDriver.EmitMusicStop(emitter: emitter);
                        break;
                    case "fade": {
                            // Pick the baked bank for this step, then republish both palette banks from it.
                            var backgroundBytes = document.Palettes.Background.Sum(selector: static palette => palette.Length) * 2;
                            var objectBytes = document.Palettes.Object.Sum(selector: static palette => palette.Length) * 2;
                            var table = statement.Toward == "white" ? fadesToWhite : fadesToBlack;
                            var clamped = emitter.NewLabel();
                            var placed = emitter.NewLabel();
                            var stride = emitter.NewLabel();
                            Load(value: statement.Amount!);
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
                            Load(value: statement.Amount!);
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
                            Load(value: statement.Row!);
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
                            Load(value: statement.Column!);
                            emitter.ArithmeticImmediate(op: AluOp.And, value: 31);
                            emitter.Arithmetic(op: AluOp.Add, source: Reg8.L);
                            emitter.Load(destination: Reg8.E, source: Reg8.A);
                            emitter.Load(destination: Reg8.A, source: Reg8.H);
                            emitter.ArithmeticImmediate(op: AluOp.Add, value: Hw.VramBackgroundMap >> 8);
                            emitter.Load(destination: Reg8.D, source: Reg8.A);
                            emitter.Push(pair: StackPair.De);
                            Load(value: statement.Tile!);
                            emitter.Pop(pair: StackPair.De);
                            background.EmitQueuePush();
                            break;
                        }
                    case "blit": {
                            var screen = screens[statement.Screen!];
                            background.EmitLcdOff();
                            background.EmitQueueClear();
                            background.EmitPaintRect(sourceAddress: screen.Table.Address, row: statement.Row!.Constant!.Value, column: statement.Column!.Constant!.Value, width: screen.Width, height: screen.Height);
                            background.EmitLcdOn(lcdc: BaseControl(document: document));
                            break;
                        }
                    default:
                        emitter.JumpAbsolute(label: breakLabel);
                        break;
                }
            }
        }

        // A "set" spills the operand and recovers it once the destination address is known; every other operation keeps
        // the operand in B and the destination address on the stack, so an indexed destination is computed exactly once.
        void Act(CartridgeStatement action) {
            if (action.Operation == "set") {
                Load(value: action.Value!);
                emitter.StoreAToAddress(address: ScratchAddress);
                Address(target: action.Target!);
                emitter.LoadAFromAddress(address: ScratchAddress);
                emitter.Load(destination: Reg8.Memory, source: Reg8.A);
                return;
            }

            Load(value: action.Value!);
            emitter.Load(destination: Reg8.B, source: Reg8.A);
            Address(target: action.Target!);
            emitter.Push(pair: StackPair.Hl);
            emitter.Load(destination: Reg8.A, source: Reg8.Memory);
            switch (action.Operation) {
                case "mul": arithmetic.EmitMultiply(); break;
                case "div": arithmetic.EmitDivide(); break;
                case "mod": arithmetic.EmitDivide(); emitter.Load(destination: Reg8.A, source: Reg8.C); break;
                case "shl": arithmetic.EmitShiftLeft(); break;
                case "shr": arithmetic.EmitShiftRight(); break;
                default:
                    emitter.Arithmetic(op: action.Operation switch {
                        "add" => AluOp.Add,
                        "subtract" => AluOp.Subtract,
                        "and" => AluOp.And,
                        "or" => AluOp.Or,
                        _ => AluOp.Xor,
                    }, source: Reg8.B);
                    break;
            }
            emitter.Pop(pair: StackPair.Hl);
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
                Load(value: slot);
                emitter.ArithmeticImmediate(op: AluOp.And, value: 0x07);
                emitter.StoreAToAddress(address: ScratchAddress);
            }

            foreach (var (flag, bit) in new[] { (sprite.FlipX, 5), (sprite.FlipY, 6), (sprite.BehindBackground, 7) }) {
                if (flag is null) {
                    continue;
                }

                var skip = emitter.NewLabel();
                Load(value: flag);
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
                Load(value: row.ScrollX);
                emitter.StoreAToAddress(address: (ushort)(RasterRowAddress + (index * 3) + 1));
                Load(value: row.ScrollY);
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

        void Load(CartridgeValue value) {
            if (value.Constant is { } constant) {
                emitter.LoadAImmediate(value: (byte)constant);
            } else if (value.Variable is { } name) {
                emitter.LoadAFromAddress(address: (ushort)variables[name]);
            } else {
                Element(array: value.Array!, index: value.Index!);
                emitter.Load(destination: Reg8.A, source: Reg8.Memory);
            }
        }

        void Address(CartridgeTarget target) {
            if (target.Variable is { } name) {
                emitter.LoadImmediate(pair: Reg16.Hl, value: (ushort)variables[name]);
            } else {
                Element(array: target.Array!, index: target.Index!);
            }
        }

        // Leaves HL at the addressed element, or at the zeroed discard sink when the index is past the declared length.
        void Element(string array, CartridgeValue index) {
            var length = lengths[key: array];
            var done = emitter.NewLabel();
            var inside = emitter.NewLabel();
            Load(value: index);
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
