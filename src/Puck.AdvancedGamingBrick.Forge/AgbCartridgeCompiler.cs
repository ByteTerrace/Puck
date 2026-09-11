using Puck.Assets.Documents;
using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>Compiles cartridge documents into BIOS-independent Thumb code and mode-0 graphics.</summary>
/// <remarks>
/// EWRAM layout above <c>AgbForgeMemoryMap.GameRam</c>: 0x02000040..0x0200007F variables, 0x02000080 discard sink,
/// 0x02000084 map-write queue count, 0x02000088 the queue's 24 entries of (row, column, tile, unused), 0x020000E8 the
/// sound sequencer's state, 0x02000104 the save mirror, 0x02000160 the digital sound engine, 0x020003F0 the clock's reply, 0x02000400 the surface's fill word, 0x02000500 the per-scanline
/// scroll table, and arrays from 0x02000900. The queue drains right
/// after the frame sync, so a map write is on screen the following frame exactly as the Color machine's
/// vertical-blank drain makes it.
/// </remarks>
public sealed class AgbCartridgeCompiler : ICartridgeCompiler {
    private const uint ArrayBaseAddress = 0x02000900u;
    private const uint DirectSoundStateAddress = 0x02000160u;
    private const uint QueueBaseAddress = 0x02000088u;
    private const uint QueueCountAddress = 0x02000084u;
    private const uint BitmapFillAddress = 0x02000400u;
    private const uint ClockReplyAddress = 0x020003F0u;
    private const uint RasterTableAddress = 0x02000500u;
    private const uint SaveMirrorAddress = 0x02000104u;
    private const uint SoundStateAddress = 0x020000E8u;
    private const uint VoidAddress = 0x02000080u;

    /// <inheritdoc />
    public string EngineId => "advanced-gaming-brick";

    /// <inheritdoc />
    public string Target => "agb";

    /// <inheritdoc />
    public CartridgeCompilation Compile(CartridgeDocument document) {
        var source = CartridgeDocuments.Canonicalize(document: document);
        document = source.Document;
        if (document.Target != Target) {
            throw new ArgumentException(message: "This compiler requires target agb.", paramName: nameof(document));
        }

        var variables = document.Variables.Select(selector: (v, i) => (v.Name, Address: (uint)(AgbForgeMemoryMap.GameRam + i))).ToDictionary(keySelector: v => v.Name, elementSelector: v => v.Address, comparer: StringComparer.Ordinal);
        var arrays = new Dictionary<string, uint>(comparer: StringComparer.Ordinal);
        var lengths = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var next = ArrayBaseAddress;
        foreach (var array in document.Arrays) {
            arrays[key: array.Name] = next;
            lengths[key: array.Name] = array.Initial.Length;
            next += (uint)array.Initial.Length;
        }

        var emitter = new ThumbEmitter();
        var arithmetic = new ThumbCartridgeArithmetic(emitter: emitter);
        var kernel = new AgbForgeKernel(emitter: emitter);
        var data = new List<byte>();
        var sounds = document.Sounds.ToDictionary(
            keySelector: static sound => sound.Name,
            elementSelector: sound => sound.Sample is { } pcm
                ? (Address: Add(bytes: pcm.Select(selector: static value => (byte)value).ToArray()), IsMusic: false, Voice: 0, SampleLength: pcm.Length, Pattern: 0u)
                : sound.Music is { } music
                    ? (Address: Add(bytes: AudioDocumentCompiler.CompileMusicLoop(document: AudioCanonicalizer.Normalize(document: music))), IsMusic: true, Voice: 0, SampleLength: 0, Pattern: 0u)
                    : (Address: Add(bytes: AudioDocumentCompiler.CompileEffect(effect: sound.Effect!, frames: sound.Frames!.Value)),
                        IsMusic: false,
                        Voice: sound.Effect!.Voice switch { AudioEffectDocument.VoiceNoise => 1, AudioEffectDocument.VoiceWave => 2, _ => 0 },
                        SampleLength: 0,
                        Pattern: sound.Waveform is { } levels
                            ? Add(bytes: Enumerable.Range(start: 0, count: 16).Select(selector: index => (byte)((levels[index * 2] << 4) | levels[(index * 2) + 1])).ToArray())
                            : 0u),
            comparer: StringComparer.Ordinal);
        var audio = document.Sounds.Length == 0 ? null : new AgbSoundDriver(emitter: emitter, stateAddress: SoundStateAddress);
        var digital = document.Sounds.Any(predicate: static sound => sound.Sample is not null) ? new AgbDirectSound(emitter: emitter, stateAddress: DirectSoundStateAddress) : null;
        AgbSaveModule? saver = null;
        var persisted = new List<(uint Address, int Length)>();
        if (document.Save is { } declared) {
            var defaults = new List<byte>();
            foreach (var name in declared.Variables) {
                persisted.Add(item: (variables[name], 1));
                defaults.Add(item: (byte)document.Variables.First(predicate: variable => variable.Name == name).Initial);
            }

            foreach (var name in declared.Arrays) {
                var array = document.Arrays.First(predicate: candidate => candidate.Name == name);
                persisted.Add(item: (arrays[name], array.Initial.Length));
                defaults.AddRange(collection: array.Initial.Select(selector: static value => (byte)value));
            }

            // The host recognizes the backup kind by scanning the image for this identifier.
            Add(bytes: System.Text.Encoding.ASCII.GetBytes(s: AgbSaveModule.SignatureText));
            saver = new AgbSaveModule(
                emitter: emitter, mirrorAddress: SaveMirrorAddress, defaultsAddress: Add(bytes: [.. defaults]),
                payloadByteCount: defaults.Count, version: (byte)declared.Version);
        }

        kernel.EmitBootPrologue();
        // Before anything else: the routine runs from the cartridge, so the prefetch and wait states govern how fast
        // every instruction after this one is fetched.
        StoreHalf(address: AgbForgeCartridge.WaitControlAddress, value: AgbForgeCartridge.WaitControlValue);
        StoreHalf(address: 0x04000000, value: 0x80); // Forced blank while copying video memory.
        var backgroundPalettes = CartridgeGraphics.PaletteBank(bank: document.Palettes.Background);
        var objectPalettes = CartridgeGraphics.PaletteBank(bank: document.Palettes.Object);
        Copy(sourceAddress: Add(bytes: backgroundPalettes), destination: 0x05000000, count: backgroundPalettes.Length);
        Copy(sourceAddress: Add(bytes: objectPalettes), destination: 0x05000200, count: objectPalettes.Length);
        var tileBytes = CartridgeGraphics.Tiles(document: document);
        var tiles = Add(bytes: tileBytes);
        // A per-pixel surface occupies the same memory the background's tiles would, so only the object bank is filled.
        if (document.Bitmap is null) {
            Copy(sourceAddress: tiles, destination: 0x06000000, count: tileBytes.Length);
        }

        Copy(sourceAddress: tiles, destination: 0x06010000, count: tileBytes.Length);
        // A screen entry carries its tile in the low ten bits and its palette in the top four.
        var cells = document.MapPalettes is { } chosen
            ? document.Map.Select(selector: (tile, index) => tile | ((chosen[index] & 0x0F) << 12)).ToArray()
            : document.Map;
        if (document.Bitmap is null) {
            Copy(sourceAddress: Add(bytes: CartridgeGraphics.Halfwords(values: cells)), destination: 0x0600F800, count: 2048);
        }

        var spriteTurns = document.Sprites.Any(predicate: static sprite => sprite.Turn is not null)
            ? new AgbSpriteTurn(emitter: emitter, tableAddress: Add(bytes: AgbAffineBackground.BuildTurnTable()))
            : null;
        AgbRealTimeClock? clock = null;
        if (document.Clock is not null) {
            // The host decides the cartridge carries a clock by scanning the image for this identifier.
            Add(bytes: System.Text.Encoding.ASCII.GetBytes(s: AgbRealTimeClock.SignatureText));
            clock = new AgbRealTimeClock(emitter: emitter, replyAddress: ClockReplyAddress);
        }

        var raster = document.Raster.Length == 0 ? null : new AgbRasterScroll(emitter: emitter, tableAddress: RasterTableAddress);
        AgbAffineBackground? affine = null;
        if (document.Affine is { } turning) {
            // Screen block 28 for the turning layer's map, clear of the scrolling layers' blocks.
            Copy(sourceAddress: Add(bytes: turning.Map.Select(selector: static tile => (byte)tile).ToArray()), destination: 0x0600E000, count: turning.Map.Length);
            // A rotating layer reads one byte per pixel, so it needs its own bank at character block one.
            var wideTiles = CartridgeGraphics.TilesEightBit(document: document);
            Copy(sourceAddress: Add(bytes: wideTiles), destination: 0x06004000, count: wideTiles.Length);
            affine = new AgbAffineBackground(emitter: emitter, tableAddress: Add(bytes: AgbAffineBackground.BuildTurnTable()));
            affine.EmitControl(control: AgbAffineBackground.Control(cellCount: turning.Map.Length, screenBlock: 28) | (1u << 2));
        }

        // The two surfaces behind the document's own background, nearest first; a turning background takes the nearer.
        var layerSlots = new (uint Control, uint ScrollX, uint Block, uint Enable)[] {
            (0x0400000C, 0x04000018, 0x0600E800, 0x0400),
            (0x0400000E, 0x0400001C, 0x0600D800, 0x0800),
        };
        var layerBase = document.Affine is null ? 0 : 1;
        for (var index = 0; index < document.Layers.Length; ++index) {
            var layer = document.Layers[index];
            var slot = layerSlots[layerBase + index];
            var layerCells = layer.MapPalettes is { } tinted
                ? layer.Map.Select(selector: (tile, cell) => tile | ((tinted[cell] & 0x0F) << 12)).ToArray()
                : layer.Map;
            Copy(sourceAddress: Add(bytes: CartridgeGraphics.Halfwords(values: layerCells)), destination: slot.Block, count: 2048);
            StoreHalf(address: slot.Control, value: ((slot.Block - 0x06000000u) / 0x800u << 8) | (uint)layer.Priority);
        }

        if (document.Window is { } panel) {
            var panelCells = panel.MapPalettes is { } tint
                ? panel.Map.Select(selector: (tile, index) => tile | ((tint[index] & 0x0F) << 12)).ToArray()
                : panel.Map;
            Copy(sourceAddress: Add(bytes: CartridgeGraphics.Halfwords(values: panelCells)), destination: 0x0600F000, count: 2048);
            // Screenblock 30, priority above the first layer.
            StoreHalf(address: 0x0400000A, value: 30 << 8);
            // A background layer tiles the whole screen, so the window unit is what clips the panel to its corner:
            // inside its rectangle the panel layer shows, outside only the background and objects do.
            // Bit 5 lets the blend unit run in each region; without it the window silently disables every blend and a
            // blend step appears to do nothing wherever the panel is.
            StoreHalf(address: 0x04000048, value: 0x0033);
            StoreHalf(address: 0x0400004A, value: 0x0031);
        }
        // All 128 hardware objects start disabled; authored slots are published each frame.
        emitter.LoadConstant(destination: LowRegister.R0, value: 0x07000000);
        emitter.MoveImmediate(destination: LowRegister.R1, value: 128);
        emitter.LoadConstant(destination: LowRegister.R2, value: 0x0200);
        var clear = emitter.NewLabel(); emitter.MarkLabel(label: clear);
        emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: 0, source: LowRegister.R2);
        emitter.AddImmediate(register: LowRegister.R0, value: 8);
        emitter.SubtractImmediate(register: LowRegister.R1, value: 1);
        emitter.Branch(condition: ThumbCondition.NotEqual, label: clear);
        foreach (var variable in document.Variables) {
            emitter.MoveImmediate(destination: LowRegister.R0, value: (byte)variable.Initial);
            emitter.LoadConstant(destination: LowRegister.R2, value: variables[variable.Name]);
            emitter.StoreByte(baseRegister: LowRegister.R2, byteOffset: 0, source: LowRegister.R0);
        }
        // Arrays pack contiguously from ArrayBaseAddress, so one ROM table seeds every one of them.
        var arrayInitial = document.Arrays.SelectMany(selector: static array => array.Initial.Select(selector: static value => (byte)value)).ToArray();
        if (arrayInitial.Length != 0) {
            CopyBytes(sourceAddress: Add(bytes: arrayInitial), destination: ArrayBaseAddress, count: arrayInitial.Length);
        }
        // Screenblock 31. With a panel present the background drops a priority so the panel layer draws over it.
        if (document.Bitmap is null) {
            StoreHalf(address: 0x04000008, value: (uint)((31 << 8) | (document.Window is null ? 0 : 1)));
        }

        StoreHalf(address: 0x04000000, value: DisplayControl(document: document));
        audio?.EmitBoot();
        digital?.EmitBoot();
        clock?.EmitBoot();

        Flush();
        var screens = document.Screens.ToDictionary(
            keySelector: static screen => screen.Name,
            elementSelector: screen => (Address: Add(bytes: CartridgeGraphics.Halfwords(values: screen.Tiles)), screen.Width, Height: screen.Tiles.Length / screen.Width),
            comparer: StringComparer.Ordinal);
        var loop = emitter.NewLabel(); emitter.MarkLabel(label: loop);
        kernel.EmitFrameSyncCall();
        if (raster is not null) {
            // Scroll republishes here rather than after the rules, because a burst must never read the table while it
            // is being written. A change therefore shows the following frame, exactly as a map write does.
            Load(value: document.ScrollX, register: LowRegister.R0); StoreResult(address: 0x04000010);
            Load(value: document.ScrollY, register: LowRegister.R0); StoreResult(address: 0x04000012);
            Flush();
            Band(line: 0, scrollX: document.ScrollX, scrollY: document.ScrollY);
            foreach (var row in document.Raster) {
                Band(line: row.Line, scrollX: row.ScrollX, scrollY: row.ScrollY);
            }

            raster.EmitRearm();
            Flush();
        }

        if (document.Bitmap is { } surface && surface.Clear is { } fill) {
            // A transfer rather than a loop: the surface is 38400 bytes, which no per-frame loop could cover and
            // still leave a frame's worth of rules to run.
            Load(value: fill, register: LowRegister.R0);
            emitter.MoveRegister(destination: LowRegister.R1, source: LowRegister.R0);
            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R1, source: LowRegister.R1, amount: 8);
            emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R0, source: LowRegister.R1);
            emitter.MoveRegister(destination: LowRegister.R1, source: LowRegister.R0);
            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R1, source: LowRegister.R1, amount: 16);
            emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R0, source: LowRegister.R1);
            emitter.LoadConstant(destination: LowRegister.R2, value: BitmapFillAddress);
            emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
            StoreWord(address: 0x040000D4, value: BitmapFillAddress);
            StoreWord(address: 0x040000D8, value: 0x06000000);
            StoreHalf(address: 0x040000DC, value: (uint)(CartridgeBitmap.Width * CartridgeBitmap.Height / 4));
            // Enable, immediate, whole words, source held still so one word paints the whole surface.
            StoreHalf(address: 0x040000DE, value: 0x8500);
            Flush();
        }

        EmitQueueDrain();
        audio?.EmitFrameTick();
        digital?.EmitFrameTick();
        Flush();
        foreach (var rule in document.Rules) {
            var end = emitter.NewLabel();
            Conditions(conditions: rule.When, fail: end);
            Statements(statements: rule.Body, breakLabel: -1);
            emitter.MarkLabel(label: end);
            Flush();
        }
        if (raster is null) {
            Load(value: document.ScrollX, register: LowRegister.R0); StoreResult(address: 0x04000010);
            Load(value: document.ScrollY, register: LowRegister.R0); StoreResult(address: 0x04000012);
        }

        // r4 accumulates the frame's display control across every surface below; Load never touches it.
        emitter.LoadConstant(destination: LowRegister.R4, value: DisplayControl(document: document));
        if (document.Affine is { } turning2) {
            var parked = emitter.NewLabel();
            Load(value: turning2.Visible, register: LowRegister.R0);
            emitter.CompareImmediate(register: LowRegister.R0, value: 0);
            emitter.Branch(condition: ThumbCondition.Equal, label: parked);
            affine!.EmitPlace(
                angle: register => Load(value: turning2.Angle, register: register),
                scale: register => Load(value: turning2.Scale, register: register),
                centreX: register => Load(value: turning2.CentreX, register: register),
                centreY: register => Load(value: turning2.CentreY, register: register));
            Enable(bit: 0x0400);
            emitter.MarkLabel(label: parked);
            Flush();
        }

        for (var index = 0; index < document.Layers.Length; ++index) {
            var layer = document.Layers[index];
            var slot = layerSlots[layerBase + index];
            var parked = emitter.NewLabel();
            Load(value: layer.Visible, register: LowRegister.R0);
            emitter.CompareImmediate(register: LowRegister.R0, value: 0);
            emitter.Branch(condition: ThumbCondition.Equal, label: parked);
            Load(value: layer.ScrollX, register: LowRegister.R0); StoreResult(address: slot.ScrollX);
            Load(value: layer.ScrollY, register: LowRegister.R0); StoreResult(address: slot.ScrollX + 2);
            Enable(bit: slot.Enable);
            emitter.MarkLabel(label: parked);
            Flush();
        }

        if (document.Window is { } placed) {
            var hidden = emitter.NewLabel();
            Load(value: placed.Visible, register: LowRegister.R0);
            emitter.CompareImmediate(register: LowRegister.R0, value: 0);
            emitter.Branch(condition: ThumbCondition.Equal, label: hidden);
            // Scrolling the layer by the negated corner puts its first cell at that corner.
            Load(value: placed.X, register: LowRegister.R0);
            emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
            emitter.SubtractRegister(destination: LowRegister.R0, source: LowRegister.R1, operand: LowRegister.R0);
            StoreResult(address: 0x04000014);
            Load(value: placed.Y, register: LowRegister.R0);
            emitter.MoveImmediate(destination: LowRegister.R1, value: 0);
            emitter.SubtractRegister(destination: LowRegister.R0, source: LowRegister.R1, operand: LowRegister.R0);
            StoreResult(address: 0x04000016);
            // The rectangle runs from the corner to the screen's edges.
            Load(value: placed.X, register: LowRegister.R0);
            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: 8);
            emitter.MoveImmediate(destination: LowRegister.R1, value: 240);
            emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R0, source: LowRegister.R1);
            StoreResult(address: 0x04000040);
            Load(value: placed.Y, register: LowRegister.R0);
            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: 8);
            emitter.MoveImmediate(destination: LowRegister.R1, value: 160);
            emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R0, source: LowRegister.R1);
            StoreResult(address: 0x04000044);
            Enable(bit: 0x2200);
            emitter.MarkLabel(label: hidden);
            Flush();
        }

        emitter.MoveRegister(destination: LowRegister.R0, source: LowRegister.R4);
        StoreResult(address: 0x04000000);
        Flush();
        for (var i = 0; i < document.Sprites.Length; ++i) {
            var sprite = document.Sprites[i];
            var hide = emitter.NewLabel(); var done = emitter.NewLabel();
            Load(value: sprite.Visible, register: LowRegister.R0);
            emitter.CompareImmediate(register: LowRegister.R0, value: 0); Require(condition: ThumbCondition.NotEqual, end: hide);
            Load(value: sprite.Tile, register: LowRegister.R0);
            if (document.Tiles.Length < 256) { emitter.CompareImmediate(register: LowRegister.R0, value: (byte)document.Tiles.Length); Require(condition: ThumbCondition.CarryClear, end: hide); }
            if (sprite.Palette is { } slot) {
                EmitFlagBits(value: slot, mask: 15, shift: 12);
            }

            // Attribute one's top bits name which parameter group a turning object reads.
            if (sprite.Turn is not null) {
                EmitFlagBits(value: new CartridgeValue(Constant: i & 0x1F), mask: 31, shift: 9);
            }

            if (sprite.BehindBackground is { } behind) {
                EmitFlagBits(value: behind, mask: 1, shift: 10);
            }

            StoreResult(address: (uint)(0x07000004 + i * 8));
            Load(value: sprite.X, register: LowRegister.R0); emitter.CompareImmediate(register: LowRegister.R0, value: 240); Require(condition: ThumbCondition.CarryClear, end: hide);
            // Mirroring and turning share attribute one's bits, so a turning object cannot also be mirrored.
            if (sprite.Turn is null) {
                if (sprite.FlipX is { } mirrorX) { EmitFlagBits(value: mirrorX, mask: 1, shift: 12); }
                if (sprite.FlipY is { } mirrorY) { EmitFlagBits(value: mirrorY, mask: 1, shift: 13); }
            }

            StoreResult(address: (uint)(0x07000002 + i * 8));
            Load(value: sprite.Y, register: LowRegister.R0); emitter.CompareImmediate(register: LowRegister.R0, value: 160); Require(condition: ThumbCondition.CarryClear, end: hide);
            if (sprite.Turn is not null) {
                emitter.MoveImmediate(destination: LowRegister.R1, value: 1);
                emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R1, source: LowRegister.R1, amount: 8);
                emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R0, source: LowRegister.R1);
            }

            if (document.TallSprites) {
                // Attribute zero's shape field: bits 14 and 15 hold 0b10 for the 8 by 16 object.
                emitter.MoveImmediate(destination: LowRegister.R1, value: 0x80);
                emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R1, source: LowRegister.R1, amount: 8);
                emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R0, source: LowRegister.R1);
            }

            StoreResult(address: (uint)(0x07000000 + i * 8));
            emitter.Branch(label: done);
            emitter.MarkLabel(label: hide); StoreHalf(address: (uint)(0x07000000 + i * 8), value: 0x0200);
            emitter.MarkLabel(label: done); Flush();
        }
        for (var i = 0; i < document.Sprites.Length; ++i) {
            if (document.Sprites[i].Turn is not { } turn) {
                continue;
            }

            // A parameter group's four halfwords sit every 32 bytes, in the gaps between object entries.
            spriteTurns!.EmitParameters(
                group: i & 0x1F,
                angle: register => Load(value: turn, register: register));
            Flush();
        }

        // BL supplies the Thumb-1 long jump; this loop never returns or consumes stack.
        emitter.Call(label: loop);
        kernel.EmitLibrary();
        arithmetic.EmitLibrary();
        byte[] rom;
        try {
            rom = AgbForgeCartridge.Build(title: document.Title, gameCode: document.GameCode, routine: emitter.ToArray(baseAddress: AgbForgeCartridge.CodeAddress), data: data.ToArray());
        } catch (Exception overrun) when (overrun is ArgumentException or ArgumentOutOfRangeException) {
            throw new CartridgeCapacityException(message: $"The document does not fit the advanced machine's cartridge: {overrun.Message}", innerException: overrun);
        }

        return new CartridgeCompilation(Rom: rom, SourceHash: source.Hash, Target: Target, Variables: variables, Arrays: arrays);

        // Publishes every queued cell into the screenblock, then empties the queue.
        void EmitQueueDrain() {
            var drain = emitter.NewLabel();
            var done = emitter.NewLabel();
            emitter.LoadConstant(destination: LowRegister.R2, value: QueueCountAddress);
            emitter.LoadByte(baseRegister: LowRegister.R2, byteOffset: 0, destination: LowRegister.R3);
            emitter.CompareImmediate(register: LowRegister.R3, value: 0);
            emitter.Branch(condition: ThumbCondition.Equal, label: done);
            emitter.LoadConstant(destination: LowRegister.R4, value: QueueBaseAddress);
            emitter.MarkLabel(label: drain);
            // address = 0x0600F800 + (row * 32 + column) * 2
            emitter.LoadByte(baseRegister: LowRegister.R4, byteOffset: 0, destination: LowRegister.R0);
            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: 5);
            emitter.LoadByte(baseRegister: LowRegister.R4, byteOffset: 1, destination: LowRegister.R1);
            emitter.AddRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R1);
            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: 1);
            emitter.LoadConstant(destination: LowRegister.R2, value: 0x0600F800u);
            emitter.AddRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R2);
            emitter.LoadByte(baseRegister: LowRegister.R4, byteOffset: 2, destination: LowRegister.R1);
            emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: 0, source: LowRegister.R1);
            emitter.AddImmediate(register: LowRegister.R4, value: 4);
            emitter.SubtractImmediate(register: LowRegister.R3, value: 1);
            emitter.Branch(condition: ThumbCondition.NotEqual, label: drain);
            emitter.LoadConstant(destination: LowRegister.R2, value: QueueCountAddress);
            emitter.MoveImmediate(destination: LowRegister.R0, value: 0);
            emitter.StoreByte(baseRegister: LowRegister.R2, byteOffset: 0, source: LowRegister.R0);
            emitter.MarkLabel(label: done);
            Flush();
        }

        // Appends one cell; validation has already bounded a frame's writes to the queue's capacity.
        void EmitQueuePush(CartridgeStatement statement) {
            Load(value: statement.Row!, register: LowRegister.R5);
            Load(value: statement.Column!, register: LowRegister.R6);
            Load(value: statement.Tile!, register: LowRegister.R7);
            emitter.LoadConstant(destination: LowRegister.R2, value: QueueCountAddress);
            emitter.LoadByte(baseRegister: LowRegister.R2, byteOffset: 0, destination: LowRegister.R1);
            emitter.LoadConstant(destination: LowRegister.R4, value: QueueBaseAddress);
            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R1, amount: 2);
            emitter.AddRegister(destination: LowRegister.R4, source: LowRegister.R4, operand: LowRegister.R0);
            emitter.MoveImmediate(destination: LowRegister.R0, value: 31);
            emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R5, source: LowRegister.R0);
            emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R6, source: LowRegister.R0);
            emitter.StoreByte(baseRegister: LowRegister.R4, byteOffset: 0, source: LowRegister.R5);
            emitter.StoreByte(baseRegister: LowRegister.R4, byteOffset: 1, source: LowRegister.R6);
            emitter.StoreByte(baseRegister: LowRegister.R4, byteOffset: 2, source: LowRegister.R7);
            emitter.AddImmediate(register: LowRegister.R1, value: 1);
            emitter.StoreByte(baseRegister: LowRegister.R2, byteOffset: 0, source: LowRegister.R1);
        }

        // A blit lands immediately and drops anything queued, matching the Color machine's display-off repaint.
        void EmitBlit(CartridgeStatement statement, uint sourceAddress, int width, int height) {
            emitter.LoadConstant(destination: LowRegister.R2, value: QueueCountAddress);
            emitter.MoveImmediate(destination: LowRegister.R0, value: 0);
            emitter.StoreByte(baseRegister: LowRegister.R2, byteOffset: 0, source: LowRegister.R0);
            for (var line = 0; line < height; ++line) {
                var destination = 0x0600F800u + (uint)(((statement.Row!.Constant!.Value + line) * 32) + statement.Column!.Constant!.Value) * 2u;
                Copy(sourceAddress: sourceAddress + (uint)(line * width * 2), destination: destination, count: width * 2);
            }
        }

        // Mode zero scrolls every layer; mode one gives the third layer the rotating and scaling hardware.
        // Sets one display-control bit in the frame's accumulator.
        void Enable(uint bit) {
            emitter.LoadConstant(destination: LowRegister.R0, value: bit);
            emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R4, source: LowRegister.R0);
        }

        // Always-on bits only: the video mode, objects and their one-dimensional mapping, and the first background.
        // Every surface that a document can hide adds its own bit each frame.
        static uint DisplayControl(CartridgeDocument document) => document.Bitmap is not null
            // Mode 4, first page: one byte per pixel over the whole screen. The surface is drawn by the third
            // background layer, so its enable bit is what puts it on screen at all.
            ? 0x1444u
            : document.Affine is null ? 0x1140u : 0x1041u;

        // Fills the table from a band's first scanline to the picture's end; ascending bands overwrite each other's tails.
        void Band(int line, CartridgeValue scrollX, CartridgeValue scrollY) {
            raster!.EmitFillFrom(
                line: line,
                scrollX: register => Load(value: scrollX, register: register),
                scrollY: register => Load(value: scrollY, register: register));
            Flush();
        }

        void Flush() {
            var resume = emitter.NewLabel();
            emitter.Branch(label: resume);
            emitter.EmitLiteralPool();
            emitter.MarkLabel(label: resume);
        }
        uint Add(byte[] bytes) { var address = AgbForgeCartridge.DataAddress + (uint)data.Count; data.AddRange(collection: bytes); return address; }

        // Jumps to fail when any condition misses; falls through when all hold.
        void Conditions(CartridgeCondition[] conditions, int fail) {
            foreach (var condition in conditions) {
                if (condition.Kind == "key") {
                    emitter.LoadConstant(destination: LowRegister.R2, value: AgbForgeMemoryMap.StateBase);
                    emitter.LoadHalf(baseRegister: LowRegister.R2, byteOffset: condition.Mode == "pressed" ? AgbForgeMemoryMap.InputPressedOffset : AgbForgeMemoryMap.InputHeldOffset, destination: LowRegister.R0);
                    if (condition.Mode == "released") {
                        emitter.LoadHalf(baseRegister: LowRegister.R2, byteOffset: AgbForgeMemoryMap.InputPreviousOffset, destination: LowRegister.R1);
                        emitter.Alu(op: ThumbAlu.BitClear, destination: LowRegister.R1, source: LowRegister.R0);
                        emitter.MoveRegister(destination: LowRegister.R0, source: LowRegister.R1);
                    }
                    emitter.MoveImmediate(destination: LowRegister.R1, value: Key(key: condition.Key!));
                    emitter.Alu(op: ThumbAlu.Test, destination: LowRegister.R0, source: LowRegister.R1);
                    Require(condition: ThumbCondition.NotEqual, end: fail);
                } else {
                    Load(value: condition.Left!, register: LowRegister.R0);
                    Load(value: condition.Right!, register: LowRegister.R1);
                    emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R0, source: LowRegister.R1);
                    Require(condition: condition.Comparison switch {
                        "eq" => ThumbCondition.Equal,
                        "ne" => ThumbCondition.NotEqual,
                        "lt" => ThumbCondition.CarryClear,
                        "le" => ThumbCondition.UnsignedLowerOrSame,
                        "gt" => ThumbCondition.UnsignedHigher,
                        _ => ThumbCondition.CarrySet,
                    }, end: fail);
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
                                emitter.Branch(label: joined);
                                emitter.MarkLabel(label: otherwise);
                                Statements(statements: alternative, breakLabel: breakLabel);
                                emitter.MarkLabel(label: joined);
                            } else {
                                emitter.MarkLabel(label: otherwise);
                            }

                            Flush();
                            break;
                        }
                    case "repeat": {
                            // A break leaves the index at the iteration that broke; normal completion leaves it at count.
                            var address = variables[statement.Index!];
                            var top = emitter.NewLabel();
                            var exit = emitter.NewLabel();
                            emitter.MoveImmediate(destination: LowRegister.R0, value: 0);
                            emitter.LoadConstant(destination: LowRegister.R2, value: address);
                            emitter.StoreByte(baseRegister: LowRegister.R2, byteOffset: 0, source: LowRegister.R0);
                            emitter.MarkLabel(label: top);
                            emitter.LoadConstant(destination: LowRegister.R2, value: address);
                            emitter.LoadByte(baseRegister: LowRegister.R2, byteOffset: 0, destination: LowRegister.R0);
                            emitter.CompareImmediate(register: LowRegister.R0, value: (byte)statement.Count!.Value);
                            Require(condition: ThumbCondition.CarryClear, end: exit);
                            Statements(statements: statement.Body!, breakLabel: exit);
                            emitter.LoadConstant(destination: LowRegister.R2, value: address);
                            emitter.LoadByte(baseRegister: LowRegister.R2, byteOffset: 0, destination: LowRegister.R0);
                            emitter.AddImmediate(register: LowRegister.R0, value: 1);
                            emitter.StoreByte(baseRegister: LowRegister.R2, byteOffset: 0, source: LowRegister.R0);
                            emitter.Branch(label: top);
                            emitter.MarkLabel(label: exit);
                            Flush();
                            break;
                        }
                    case "play": {
                            var sound = sounds[statement.Sound!];
                            if (sound.SampleLength > 0) { digital!.EmitStart(sampleAddress: sound.Address, sampleLength: sound.SampleLength, rate: statement.Rate is null ? null : register => Load(value: statement.Rate, register: register)); } else if (sound.IsMusic) { audio!.EmitStart(streamAddress: sound.Address); } else {
                                // The wave voice plays through a pattern, which must be in place before the voice starts.
                                if (sound.Pattern != 0u) { audio!.EmitWavePatternLoad(patternAddress: sound.Pattern); }

                                audio!.EmitEffectStart(streamAddress: sound.Address, voice: sound.Voice);
                            }

                            Flush();
                            break;
                        }
                    case "stop":
                        audio!.EmitStop();
                        Flush();
                        break;
                    case "save": {
                            var offset = 0;
                            foreach (var (address, length) in persisted) {
                                CopyBytes(sourceAddress: address, destination: SaveMirrorAddress + (uint)offset, count: length);
                                offset += length;
                            }

                            saver!.EmitStore();
                            Flush();
                            break;
                        }
                    case "load": {
                            saver!.EmitLoad();
                            var offset = 0;
                            foreach (var (address, length) in persisted) {
                                CopyBytes(sourceAddress: SaveMirrorAddress + (uint)offset, destination: address, count: length);
                                offset += length;
                            }

                            Flush();
                            break;
                        }
                    case "blend": {
                            // The blend unit mixes the named surface with whatever the picture has already drawn
                            // beneath it: the weight is the top's share in sixteenths, the rest comes from below.
                            var surface = 1u << Array.IndexOf(array: CartridgeLimits.BlendSurfaces, value: statement.Surface!);
                            var control = surface | 0x40u | ((0x3Fu & ~surface) << 8);
                            Load(value: statement.Weight!, register: LowRegister.R0);
                            emitter.MoveImmediate(destination: LowRegister.R1, value: CartridgeLimits.BlendWeights);
                            emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R0, source: LowRegister.R1);
                            var held = emitter.NewLabel();
                            emitter.Branch(condition: ThumbCondition.UnsignedLowerOrSame, label: held);
                            emitter.MoveImmediate(destination: LowRegister.R0, value: CartridgeLimits.BlendWeights);
                            emitter.MarkLabel(label: held);
                            emitter.MoveImmediate(destination: LowRegister.R1, value: CartridgeLimits.BlendWeights);
                            emitter.SubtractRegister(destination: LowRegister.R1, source: LowRegister.R1, operand: LowRegister.R0);
                            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R1, source: LowRegister.R1, amount: 8);
                            emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R0, source: LowRegister.R1);
                            StoreResult(address: 0x04000052);
                            emitter.LoadConstant(destination: LowRegister.R0, value: control);
                            StoreResult(address: 0x04000050);
                            Flush();
                            break;
                        }
                    case "fade": {
                            // This machine fades in hardware: every layer and the backdrop are selected as targets, the
                            // mode picks brighten or darken, and the level register carries the step.
                            emitter.LoadConstant(destination: LowRegister.R0, value: statement.Toward == "white" ? 0x3FBFu : 0x3FFFu);
                            StoreResult(address: 0x04000050);
                            Load(value: statement.Amount!, register: LowRegister.R0);
                            emitter.MoveImmediate(destination: LowRegister.R1, value: 16);
                            emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R0, source: LowRegister.R1);
                            var clamped = emitter.NewLabel();
                            emitter.Branch(condition: ThumbCondition.UnsignedLowerOrSame, label: clamped);
                            emitter.MoveImmediate(destination: LowRegister.R0, value: 16);
                            emitter.MarkLabel(label: clamped);
                            StoreResult(address: 0x04000054);
                            Flush();
                            break;
                        }
                    case "clock": {
                            clock!.EmitRead();
                            foreach (var (name, address) in new[] {
                                (document.Clock!.Seconds, clock.SecondAddress), (document.Clock.Minutes, clock.MinuteAddress),
                                (document.Clock.Hours, clock.HourAddress), (document.Clock.Day, clock.DayAddress),
                                (document.Clock.Month, clock.MonthAddress), (document.Clock.Year, clock.YearAddress),
                            }) {
                                if (name is null) {
                                    continue;
                                }

                                emitter.LoadConstant(destination: LowRegister.R2, value: address);
                                emitter.LoadByte(destination: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
                                emitter.LoadConstant(destination: LowRegister.R2, value: variables[name]);
                                emitter.StoreByte(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
                                Flush();
                            }

                            Flush();
                            break;
                        }
                    case "plot": {
                            var dropped = emitter.NewLabel();
                            var odd = emitter.NewLabel();
                            var written = emitter.NewLabel();

                            // Off the surface is dropped rather than wrapped, which would draw on another row.
                            Load(value: statement.Column!, register: LowRegister.R5);
                            emitter.LoadConstant(destination: LowRegister.R0, value: CartridgeBitmap.Width);
                            emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R5, source: LowRegister.R0);
                            emitter.Branch(condition: ThumbCondition.UnsignedHigher, label: dropped);
                            emitter.Branch(condition: ThumbCondition.Equal, label: dropped);
                            Load(value: statement.Row!, register: LowRegister.R6);
                            emitter.LoadConstant(destination: LowRegister.R0, value: CartridgeBitmap.Height);
                            emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R6, source: LowRegister.R0);
                            emitter.Branch(condition: ThumbCondition.UnsignedHigher, label: dropped);
                            emitter.Branch(condition: ThumbCondition.Equal, label: dropped);

                            // r5 becomes the pixel's byte offset: row times the width, plus the column.
                            emitter.LoadConstant(destination: LowRegister.R0, value: CartridgeBitmap.Width);
                            emitter.Alu(op: ThumbAlu.Multiply, destination: LowRegister.R6, source: LowRegister.R0);
                            emitter.AddRegister(destination: LowRegister.R5, source: LowRegister.R5, operand: LowRegister.R6);
                            Load(value: statement.Colour!, register: LowRegister.R7);
                            emitter.LoadConstant(destination: LowRegister.R0, value: 0x06000000u);
                            emitter.AddRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R5);

                            // Video memory ignores a single-byte write, so the pixel's halfword is read and rebuilt.
                            emitter.MoveImmediate(destination: LowRegister.R1, value: 1);
                            emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R5, source: LowRegister.R1);
                            emitter.MoveImmediate(destination: LowRegister.R1, value: 1);
                            emitter.Alu(op: ThumbAlu.BitClear, destination: LowRegister.R0, source: LowRegister.R1);
                            emitter.LoadHalf(destination: LowRegister.R6, baseRegister: LowRegister.R0, byteOffset: 0);
                            emitter.CompareImmediate(register: LowRegister.R5, value: 0);
                            emitter.Branch(condition: ThumbCondition.NotEqual, label: odd);
                            emitter.LoadConstant(destination: LowRegister.R1, value: 0xFF00u);
                            emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R6, source: LowRegister.R1);
                            emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R6, source: LowRegister.R7);
                            emitter.Branch(label: written);
                            emitter.MarkLabel(label: odd);
                            emitter.MoveImmediate(destination: LowRegister.R1, value: 0xFF);
                            emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R6, source: LowRegister.R1);
                            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R7, source: LowRegister.R7, amount: 8);
                            emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R6, source: LowRegister.R7);
                            emitter.MarkLabel(label: written);
                            emitter.StoreHalf(source: LowRegister.R6, baseRegister: LowRegister.R0, byteOffset: 0);
                            emitter.MarkLabel(label: dropped);
                            Flush();
                            break;
                        }
                    case "map":
                        EmitQueuePush(statement: statement);
                        Flush();
                        break;
                    case "blit": {
                            var screen = screens[statement.Screen!];
                            EmitBlit(statement: statement, sourceAddress: screen.Address, width: screen.Width, height: screen.Height);
                            break;
                        }
                    default:
                        emitter.Branch(label: breakLabel);
                        break;
                }
            }
        }

        // A "set" evaluates the operand first because the destination address computation leaves r0 alone; every other
        // operation keeps the operand in r1 and the destination address in r4, so an indexed destination is computed once.
        void Act(CartridgeStatement action) {
            if (action.Operation == "set") {
                Load(value: action.Value!, register: LowRegister.R0);
                Address(target: action.Target!);
                emitter.StoreByte(baseRegister: LowRegister.R4, byteOffset: 0, source: LowRegister.R0);
                return;
            }

            Load(value: action.Value!, register: LowRegister.R1);
            Address(target: action.Target!);
            emitter.LoadByte(baseRegister: LowRegister.R4, byteOffset: 0, destination: LowRegister.R0);
            switch (action.Operation) {
                case "add": emitter.AddRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R1); break;
                case "subtract": emitter.SubtractRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R1); break;
                case "mul": arithmetic.EmitMultiply(); break;
                case "div": arithmetic.EmitDivide(); break;
                case "mod": arithmetic.EmitDivide(); emitter.MoveRegister(destination: LowRegister.R0, source: LowRegister.R5); break;
                case "shl": arithmetic.EmitShiftLeft(); break;
                case "shr": arithmetic.EmitShiftRight(); break;
                default: emitter.Alu(op: action.Operation switch { "and" => ThumbAlu.And, "or" => ThumbAlu.Or, _ => ThumbAlu.ExclusiveOr }, destination: LowRegister.R0, source: LowRegister.R1); break;
            }
            emitter.StoreByte(baseRegister: LowRegister.R4, byteOffset: 0, source: LowRegister.R0);
        }

        void Load(CartridgeValue value, LowRegister register) {
            if (value.Constant is { } constant) {
                emitter.MoveImmediate(destination: register, value: (byte)constant);
            } else if (value.Variable is { } name) {
                emitter.LoadConstant(destination: LowRegister.R2, value: variables[name]);
                emitter.LoadByte(baseRegister: LowRegister.R2, byteOffset: 0, destination: register);
            } else {
                Element(array: value.Array!, index: value.Index!, address: LowRegister.R2);
                emitter.LoadByte(baseRegister: LowRegister.R2, byteOffset: 0, destination: register);
            }
        }

        void Address(CartridgeTarget target) {
            if (target.Variable is { } name) {
                emitter.LoadConstant(destination: LowRegister.R4, value: variables[name]);
            } else {
                Element(array: target.Array!, index: target.Index!, address: LowRegister.R4);
            }
        }

        // Leaves the addressed element in the given register, or the zeroed discard sink when the index is past the
        // declared length. The index is consumed in r3 immediately, so a nested array index reuses it safely.
        void Element(string array, CartridgeValue index, LowRegister address) {
            var done = emitter.NewLabel();
            var inside = emitter.NewLabel();
            var length = lengths[key: array];
            Load(value: index, register: LowRegister.R3);
            if (length < CartridgeLimits.ArrayLength) {
                emitter.CompareImmediate(register: LowRegister.R3, value: (byte)length);
                emitter.Branch(condition: ThumbCondition.CarryClear, label: inside);
                emitter.MoveImmediate(destination: LowRegister.R3, value: 0);
                emitter.LoadConstant(destination: address, value: VoidAddress);
                emitter.StoreByte(baseRegister: address, byteOffset: 0, source: LowRegister.R3);
                emitter.Branch(label: done);
            }

            emitter.MarkLabel(label: inside);
            emitter.LoadConstant(destination: address, value: arrays[array]);
            emitter.AddRegister(destination: address, source: address, operand: LowRegister.R3);
            emitter.MarkLabel(label: done);
        }

        // Folds a document value into the attribute halfword already being built in r0.
        void EmitFlagBits(CartridgeValue value, byte mask, int shift) {
            emitter.MoveRegister(destination: LowRegister.R5, source: LowRegister.R0);
            Load(value: value, register: LowRegister.R0);
            emitter.MoveImmediate(destination: LowRegister.R1, value: mask);
            if (mask == 1) {
                // Any non-zero value sets the bit, so collapse it rather than masking its low bit away.
                var zero = emitter.NewLabel();
                var joined = emitter.NewLabel();
                emitter.CompareImmediate(register: LowRegister.R0, value: 0);
                emitter.Branch(condition: ThumbCondition.Equal, label: zero);
                emitter.MoveImmediate(destination: LowRegister.R0, value: 1);
                emitter.Branch(label: joined);
                emitter.MarkLabel(label: zero);
                emitter.MoveImmediate(destination: LowRegister.R0, value: 0);
                emitter.MarkLabel(label: joined);
            } else {
                emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R0, source: LowRegister.R1);
            }

            emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: shift);
            emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R0, source: LowRegister.R5);
        }

        void StoreResult(uint address) {
            emitter.LoadConstant(destination: LowRegister.R2, value: address);
            emitter.StoreHalf(baseRegister: LowRegister.R2, byteOffset: 0, source: LowRegister.R0);
        }
        void StoreHalf(uint address, uint value) { emitter.LoadConstant(destination: LowRegister.R0, value: value); StoreResult(address: address); }
        void StoreWord(uint address, uint value) {
            emitter.LoadConstant(destination: LowRegister.R0, value: value);
            emitter.LoadConstant(destination: LowRegister.R2, value: address);
            emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
        }
        void Require(ThumbCondition condition, int end) {
            var accepted = emitter.NewLabel();
            emitter.Branch(condition: condition, label: accepted);
            emitter.Branch(label: end);
            emitter.MarkLabel(label: accepted);
        }
        void CopyBytes(uint sourceAddress, uint destination, int count) {
            emitter.LoadConstant(destination: LowRegister.R0, value: sourceAddress);
            emitter.LoadConstant(destination: LowRegister.R1, value: destination);
            emitter.LoadConstant(destination: LowRegister.R2, value: (uint)count);
            var copy = emitter.NewLabel(); emitter.MarkLabel(label: copy);
            emitter.LoadByte(baseRegister: LowRegister.R0, byteOffset: 0, destination: LowRegister.R3);
            emitter.StoreByte(baseRegister: LowRegister.R1, byteOffset: 0, source: LowRegister.R3);
            emitter.AddImmediate(register: LowRegister.R0, value: 1); emitter.AddImmediate(register: LowRegister.R1, value: 1);
            emitter.SubtractImmediate(register: LowRegister.R2, value: 1); emitter.Branch(condition: ThumbCondition.NotEqual, label: copy);
            Flush();
        }
        void Copy(uint sourceAddress, uint destination, int count) {
            emitter.LoadConstant(destination: LowRegister.R0, value: sourceAddress);
            emitter.LoadConstant(destination: LowRegister.R1, value: destination);
            emitter.LoadConstant(destination: LowRegister.R2, value: (uint)(count / 2));
            var copy = emitter.NewLabel(); emitter.MarkLabel(label: copy);
            emitter.LoadHalf(baseRegister: LowRegister.R0, byteOffset: 0, destination: LowRegister.R3);
            emitter.StoreHalf(baseRegister: LowRegister.R1, byteOffset: 0, source: LowRegister.R3);
            emitter.AddImmediate(register: LowRegister.R0, value: 2); emitter.AddImmediate(register: LowRegister.R1, value: 2);
            emitter.SubtractImmediate(register: LowRegister.R2, value: 1); emitter.Branch(condition: ThumbCondition.NotEqual, label: copy);
            Flush();
        }
    }

    private static byte Key(string key) => key switch {
        "a" => 1,
        "b" => 2,
        "select" => 4,
        "start" => 8,
        "right" => 16,
        "left" => 32,
        "up" => 64,
        "down" => 128,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(key)),
    };
}
