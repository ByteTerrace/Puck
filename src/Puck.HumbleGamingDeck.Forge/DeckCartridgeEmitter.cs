using Puck.Assets.Documents;
using Puck.GamingBricks.Forge;
using Puck.State;
using static Puck.HumbleGamingDeck.Forge.DeckOpcodes;

namespace Puck.HumbleGamingDeck.Forge;

/// <summary>
/// Lowers one validated hgd document onto one board: the memory map, the boot, the NMI-paced frame, the rule lowering
/// over <see cref="CartridgeRuleLowering"/>, and the libraries the lowering calls.
/// </summary>
/// <remarks>
/// <para><b>Frame.</b> The main loop waits for the NMI to count a frame, reads the pad, steps the sound driver, runs the
/// rules, publishes the scroll and the sprites into shadows, and raises <c>ready</c>. The NMI publishes the shadows (OAM
/// DMA, a changed palette, the queued nametable writes, then scroll, control and mask) only while <c>ready</c> is raised,
/// and lowers it; a frame whose rules overrun the picture is published at the next NMI instead, so the PPU never sees a
/// half-built frame. Map writes therefore land on screen the frame after the rule that queued them, as on the Bricks.
/// </para>
/// <para><b>Picture.</b> The nametables mirror horizontally and the background draws only from the first, so one copy of
/// the 32-column map wraps horizontally at 256 pixels and vertically at 240: the document's last two rows are never
/// shown, and a vertical scroll of 240 or more wraps to the top. A map cell's palette sets its 16-pixel quadrant's
/// attribute, so the last cell written in a quadrant colours all four. The tile bank sits in both pattern tables, the
/// background reading the first and sprites the second, so a tall sprite's pair resolves wherever its index points.</para>
/// <para><b>Memory.</b> Zero page holds the frame's scratch, the variables at $20 (the 128-byte window
/// <see cref="CartridgeLimits.VariableCount"/> bounds) and the sound driver's state; page 2 is the OAM shadow and page 3
/// the attribute and palette shadows and the nametable write queue. Arrays sit in internal RAM at $0400 while they fit
/// its 1 KiB, and otherwise in the board's PRG-RAM at $6000, whose last kilobyte from $7C00 holds the battery block.</para>
/// <para><b>Banks.</b> Code stays in the 16 KiB fixed at $C000. Data fills 16 KiB pages mapped at $8000: page 0 stays
/// mapped while the game runs and holds what the frame reads (sound streams, fade tables, save defaults); boot-only data
/// and screens take any page, and the code that reads them maps their page around the read.</para>
/// </remarks>
internal sealed class DeckCartridgeEmitter {
    // Zero page.
    private const byte Frame = 0x00;
    private const byte Ready = 0x01;
    private const byte Held = 0x02;
    private const byte Previous = 0x03;
    private const byte Pressed = 0x04;
    private const byte Released = 0x05;
    private const byte QueueCount = 0x06;
    private const byte ControlShadow = 0x07;
    private const byte MaskShadow = 0x08;
    private const byte ScrollXShadow = 0x09;
    private const byte ScrollYShadow = 0x0A;
    private const byte SceneSnapshot = 0x0B;
    private const byte PaletteDirty = 0x0C;
    private const byte Void = 0x0D;
    private const byte PointerP = 0x0E;
    private const byte PointerQ = 0x10;
    private const byte Count = 0x12;
    private const byte T0 = 0x14;
    private const byte T1 = 0x15;
    private const byte M0 = 0x16;
    private const byte M1 = 0x17;
    private const byte M2 = 0x18;
    private const byte CellRow = 0x19;
    private const byte CellColumn = 0x1A;
    private const byte CellTile = 0x1B;
    private const byte CellPalette = 0x1C;
    private const byte Attribute = 0x1D;
    private const byte VariableBase = 0x20;
    private const byte WideLeft = 0xA0;
    private const byte WideRight = 0xA2;
    private const byte VoicePointerLow = 0xA4;
    private const byte VoicePointerHigh = 0xA8;
    private const byte VoiceWait = 0xAC;
    private const byte VoiceLoopLow = 0xB0;
    private const byte VoiceLoopHigh = 0xB4;
    private const byte SoundPointer = 0xB8;
    private const byte SoundRow = 0xBA;
    private const byte SoundRegister = 0xBE;
    private const byte Checksum = 0xBF;
    // Internal RAM.
    private const ushort OamShadow = 0x0200;
    private const ushort AttributeShadow = 0x0300;
    private const ushort PaletteShadow = 0x0340;
    private const ushort QueueHigh = 0x0360;
    private const ushort QueueLow = 0x0390;
    private const ushort QueueValue = 0x03C0;
    private const ushort InternalArrays = 0x0400;
    private const int InternalArrayBytes = 0x0400;
    // Board PRG-RAM.
    private const ushort RamArrays = 0x6000;
    private const ushort SaveBlock = 0x7C00;
    // The picture: 30 visible rows of 32 cells, and 64 attribute bytes after them.
    private const int VisibleRows = 30;
    private const int PageSize = 0x4000;

    private readonly DeckBoard m_board;
    private readonly CartridgeDocument m_document;
    private readonly CartridgeStateLayout m_layout;
    private readonly DeckProgram m_program = new(origin: 0xC000);
    private readonly List<List<byte>> m_pages = [[]];
    private readonly Dictionary<string, (int Address, int Width)> m_variables = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, int> m_arrays = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_libraries = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, (int Page, int Address, int Width, int Height, (int Index, int Mask, int Bits)[] Attributes)> m_screens = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Music, (int Address, int Channel)[] Parts)> m_sounds = new(comparer: StringComparer.Ordinal);
    private readonly int[] m_musicChannels;
    private int m_fadeBlack;
    private int m_fadeWhite;
    private int m_saveDefaults;

    /// <summary>Initializes a new instance of the <see cref="DeckCartridgeEmitter"/> class for one document and board.</summary>
    /// <param name="board">The board the image is built for.</param>
    /// <param name="document">The canonical, validated hgd document.</param>
    public DeckCartridgeEmitter(DeckBoard board, CartridgeDocument document) {
        m_board = board;
        m_document = document;
        m_layout = new CartridgeStateLayout(document: document);
        m_musicChannels = [.. document.Sounds
            .SelectMany(selector: static sound => (sound.Music ?? []))
            .Select(selector: static part => DeckSoundCompiler.Channel(voice: part.Voice))
            .Distinct()
            .Order()];
        foreach (var (name, slot) in m_layout.Slots) {
            m_variables[name] = ((VariableBase + slot.Offset), slot.Width);
        }

        var arrayBase = (int)InternalArrays;

        if (m_layout.ArrayInitial.Length > InternalArrayBytes) {
            if (!DeckBoards.HasPrgRam(board: board)) {
                throw new CartridgeCapacityException(message: $"Arrays need {m_layout.ArrayInitial.Length} bytes; an nrom board's internal RAM holds {InternalArrayBytes}, and it has no PRG-RAM.");
            }
            arrayBase = RamArrays;
        }
        foreach (var (name, offset) in m_layout.ArrayOffsets) {
            m_arrays[name] = (arrayBase + offset);
        }
    }

    /// <summary>Gets the bus address of each named state slot.</summary>
    public IReadOnlyDictionary<string, uint> Variables => m_variables.ToDictionary(keySelector: static pair => pair.Key, elementSelector: static pair => ((uint)pair.Value.Address), comparer: StringComparer.Ordinal);
    /// <summary>Gets the bus address of each named array's first element.</summary>
    public IReadOnlyDictionary<string, uint> Arrays => m_arrays.ToDictionary(keySelector: static pair => pair.Key, elementSelector: static pair => ((uint)pair.Value), comparer: StringComparer.Ordinal);

    private bool Banked => (m_board != DeckBoard.Nrom);

    /// <summary>Emits the whole image.</summary>
    /// <returns>The iNES (nrom) or NES 2.0 (mmc1, mmc3) image.</returns>
    /// <exception cref="CartridgeCapacityException">The code, a data page or the page count outgrows the board.</exception>
    public byte[] Emit() {
        // Resident data first, so page 0 (the one the frame keeps mapped) holds it.
        foreach (var sound in m_document.Sounds) {
            m_sounds[sound.Name] = ((sound.Music is { } music)
                ? (true, music.Select(selector: part => (Place(bytes: DeckSoundCompiler.CompileMusicVoice(document: AudioCanonicalizer.Normalize(document: part.Part), voice: part.Voice), resident: true).Address, DeckSoundCompiler.Channel(voice: part.Voice))).ToArray())
                : (false, [(Place(bytes: DeckSoundCompiler.CompileEffect(effect: sound.Effect!, frames: sound.Frames!.Value), resident: true).Address, DeckSoundCompiler.Channel(voice: sound.Effect!.Voice))]));
        }
        if (UsesStep(kind: "fade")) {
            m_fadeBlack = Place(bytes: FadeTable(toWhite: false), resident: true).Address;
            m_fadeWhite = Place(bytes: FadeTable(toWhite: true), resident: true).Address;
        }
        if (m_document.Save is not null) {
            m_saveDefaults = Place(bytes: m_layout.SaveDefaults, resident: true).Address;
        }

        var picture = Place(bytes: Nametable(), resident: false);
        var palettes = Place(bytes: FadeTable(toWhite: false).AsSpan(start: 0, length: 32).ToArray(), resident: false);
        var arrays = ((m_layout.ArrayInitial.Length == 0) ? default : Place(bytes: m_layout.ArrayInitial, resident: false));

        foreach (var screen in m_document.Screens) {
            var placed = Place(bytes: [.. screen.Tiles.Select(selector: static tile => ((byte)tile))], resident: false);

            m_screens[screen.Name] = (placed.Page, placed.Address, screen.Width, (screen.Tiles.Length / screen.Width), []);
        }

        EmitBoot(arrays: arrays, palettes: palettes, picture: picture);
        EmitFrame();
        EmitNmi();
        EmitLibraries();

        return Pack();
    }

    // ---- Data placement ------------------------------------------------------------------------------------------

    // Places a table in a data page: page 0 for one the running frame reads, else the first page with room.
    private (int Page, int Address) Place(byte[] bytes, bool resident) {
        if (bytes.Length > PageSize) {
            throw new CartridgeCapacityException(message: $"A {bytes.Length}-byte table outgrows a 16 KiB data page.");
        }

        for (var page = 0; ; ++page) {
            if (page == m_pages.Count) {
                if (resident || (page >= DeckBoards.DataPages(board: m_board))) {
                    throw new CartridgeCapacityException(message: (resident
                        ? $"The data the running frame reads outgrows the {PageSize}-byte page kept mapped at $8000."
                        : $"The data outgrows the {DeckBoards.DataPages(board: m_board)} data page(s) of the {m_board} board."));
                }
                m_pages.Add(item: []);
            }

            var contents = m_pages[page];

            if ((contents.Count + bytes.Length) <= PageSize) {
                var address = (0x8000 + contents.Count);

                contents.AddRange(collection: bytes);

                return (page, address);
            }
            if (resident) {
                throw new CartridgeCapacityException(message: $"The data the running frame reads outgrows the {PageSize}-byte page kept mapped at $8000.");
            }
        }
    }
    // The 30 visible rows of the map, then the attribute table, each quadrant taking its top-left cell's palette.
    private byte[] Nametable() {
        var bytes = new byte[1024];

        for (var cell = 0; (cell < (VisibleRows * 32)); ++cell) {
            bytes[cell] = ((byte)m_document.Map[cell]);
        }
        if (m_document.MapPalettes is { } palettes) {
            for (var cell = 0; (cell < (VisibleRows * 32)); ++cell) {
                var row = (cell >> 5);
                var column = (cell & 31);

                if (((row & 1) == 0) && ((column & 1) == 0)) {
                    var (index, shift) = AttributeSlot(row: row, column: column);

                    bytes[(960 + index)] |= ((byte)((palettes[cell] & 3) << shift));
                }
            }
        }

        return bytes;
    }
    private static (int Index, int Shift) AttributeSlot(int row, int column) =>
        ((((row >> 2) << 3) | (column >> 2)), (((row & 2) << 1) | (column & 2)));
    // Every fade level's 32 palette entries: level 0 is the palettes at rest, each further level a brightness row
    // darker (toward black) or lighter (toward white). Every colour zero is the backdrop, the first palette's first.
    private byte[] FadeTable(bool toWhite) {
        var rest = new byte[32];
        var backdrop = m_document.Palettes.Background[0][0];

        for (var bank = 0; (bank < 2); ++bank) {
            var palettes = ((bank == 0) ? m_document.Palettes.Background : m_document.Palettes.Object);

            for (var palette = 0; (palette < 4); ++palette) {
                for (var colour = 0; (colour < 4); ++colour) {
                    rest[(((bank * 16) + (palette * 4)) + colour)] = ((byte)((colour == 0)
                        ? backdrop
                        : ((palette < palettes.Length) ? palettes[palette][colour] : backdrop)));
                }
            }
        }

        var table = new byte[(FadeLevels * 32)];

        for (var level = 0; (level < FadeLevels); ++level) {
            for (var entry = 0; (entry < 32); ++entry) {
                table[((level * 32) + entry)] = Faded(colour: rest[entry], level: level, toWhite: toWhite);
            }
        }

        return table;
    }
    private const int FadeLevels = 5;
    // The master palette is four brightness rows of sixteen hues. Columns $E and $F and the first two rows of column $D
    // are black; the rest of column $D is grey. A level moves a colour that many rows, darkening past the first row to
    // black, lightening past the last to white, and lightening black through the grey column.
    private static byte Faded(int colour, int level, bool toWhite) {
        var row = (colour >> 4);
        var hue = (colour & 0x0F);
        var black = ((hue >= 0x0E) || (colour is 0x0D or 0x1D));

        if (level == 0) {
            return ((byte)colour);
        }
        if (toWhite) {
            return (black
                ? ((byte)((level - 1) << 4))
                : (((row + level) > 3) ? (byte)0x30 : ((byte)(((row + level) << 4) | hue))));
        }

        return ((black || ((row - level) < 0)) ? (byte)0x0F : ((byte)(((row - level) << 4) | hue)));
    }
    private bool UsesStep(string kind) =>
        m_document.Rules.Any(predicate: rule => Uses(kind: kind, statements: rule.Body));
    private static bool Uses(string kind, CartridgeStatement[]? statements) =>
        (statements ?? []).Any(predicate: statement => ((statement.Kind == kind) || Uses(kind: kind, statements: statement.Then) || Uses(kind: kind, statements: statement.Else) || Uses(kind: kind, statements: statement.Body)));

    // ---- Instruction helpers -------------------------------------------------------------------------------------

    private void Op(byte opcode) => m_program.Bytes(opcode);
    private void Op(byte opcode, int operand) => m_program.Op(opcode: opcode, operand: ((byte)operand));
    private void Abs(byte opcode, int address) => m_program.Op(opcode: opcode, address: ((ushort)address));
    private void Mark(string label) => m_program.Label(name: label);
    private string NewLabel() => m_program.NewLabel();
    private void JumpTo(string label) => m_program.Jump(opcode: Jmp, label: label);
    private void Call(string library) {
        m_libraries.Add(item: library);
        m_program.Jump(opcode: Jsr, label: library);
    }
    private void Far(byte branch, string label) => m_program.BranchFar(opcode: branch, label: label);
    private void LoadImmediate(int value) => Op(opcode: LdaImmediate, operand: value);
    private void Pointer(byte zeroPage, int address) {
        LoadImmediate(value: (address & 0xFF));
        Op(opcode: StaZeroPage, operand: zeroPage);
        LoadImmediate(value: (address >> 8));
        Op(opcode: StaZeroPage, operand: (zeroPage + 1));
    }
    private void SetCount(int count) =>
        Pointer(zeroPage: Count, address: count);
    // Maps a data page at $8000; nrom's one page never moves.
    private void SelectPage(int page) {
        if (!Banked) {
            return;
        }

        LoadImmediate(value: page);
        Call(library: "select-page");
    }

    // ---- Boot ----------------------------------------------------------------------------------------------------

    private void EmitBoot((int Page, int Address) arrays, (int Page, int Address) palettes, (int Page, int Address) picture) {
        var warm1 = NewLabel();
        var warm2 = NewLabel();
        var clear = NewLabel();
        var hide = NewLabel();

        // Interrupts and decimal mode off, the APU frame IRQ inhibited, the stack at $01FF, the PPU and the DMC silent.
        Op(opcode: Sei);
        Op(opcode: Cld);
        Op(opcode: LdxImmediate, operand: 0x40);
        Abs(opcode: StxAbsolute, address: 0x4017);
        Op(opcode: LdxImmediate, operand: 0xFF);
        Op(opcode: Txs);
        Op(opcode: Inx);
        Abs(opcode: StxAbsolute, address: 0x2000);
        Abs(opcode: StxAbsolute, address: 0x2001);
        Abs(opcode: StxAbsolute, address: 0x4010);
        Abs(opcode: BitAbsolute, address: 0x2002);
        Mark(label: warm1);
        Abs(opcode: BitAbsolute, address: 0x2002);
        m_program.Branch(opcode: Bpl, label: warm1);
        // Internal RAM to zero, then every sprite parked below the picture.
        LoadImmediate(value: 0);
        Op(opcode: Tax);
        Mark(label: clear);
        foreach (var page in (ReadOnlySpan<int>)[0x0000, 0x0100, 0x0300, 0x0400, 0x0500, 0x0600, 0x0700]) {
            Abs(opcode: StaAbsoluteX, address: page);
        }
        Op(opcode: Inx);
        m_program.Branch(opcode: Bne, label: clear);
        LoadImmediate(value: 0xFF);
        Mark(label: hide);
        Abs(opcode: StaAbsoluteX, address: OamShadow);
        Op(opcode: Inx);
        m_program.Branch(opcode: Bne, label: hide);
        Mark(label: warm2);
        Abs(opcode: BitAbsolute, address: 0x2002);
        m_program.Branch(opcode: Bpl, label: warm2);
        EmitBoardSetup();
        // Every tone channel enabled; the pulse sweeps negated at shift zero, so a low note is never muted.
        LoadImmediate(value: 0x0F);
        Abs(opcode: StaAbsolute, address: 0x4015);
        LoadImmediate(value: 0x08);
        Abs(opcode: StaAbsolute, address: 0x4001);
        Abs(opcode: StaAbsolute, address: 0x4005);
        // Palettes into the shadow and the PPU.
        SelectPage(page: palettes.Page);
        Pointer(zeroPage: PointerP, address: palettes.Address);
        Pointer(zeroPage: PointerQ, address: PaletteShadow);
        SetCount(count: 32);
        Call(library: "copy-ram");
        PpuAddress(address: 0x3F00);
        Pointer(zeroPage: PointerP, address: PaletteShadow);
        SetCount(count: 32);
        Call(library: "copy-ppu");
        // The first nametable and its attribute shadow.
        SelectPage(page: picture.Page);
        PpuAddress(address: 0x2000);
        Pointer(zeroPage: PointerP, address: picture.Address);
        SetCount(count: 1024);
        Call(library: "copy-ppu");
        Pointer(zeroPage: PointerP, address: (picture.Address + 960));
        Pointer(zeroPage: PointerQ, address: AttributeShadow);
        SetCount(count: 64);
        Call(library: "copy-ram");
        // State at its authored values.
        foreach (var variable in m_document.Variables) {
            var (address, width) = m_variables[variable.Name];

            LoadImmediate(value: (variable.Initial & 0xFF));
            Op(opcode: StaZeroPage, operand: address);
            if (width == 2) {
                LoadImmediate(value: (variable.Initial >> 8));
                Op(opcode: StaZeroPage, operand: (address + 1));
            }
        }
        if (m_layout.ArrayInitial.Length != 0) {
            SelectPage(page: arrays.Page);
            Pointer(zeroPage: PointerP, address: arrays.Address);
            Pointer(zeroPage: PointerQ, address: m_arrays.Values.Min());
            SetCount(count: m_layout.ArrayInitial.Length);
            Call(library: "copy-ram");
        }
        SelectPage(page: 0);
        // NMI on, the background on the first pattern table and sprites on the second; the mask shows both layers, the
        // left column included, once the first frame is published.
        LoadImmediate(value: (0x88 | (m_document.TallSprites ? 0x20 : 0x00)));
        Op(opcode: StaZeroPage, operand: ControlShadow);
        LoadImmediate(value: 0x1E);
        Op(opcode: StaZeroPage, operand: MaskShadow);
        Op(opcode: LdaZeroPage, operand: ControlShadow);
        Abs(opcode: StaAbsolute, address: 0x2000);
    }
    // MMC1: serial reset, then control $0F (horizontal wiring, the last 16 KiB fixed at $C000, 8 KiB CHR) and CHR bank
    // 0. MMC3: the six CHR registers in the 8 KiB layout, horizontal wiring, PRG-RAM enabled and writable, IRQ off.
    private void EmitBoardSetup() {
        switch (m_board) {
            case DeckBoard.Mmc1:
                LoadImmediate(value: 0x80);
                Abs(opcode: StaAbsolute, address: 0x8000);
                Mmc1Write(register: 0x8000, value: 0x0F);
                Mmc1Write(register: 0xA000, value: 0x00);

                break;
            case DeckBoard.Mmc3:
                foreach (var (register, bank) in (ReadOnlySpan<(int, int)>)[(0, 0), (1, 2), (2, 4), (3, 5), (4, 6), (5, 7)]) {
                    LoadImmediate(value: register);
                    Abs(opcode: StaAbsolute, address: 0x8000);
                    LoadImmediate(value: bank);
                    Abs(opcode: StaAbsolute, address: 0x8001);
                }
                LoadImmediate(value: 1);
                Abs(opcode: StaAbsolute, address: 0xA000);
                LoadImmediate(value: 0x80);
                Abs(opcode: StaAbsolute, address: 0xA001);
                Abs(opcode: StaAbsolute, address: 0xE000);

                break;
            default:
                break;
        }
    }
    // Five writes of one bit each, a shift between them so no two writes fall on consecutive cycles.
    private void Mmc1Write(int register, int value) {
        LoadImmediate(value: value);
        for (var bit = 0; (bit < 5); ++bit) {
            Abs(opcode: StaAbsolute, address: register);
            if (bit < 4) {
                Op(opcode: LsrAccumulator);
            }
        }
    }
    private void PpuAddress(int address) {
        Abs(opcode: BitAbsolute, address: 0x2002);
        LoadImmediate(value: (address >> 8));
        Abs(opcode: StaAbsolute, address: 0x2006);
        LoadImmediate(value: (address & 0xFF));
        Abs(opcode: StaAbsolute, address: 0x2006);
    }

    // ---- Frame ---------------------------------------------------------------------------------------------------

    private void EmitFrame() {
        var main = NewLabel();
        var wait = NewLabel();

        Mark(label: main);
        Op(opcode: LdaZeroPage, operand: Frame);
        Mark(label: wait);
        Op(opcode: CmpZeroPage, operand: Frame);
        m_program.Branch(opcode: Beq, label: wait);
        Call(library: "read-pad");
        if (m_document.Sounds.Length != 0) {
            Call(library: "sound-tick");
        }
        if (m_document.Scene is { } scene) {
            Op(opcode: LdaZeroPage, operand: m_variables[scene].Address);
            Op(opcode: StaZeroPage, operand: SceneSnapshot);
        }
        Lowering().Rules(rules: m_document.Rules);
        // Scroll: a vertical scroll past the 240 visible lines wraps to the top.
        var wrapped = NewLabel();

        Load(expression: m_document.ScrollX);
        Op(opcode: StaZeroPage, operand: ScrollXShadow);
        Load(expression: m_document.ScrollY);
        Op(opcode: CmpImmediate, operand: 240);
        m_program.Branch(opcode: Bcc, label: wrapped);
        Op(opcode: SbcImmediate, operand: 240);
        Mark(label: wrapped);
        Op(opcode: StaZeroPage, operand: ScrollYShadow);
        for (var index = 0; (index < m_document.Sprites.Length); ++index) {
            EmitSprite(index: index, sprite: m_document.Sprites[index]);
        }
        LoadImmediate(value: 1);
        Op(opcode: StaZeroPage, operand: Ready);
        JumpTo(label: main);
    }
    // Labels are the program's names, but the shared walker numbers them, so each number maps to one fresh name.
    private CartridgeRuleLowering Lowering() {
        var names = new List<string>();

        return new CartridgeRuleLowering(
            newLabel: () => {
                names.Add(item: NewLabel());

                return (names.Count - 1);
            },
            mark: label => Mark(label: names[label]),
            jump: label => JumpTo(label: names[label]),
            compare: (compare, fail) => Compare(compare: compare, fail: names[fail]),
            assign: Assign,
            step: Step,
            repeatEnter: index => {
                LoadImmediate(value: 0);
                Op(opcode: StaZeroPage, operand: m_variables[index].Address);
            },
            repeatTest: (index, count, exit) => {
                Op(opcode: LdaZeroPage, operand: m_variables[index].Address);
                Op(opcode: CmpImmediate, operand: count);
                Far(branch: Bcs, label: names[exit]);
            },
            repeatAdvance: index => Op(opcode: IncZeroPage, operand: m_variables[index].Address)
        );
    }
    // One OAM entry from its sprite: Y less one (the PPU draws a sprite a line below its Y), tile, attributes, X.
    private void EmitSprite(int index, CartridgeSprite sprite) {
        var slot = (OamShadow + (index * 4));
        var hide = NewLabel();
        var done = NewLabel();

        Load(expression: sprite.Visible);
        Op(opcode: CmpImmediate, operand: 0);
        Far(branch: Beq, label: hide);
        Load(expression: sprite.Tile);
        if (m_document.Tiles.Length < 256) {
            Op(opcode: CmpImmediate, operand: m_document.Tiles.Length);
            Far(branch: Bcs, label: hide);
        }
        Abs(opcode: StaAbsolute, address: (slot + 1));
        Load(expression: sprite.X);
        Abs(opcode: StaAbsolute, address: (slot + 3));
        // Attributes: palette in bits 0-1, behind the background in bit 5, mirrored in bits 6 and 7.
        LoadImmediate(value: 0);
        Op(opcode: StaZeroPage, operand: Attribute);
        if (sprite.Palette is { } palette) {
            Load(expression: palette);
            Op(opcode: AndImmediate, operand: 0x03);
            Op(opcode: OraZeroPage, operand: Attribute);
            Op(opcode: StaZeroPage, operand: Attribute);
        }
        foreach (var (flag, bit) in new[] { (sprite.BehindBackground, 0x20), (sprite.FlipX, 0x40), (sprite.FlipY, 0x80) }) {
            if (flag is null) {
                continue;
            }

            var clear = NewLabel();

            Load(expression: flag);
            Op(opcode: CmpImmediate, operand: 0);
            m_program.Branch(opcode: Beq, label: clear);
            Op(opcode: LdaZeroPage, operand: Attribute);
            Op(opcode: OraImmediate, operand: bit);
            Op(opcode: StaZeroPage, operand: Attribute);
            Mark(label: clear);
        }
        Op(opcode: LdaZeroPage, operand: Attribute);
        Abs(opcode: StaAbsolute, address: (slot + 2));
        Load(expression: sprite.Y);
        Op(opcode: CmpImmediate, operand: 240);
        Far(branch: Bcs, label: hide);
        Op(opcode: Sec);
        Op(opcode: SbcImmediate, operand: 1);
        Abs(opcode: StaAbsolute, address: slot);
        JumpTo(label: done);
        Mark(label: hide);
        LoadImmediate(value: 0xFF);
        Abs(opcode: StaAbsolute, address: slot);
        Mark(label: done);
    }

    // ---- Steps ---------------------------------------------------------------------------------------------------

    // A set step. A wide destination runs through the two-byte registers; validation admits only assignment, add and
    // subtract there. A byte destination combines in the accumulator; an array element is addressed through P first, so
    // the value's own evaluation is free to use X.
    private void Assign(CartridgeStatement statement) {
        var target = statement.Target!;

        if ((target.Key is null) && (m_variables[target.State].Width == 2)) {
            var address = m_variables[target.State].Address;

            LoadWide(expression: statement.Value!, destination: WideRight);
            if (statement.Operation is null) {
                Op(opcode: LdaZeroPage, operand: WideRight);
                Op(opcode: StaZeroPage, operand: address);
                Op(opcode: LdaZeroPage, operand: (WideRight + 1));
                Op(opcode: StaZeroPage, operand: (address + 1));

                return;
            }

            var add = (statement.Operation == ExpressionOp.Add);

            Op(opcode: (add ? Clc : Sec));
            for (var half = 0; (half < 2); ++half) {
                Op(opcode: LdaZeroPage, operand: (address + half));
                Op(opcode: (add ? AdcZeroPage : SbcZeroPage), operand: (WideRight + half));
                Op(opcode: StaZeroPage, operand: (address + half));
            }

            return;
        }
        if (CartridgeExpressions.Index(key: target.Key) is { } index) {
            ElementPointer(array: target.State, index: index);
            Load(expression: statement.Value!);
            if (statement.Operation is { } combining) {
                Op(opcode: StaZeroPage, operand: T0);
                Op(opcode: LdyImmediate, operand: 0);
                Op(opcode: LdaIndirectY, operand: PointerP);
                Combine(operation: combining);
            }
            Op(opcode: LdyImmediate, operand: 0);
            Op(opcode: StaIndirectY, operand: PointerP);

            return;
        }

        var slot = m_variables[target.State].Address;

        if (CombinesInPlace(operation: statement.Operation, slot: slot, value: statement.Value!)) {
            return;
        }

        Load(expression: statement.Value!);
        if (statement.Operation is { } operation) {
            Op(opcode: StaZeroPage, operand: T0);
            Op(opcode: LdaZeroPage, operand: slot);
            Combine(operation: operation);
        }
        Op(opcode: StaZeroPage, operand: slot);
    }
    // A byte slot combined with a literal or another slot needs no spill: add or subtract one is an increment or
    // decrement, and the other bitwise and additive operations take their operand straight from the instruction.
    private bool CombinesInPlace(ExpressionOp? operation, int slot, ExpressionProgram value) {
        if ((operation is not (ExpressionOp.Add or ExpressionOp.Subtract or ExpressionOp.BitAnd or ExpressionOp.BitOr or ExpressionOp.BitXor))
            || (Simple(expression: value, guard: false) is not { } operand)) {
            return false;
        }

        var literal = (operand.Opcode == CmpImmediate);

        if (literal && (operand.Operand == 1) && (operation is (ExpressionOp.Add or ExpressionOp.Subtract))) {
            Op(opcode: ((operation == ExpressionOp.Add) ? IncZeroPage : DecZeroPage), operand: slot);

            return true;
        }

        Op(opcode: LdaZeroPage, operand: slot);
        switch (operation) {
            case ExpressionOp.Add:
                Op(opcode: Clc);
                Op(opcode: (literal ? AdcImmediate : AdcZeroPage), operand: operand.Operand);

                break;
            case ExpressionOp.Subtract:
                Op(opcode: Sec);
                Op(opcode: (literal ? SbcImmediate : SbcZeroPage), operand: operand.Operand);

                break;
            case ExpressionOp.BitAnd:
                Op(opcode: (literal ? AndImmediate : AndZeroPage), operand: operand.Operand);

                break;
            case ExpressionOp.BitOr:
                Op(opcode: (literal ? OraImmediate : OraZeroPage), operand: operand.Operand);

                break;
            default:
                Op(opcode: (literal ? EorImmediate : EorZeroPage), operand: operand.Operand);

                break;
        }
        Op(opcode: StaZeroPage, operand: slot);

        return true;
    }
    // Points P at an array element, or at the discard sink when the index is past the declared length.
    private void ElementPointer(string array, ExpressionProgram index) {
        var length = m_layout.ArrayLengths[array];
        var inside = NewLabel();
        var done = NewLabel();

        Load(expression: index);
        if (length < CartridgeLimits.ArrayLength) {
            Op(opcode: CmpImmediate, operand: length);
            m_program.Branch(opcode: Bcc, label: inside);
            Pointer(zeroPage: PointerP, address: Void);
            JumpTo(label: done);
        }
        Mark(label: inside);
        Op(opcode: Clc);
        Op(opcode: AdcImmediate, operand: (m_arrays[array] & 0xFF));
        Op(opcode: StaZeroPage, operand: PointerP);
        LoadImmediate(value: (m_arrays[array] >> 8));
        Op(opcode: AdcImmediate, operand: 0);
        Op(opcode: StaZeroPage, operand: (PointerP + 1));
        Mark(label: done);
    }
    private void Step(CartridgeStatement statement) {
        switch (statement.Kind) {
            case "map":
                Load(expression: statement.Row!);
                Op(opcode: AndImmediate, operand: 31);
                Op(opcode: StaZeroPage, operand: CellRow);
                Load(expression: statement.Column!);
                Op(opcode: AndImmediate, operand: 31);
                Op(opcode: StaZeroPage, operand: CellColumn);
                Load(expression: statement.Tile!);
                Op(opcode: StaZeroPage, operand: CellTile);
                if (statement.Palette is { } palette) {
                    Load(expression: palette);
                    Op(opcode: AndImmediate, operand: 3);
                } else {
                    LoadImmediate(value: 0);
                }
                Op(opcode: StaZeroPage, operand: CellPalette);
                Call(library: "map-write");

                break;
            case "blit":
                EmitBlit(statement: statement);

                break;
            case "play": {
                    var (music, parts) = m_sounds[statement.Sound!];

                    foreach (var (address, channel) in parts) {
                        LoadImmediate(value: (address & 0xFF));
                        Op(opcode: StaZeroPage, operand: (VoicePointerLow + channel));
                        Op(opcode: StaZeroPage, operand: (VoiceLoopLow + channel));
                        LoadImmediate(value: (address >> 8));
                        Op(opcode: StaZeroPage, operand: (VoicePointerHigh + channel));
                        if (!music) {
                            LoadImmediate(value: 0);
                        }
                        // A loop start of zero is what makes an effect stop at its terminator rather than rewind.
                        Op(opcode: StaZeroPage, operand: (VoiceLoopHigh + channel));
                        LoadImmediate(value: 1);
                        Op(opcode: StaZeroPage, operand: (VoiceWait + channel));
                    }

                    break;
                }
            case "stop":
                foreach (var channel in m_musicChannels) {
                    LoadImmediate(value: 0);
                    Op(opcode: StaZeroPage, operand: (VoicePointerHigh + channel));
                    LoadImmediate(value: DeckSoundCompiler.Silence(channel: channel));
                    Abs(opcode: StaAbsolute, address: (0x4000 + (channel * 4)));
                }

                break;
            case "fade":
                EmitFade(statement: statement);

                break;
            case "save":
                EmitSave();

                break;
            case "load":
                EmitLoad();

                break;
            default:
                // Validation refuses every other kind on hgd (clock, plot, blend); none reaches here.
                throw new ArgumentException(message: $"The hgd target has no '{statement.Kind}' step.", paramName: nameof(statement));
        }
    }
    // A blit repaints with rendering off and drops anything queued, as the Bricks do; NMI publishes nothing while a
    // frame's rules run, so the PPU address is the blit's alone, and the next published frame turns rendering back on.
    private void EmitBlit(CartridgeStatement statement) {
        var screen = m_screens[statement.Screen!];
        var row = CartridgeExpressions.Whole(expression: statement.Row)!.Value;
        var column = CartridgeExpressions.Whole(expression: statement.Column)!.Value;
        var palettes = m_document.Screens.First(predicate: candidate => (candidate.Name == statement.Screen)).Palettes;
        var width = Math.Min(val1: screen.Width, val2: (32 - column));

        LoadImmediate(value: 0);
        Abs(opcode: StaAbsolute, address: 0x2001);
        Op(opcode: StaZeroPage, operand: QueueCount);
        SelectPage(page: screen.Page);
        for (var line = 0; ((line < screen.Height) && ((row + line) < VisibleRows)); ++line) {
            var copy = NewLabel();

            PpuAddress(address: ((0x2000 + ((row + line) * 32)) + column));
            Op(opcode: LdxImmediate, operand: 0);
            Mark(label: copy);
            Abs(opcode: LdaAbsoluteX, address: (screen.Address + (line * screen.Width)));
            Abs(opcode: StaAbsolute, address: 0x2007);
            Op(opcode: Inx);
            Op(opcode: CpxImmediate, operand: width);
            m_program.Branch(opcode: Bne, label: copy);
        }
        SelectPage(page: 0);

        // Each painted cell's quadrant takes its palette (zero without a palette table); the last cell in a quadrant wins.
        var attributes = new SortedDictionary<int, (int Mask, int Bits)>();

        for (var line = 0; ((line < screen.Height) && ((row + line) < VisibleRows)); ++line) {
            for (var cell = 0; (cell < width); ++cell) {
                var (index, shift) = AttributeSlot(row: (row + line), column: (column + cell));
                var palette = ((palettes?[((line * screen.Width) + cell)] ?? 0) & 3);
                var (mask, bits) = attributes.GetValueOrDefault(key: index);

                attributes[index] = ((mask | (3 << shift)), ((bits & ~(3 << shift)) | (palette << shift)));
            }
        }
        foreach (var (index, (mask, bits)) in attributes) {
            Abs(opcode: LdaAbsolute, address: (AttributeShadow + index));
            Op(opcode: AndImmediate, operand: (~mask & 0xFF));
            Op(opcode: OraImmediate, operand: bits);
            Abs(opcode: StaAbsolute, address: (AttributeShadow + index));
        }
        PpuAddress(address: 0x23C0);
        Pointer(zeroPage: PointerP, address: AttributeShadow);
        SetCount(count: 64);
        Call(library: "copy-ppu");
    }
    // A fade picks the baked level for its amount, 0 through 16 in quarters of a level, and marks the palette for the NMI.
    private void EmitFade(CartridgeStatement statement) {
        var clamped = NewLabel();
        var copy = NewLabel();

        Load(expression: statement.Amount!);
        Op(opcode: CmpImmediate, operand: (CartridgeLimits.FadeSteps + 1));
        m_program.Branch(opcode: Bcc, label: clamped);
        LoadImmediate(value: CartridgeLimits.FadeSteps);
        Mark(label: clamped);
        Op(opcode: Clc);
        Op(opcode: AdcImmediate, operand: 2);
        Op(opcode: LsrAccumulator);
        Op(opcode: LsrAccumulator);
        for (var shift = 0; (shift < 5); ++shift) {
            Op(opcode: AslAccumulator);
        }
        Op(opcode: Tay);
        Op(opcode: LdxImmediate, operand: 0);
        Mark(label: copy);
        Abs(opcode: LdaAbsoluteY, address: ((statement.Toward == "white") ? m_fadeWhite : m_fadeBlack));
        Abs(opcode: StaAbsoluteX, address: PaletteShadow);
        Op(opcode: Iny);
        Op(opcode: Inx);
        Op(opcode: CpxImmediate, operand: 32);
        m_program.Branch(opcode: Bne, label: copy);
        LoadImmediate(value: 1);
        Op(opcode: StaZeroPage, operand: PaletteDirty);
    }
    // The battery block at $7C00: 'P', 'K', the payload version, the payload, then its checksum.
    private void EmitSave() {
        var offset = 0;

        foreach (var (address, length) in SaveRuns()) {
            CopyRun(source: address, destination: ((SaveBlock + 3) + offset), length: length);
            offset += length;
        }
        Call(library: "save-seal");
    }
    // A valid block restores the payload; a missing, stale or damaged one restores the authored initial values.
    private void EmitLoad() {
        var invalid = NewLabel();
        var done = NewLabel();
        var offset = 0;

        Call(library: "save-check");
        Far(branch: Bcc, label: invalid);
        foreach (var (address, length) in SaveRuns()) {
            CopyRun(source: ((SaveBlock + 3) + offset), destination: address, length: length);
            offset += length;
        }
        JumpTo(label: done);
        Mark(label: invalid);
        offset = 0;
        foreach (var (address, length) in SaveRuns()) {
            CopyRun(source: (m_saveDefaults + offset), destination: address, length: length);
            offset += length;
        }
        Mark(label: done);
    }
    private IEnumerable<(int Address, int Length)> SaveRuns() =>
        m_layout.SavePayload.Select(selector: run => ((run.IsArray ? m_arrays[run.Name] : m_variables[run.Name].Address), run.Length));
    // Copies a run of at most 256 bytes downward from its last byte.
    private void CopyRun(int source, int destination, int length) {
        var copy = NewLabel();

        Op(opcode: LdxImmediate, operand: (length - 1));
        Mark(label: copy);
        Abs(opcode: LdaAbsoluteX, address: source);
        Abs(opcode: StaAbsoluteX, address: destination);
        Op(opcode: Dex);
        Op(opcode: CpxImmediate, operand: 0xFF);
        m_program.Branch(opcode: Bne, label: copy);
    }

    // ---- Gates and expressions -----------------------------------------------------------------------------------

    // Branches to fail when the comparison misses. CMP leaves carry set when the left operand is at least the right one
    // and zero set when they are equal, which every comparison reads.
    private void Compare(ActionPredicate.CompareValue compare, string fail) {
        if (Wide(expression: compare.Left) || Wide(expression: compare.Right)) {
            CompareWide(compare: compare, fail: fail);

            return;
        }

        // A button against 1 or 0 tests its mask's bit directly: the zero flag is the answer.
        if ((compare.Comparison is (ExpressionOp.Equal or ExpressionOp.NotEqual))
            && (compare.Left.Instructions is [{ Payload: InstructionPayload.State { Key: null } key }])
            && CartridgeExpressions.TryKey(name: key.Name.Spelling, button: out var button, mode: out var mode)
            && (CartridgeExpressions.Whole(expression: compare.Right) is (0 or 1) and var expected)) {
            Op(opcode: LdaZeroPage, operand: Edge(mode: mode));
            Op(opcode: AndImmediate, operand: Key(button: button));
            Far(branch: (((compare.Comparison == ExpressionOp.Equal) == (expected == 1)) ? Beq : Bne), label: fail);

            return;
        }

        Load(expression: compare.Left, guard: true);
        if (Simple(expression: compare.Right, guard: true) is { } right) {
            Op(opcode: right.Opcode, operand: right.Operand);
        } else {
            Op(opcode: Pha);
            Load(expression: compare.Right, guard: true);
            Op(opcode: StaZeroPage, operand: T0);
            Op(opcode: Pla);
            Op(opcode: CmpZeroPage, operand: T0);
        }
        FailUnless(comparison: compare.Comparison, fail: fail);
    }
    private void FailUnless(ExpressionOp comparison, string fail) {
        switch (comparison) {
            case ExpressionOp.Equal:
                Far(branch: Bne, label: fail);

                break;
            case ExpressionOp.NotEqual:
                Far(branch: Beq, label: fail);

                break;
            case ExpressionOp.Less:
                Far(branch: Bcs, label: fail);

                break;
            case ExpressionOp.Greater:
                Far(branch: Beq, label: fail);
                Far(branch: Bcc, label: fail);

                break;
            case ExpressionOp.LessOrEqual: {
                    var holds = NewLabel();

                    m_program.Branch(opcode: Beq, label: holds);
                    Far(branch: Bcs, label: fail);
                    Mark(label: holds);

                    break;
                }
            default:
                Far(branch: Bcc, label: fail);

                break;
        }
    }
    // Sixteen bits: equality a byte at a time, order from the borrow of one subtraction chain, with greater and
    // less-or-equal swapping their operands so every order reads the carry alone.
    private void CompareWide(ActionPredicate.CompareValue compare, string fail) {
        var swap = (compare.Comparison is (ExpressionOp.Greater or ExpressionOp.LessOrEqual));

        LoadWide(expression: (swap ? compare.Right : compare.Left), destination: WideLeft, guard: true);
        LoadWide(expression: (swap ? compare.Left : compare.Right), destination: WideRight, guard: true);
        switch (compare.Comparison) {
            case ExpressionOp.Equal:
                Op(opcode: LdaZeroPage, operand: WideLeft);
                Op(opcode: CmpZeroPage, operand: WideRight);
                Far(branch: Bne, label: fail);
                Op(opcode: LdaZeroPage, operand: (WideLeft + 1));
                Op(opcode: CmpZeroPage, operand: (WideRight + 1));
                Far(branch: Bne, label: fail);

                return;
            case ExpressionOp.NotEqual: {
                    var holds = NewLabel();

                    Op(opcode: LdaZeroPage, operand: WideLeft);
                    Op(opcode: CmpZeroPage, operand: WideRight);
                    m_program.Branch(opcode: Bne, label: holds);
                    Op(opcode: LdaZeroPage, operand: (WideLeft + 1));
                    Op(opcode: CmpZeroPage, operand: (WideRight + 1));
                    Far(branch: Beq, label: fail);
                    Mark(label: holds);

                    return;
                }
            default:
                Op(opcode: LdaZeroPage, operand: WideLeft);
                Op(opcode: CmpZeroPage, operand: WideRight);
                Op(opcode: LdaZeroPage, operand: (WideLeft + 1));
                Op(opcode: SbcZeroPage, operand: (WideRight + 1));
                // Carry set: the first operand is at least the second.
                Far(branch: ((compare.Comparison is (ExpressionOp.Less or ExpressionOp.Greater)) ? Bcs : Bcc), label: fail);

                return;
        }
    }
    // Whether an operand reads two bytes: a bare wide slot, or a bare literal past a byte.
    private bool Wide(ExpressionProgram expression) => expression.Instructions switch {
        [{ Payload: InstructionPayload.State { Key: null } state }] => (m_variables.TryGetValue(key: state.Name.Spelling, value: out var slot) && (slot.Width == 2)),
        [{ Payload: InstructionPayload.Constant constant }] => (constant.Value > CartridgeLimits.NarrowMaximum),
        _ => false,
    };
    // Leaves a two-byte value at the destination: a bare literal or wide slot whole, anything else as a byte widened.
    private void LoadWide(ExpressionProgram expression, byte destination, bool guard = false) {
        switch (expression.Instructions) {
            case [{ Payload: InstructionPayload.Constant constant }]:
                LoadImmediate(value: (((int)constant.Value) & 0xFF));
                Op(opcode: StaZeroPage, operand: destination);
                LoadImmediate(value: (((int)constant.Value) >> 8));
                Op(opcode: StaZeroPage, operand: (destination + 1));

                return;
            case [{ Payload: InstructionPayload.State { Key: null } state }] when (m_variables.TryGetValue(key: state.Name.Spelling, value: out var slot) && (slot.Width == 2)):
                Op(opcode: LdaZeroPage, operand: slot.Address);
                Op(opcode: StaZeroPage, operand: destination);
                Op(opcode: LdaZeroPage, operand: (slot.Address + 1));
                Op(opcode: StaZeroPage, operand: (destination + 1));

                return;
            default:
                Load(expression: expression, guard: guard);
                Op(opcode: StaZeroPage, operand: destination);
                LoadImmediate(value: 0);
                Op(opcode: StaZeroPage, operand: (destination + 1));

                return;
        }
    }
    // The single-instruction comparand a bare operand gives CMP, or null when it needs evaluating.
    private (byte Opcode, int Operand)? Simple(ExpressionProgram expression, bool guard) => expression.Instructions switch {
        [{ Payload: InstructionPayload.Constant constant }] => (CmpImmediate, ((int)constant.Value)),
        [{ Payload: InstructionPayload.State { Key: null } state }] when (guard && (state.Name.Spelling == m_document.Scene)) => (CmpZeroPage, SceneSnapshot),
        [{ Payload: InstructionPayload.State { Key: null } state }] when m_variables.ContainsKey(key: state.Name.Spelling) => (CmpZeroPage, m_variables[state.Name.Spelling].Address),
        _ => null,
    };
    // Leaves a byte expression's value in A. Values in flight wait on the hardware stack; an operator finds its right
    // operand in A and its others on the stack, deepest first.
    private void Load(ExpressionProgram expression, bool guard = false) {
        var depth = 0;

        foreach (var token in expression.Instructions) {
            if (token.Payload is InstructionPayload.Constant or InstructionPayload.State) {
                if (depth > 0) {
                    Op(opcode: Pha);
                }
                Operand(token: token, guard: guard);
                ++depth;

                continue;
            }

            var operation = ExpressionVocabulary.Operation(instruction: token)!.Value;
            var arity = ExpressionVocabulary.Arity(operation: operation);

            switch (arity) {
                case 1:
                    Unary(operation: operation);

                    break;
                case 2:
                    Op(opcode: StaZeroPage, operand: T0);
                    Op(opcode: Pla);
                    Combine(operation: operation);

                    break;
                default:
                    Ternary(operation: operation);

                    break;
            }
            depth -= (arity - 1);
        }
    }
    private void Operand(Instruction token, bool guard) {
        if (token.Payload is InstructionPayload.Constant literal) {
            LoadImmediate(value: (((int)literal.Value) & 0xFF));

            return;
        }

        var state = ((InstructionPayload.State)token.Payload!);

        if (CartridgeExpressions.TryKey(name: state.Name.Spelling, button: out var button, mode: out var mode)) {
            var zero = NewLabel();

            Op(opcode: LdaZeroPage, operand: Edge(mode: mode));
            Op(opcode: AndImmediate, operand: Key(button: button));
            m_program.Branch(opcode: Beq, label: zero);
            LoadImmediate(value: 1);
            Mark(label: zero);

            return;
        }
        if (CartridgeExpressions.Index(key: state.Key?.Spelling) is { } index) {
            var array = state.Name.Spelling;
            var length = m_layout.ArrayLengths[array];
            var inside = NewLabel();
            var done = NewLabel();

            Load(expression: index, guard: guard);
            Op(opcode: Tax);
            if (length < CartridgeLimits.ArrayLength) {
                Op(opcode: CpxImmediate, operand: length);
                m_program.Branch(opcode: Bcc, label: inside);
                LoadImmediate(value: 0);
                m_program.Branch(opcode: Beq, label: done);
            }
            Mark(label: inside);
            Abs(opcode: LdaAbsoluteX, address: m_arrays[array]);
            Mark(label: done);

            return;
        }
        if (guard && (state.Name.Spelling == m_document.Scene)) {
            Op(opcode: LdaZeroPage, operand: SceneSnapshot);

            return;
        }

        Op(opcode: LdaZeroPage, operand: m_variables[state.Name.Spelling].Address);
    }
    // The pad byte a button mode reads: held now, newly down this frame, or newly up.
    private static byte Edge(string mode) => mode switch { "pressed" => Pressed, "released" => Released, _ => Held };
    // The pad byte holds A in bit 7 down to Right in bit 0, the order the controller shifts them out.
    private static int Key(string button) => button switch {
        "a" => 0x80,
        "b" => 0x40,
        "select" => 0x20,
        "start" => 0x10,
        "up" => 0x08,
        "down" => 0x04,
        "left" => 0x02,
        _ => 0x01,
    };
    private void Unary(ExpressionOp operation) {
        switch (operation) {
            case ExpressionOp.BitNot:
                Op(opcode: EorImmediate, operand: 0xFF);

                return;
            case ExpressionOp.Negate:
                Op(opcode: EorImmediate, operand: 0xFF);
                Op(opcode: Clc);
                Op(opcode: AdcImmediate, operand: 1);

                return;
            default: {
                    // Every value is unsigned, so a sign is 0 or 1.
                    var zero = NewLabel();

                    Op(opcode: CmpImmediate, operand: 0);
                    m_program.Branch(opcode: Beq, label: zero);
                    LoadImmediate(value: 1);
                    Mark(label: zero);

                    return;
                }
        }
    }
    // Combines A (the left operand, or a destination's contents) with T0 (the right operand) into A, a byte wide.
    private void Combine(ExpressionOp operation) {
        switch (operation) {
            case ExpressionOp.Add:
                Op(opcode: Clc);
                Op(opcode: AdcZeroPage, operand: T0);

                return;
            case ExpressionOp.Subtract:
                Op(opcode: Sec);
                Op(opcode: SbcZeroPage, operand: T0);

                return;
            case ExpressionOp.BitAnd:
                Op(opcode: AndZeroPage, operand: T0);

                return;
            case ExpressionOp.BitOr:
                Op(opcode: OraZeroPage, operand: T0);

                return;
            case ExpressionOp.BitXor:
                Op(opcode: EorZeroPage, operand: T0);

                return;
            case ExpressionOp.Multiply:
                Call(library: "multiply");

                return;
            case ExpressionOp.Divide:
                Call(library: "divide");

                return;
            case ExpressionOp.Remainder:
                Call(library: "divide");
                Op(opcode: LdaZeroPage, operand: M1);

                return;
            case ExpressionOp.ShiftLeft:
                Call(library: "shift-left");

                return;
            case ExpressionOp.ShiftRight:
                Call(library: "shift-right");

                return;
            case ExpressionOp.Minimum:
            case ExpressionOp.Maximum: {
                    var keep = NewLabel();

                    Op(opcode: CmpZeroPage, operand: T0);
                    m_program.Branch(opcode: ((operation == ExpressionOp.Minimum) ? Bcc : Bcs), label: keep);
                    Op(opcode: LdaZeroPage, operand: T0);
                    Mark(label: keep);

                    return;
                }
            default: {
                    // A comparison as a value: 1 when it holds, 0 otherwise.
                    var zero = NewLabel();
                    var done = NewLabel();

                    Op(opcode: CmpZeroPage, operand: T0);
                    FailUnless(comparison: operation, fail: zero);
                    LoadImmediate(value: 1);
                    m_program.Branch(opcode: Bne, label: done);
                    Mark(label: zero);
                    LoadImmediate(value: 0);
                    Mark(label: done);

                    return;
                }
        }
    }
    // Three operands: the last in A, the first two on the stack.
    private void Ternary(ExpressionOp operation) {
        var otherwise = NewLabel();
        var done = NewLabel();

        Op(opcode: StaZeroPage, operand: T0);
        Op(opcode: Pla);
        Op(opcode: StaZeroPage, operand: T1);
        Op(opcode: Pla);
        if (operation == ExpressionOp.Select) {
            // PLA set Z from the condition.
            m_program.Branch(opcode: Beq, label: otherwise);
            Op(opcode: LdaZeroPage, operand: T1);
            JumpTo(label: done);
            Mark(label: otherwise);
            Op(opcode: LdaZeroPage, operand: T0);
            Mark(label: done);

            return;
        }

        // Clamp: below the floor takes the floor, above the ceiling the ceiling.
        Op(opcode: CmpZeroPage, operand: T1);
        m_program.Branch(opcode: Bcs, label: otherwise);
        Op(opcode: LdaZeroPage, operand: T1);
        Mark(label: otherwise);
        Op(opcode: CmpZeroPage, operand: T0);
        m_program.Branch(opcode: Bcc, label: done);
        m_program.Branch(opcode: Beq, label: done);
        Op(opcode: LdaZeroPage, operand: T0);
        Mark(label: done);
    }

    // ---- NMI -----------------------------------------------------------------------------------------------------

    private void EmitNmi() {
        var skip = NewLabel();
        var palette = NewLabel();
        var paletteCopy = NewLabel();
        var drained = NewLabel();
        var drain = NewLabel();

        Mark(label: "nmi");
        Op(opcode: Pha);
        Op(opcode: Txa);
        Op(opcode: Pha);
        Op(opcode: Tya);
        Op(opcode: Pha);
        Op(opcode: LdaZeroPage, operand: Ready);
        Far(branch: Beq, label: skip);
        // Sprites from page 2.
        LoadImmediate(value: 0);
        Abs(opcode: StaAbsolute, address: 0x2003);
        LoadImmediate(value: (OamShadow >> 8));
        Abs(opcode: StaAbsolute, address: 0x4014);
        // A changed palette.
        Op(opcode: LdaZeroPage, operand: PaletteDirty);
        m_program.Branch(opcode: Beq, label: palette);
        PpuAddress(address: 0x3F00);
        Op(opcode: LdxImmediate, operand: 0);
        Mark(label: paletteCopy);
        Abs(opcode: LdaAbsoluteX, address: PaletteShadow);
        Abs(opcode: StaAbsolute, address: 0x2007);
        Op(opcode: Inx);
        Op(opcode: CpxImmediate, operand: 32);
        m_program.Branch(opcode: Bne, label: paletteCopy);
        LoadImmediate(value: 0);
        Op(opcode: StaZeroPage, operand: PaletteDirty);
        Mark(label: palette);
        // The queued nametable writes, each its own address.
        Abs(opcode: BitAbsolute, address: 0x2002);
        Op(opcode: LdxImmediate, operand: 0);
        Op(opcode: CpxZeroPage, operand: QueueCount);
        m_program.Branch(opcode: Beq, label: drained);
        Mark(label: drain);
        Abs(opcode: LdaAbsoluteX, address: QueueHigh);
        Abs(opcode: StaAbsolute, address: 0x2006);
        Abs(opcode: LdaAbsoluteX, address: QueueLow);
        Abs(opcode: StaAbsolute, address: 0x2006);
        Abs(opcode: LdaAbsoluteX, address: QueueValue);
        Abs(opcode: StaAbsolute, address: 0x2007);
        Op(opcode: Inx);
        Op(opcode: CpxZeroPage, operand: QueueCount);
        m_program.Branch(opcode: Bne, label: drain);
        LoadImmediate(value: 0);
        Op(opcode: StaZeroPage, operand: QueueCount);
        Mark(label: drained);
        // Control, then scroll, then mask: the last scroll writes set the next picture's origin.
        Op(opcode: LdaZeroPage, operand: ControlShadow);
        Abs(opcode: StaAbsolute, address: 0x2000);
        Op(opcode: LdaZeroPage, operand: ScrollXShadow);
        Abs(opcode: StaAbsolute, address: 0x2005);
        Op(opcode: LdaZeroPage, operand: ScrollYShadow);
        Abs(opcode: StaAbsolute, address: 0x2005);
        Op(opcode: LdaZeroPage, operand: MaskShadow);
        Abs(opcode: StaAbsolute, address: 0x2001);
        LoadImmediate(value: 0);
        Op(opcode: StaZeroPage, operand: Ready);
        Mark(label: skip);
        Op(opcode: IncZeroPage, operand: Frame);
        Op(opcode: Pla);
        Op(opcode: Tay);
        Op(opcode: Pla);
        Op(opcode: Tax);
        Op(opcode: Pla);
        Op(opcode: Rti);
    }

    // ---- Libraries -----------------------------------------------------------------------------------------------

    // Emits each library a step called, and the ones those call, once.
    private void EmitLibraries() {
        var emitted = new HashSet<string>(comparer: StringComparer.Ordinal);

        while (m_libraries.Except(second: emitted).Order(comparer: StringComparer.Ordinal).FirstOrDefault() is { } library) {
            emitted.Add(item: library);
            Mark(label: library);
            switch (library) {
                case "read-pad":
                    EmitReadPad();

                    break;
                case "sound-tick":
                    EmitSoundTick();

                    break;
                case "map-write":
                    EmitMapWrite();

                    break;
                case "copy-ram":
                    EmitCopy(toPpu: false);

                    break;
                case "copy-ppu":
                    EmitCopy(toPpu: true);

                    break;
                case "select-page":
                    EmitSelectPage();

                    break;
                case "multiply":
                    EmitMultiply();

                    break;
                case "divide":
                    EmitDivide();

                    break;
                case "shift-left":
                    EmitShift(opcode: AslAccumulator);

                    break;
                case "shift-right":
                    EmitShift(opcode: LsrAccumulator);

                    break;
                case "save-seal":
                    EmitSaveSeal();

                    break;
                default:
                    EmitSaveCheck();

                    break;
            }
        }
    }
    // Strobes port one and shifts its eight buttons into Held, then derives the frame's edges.
    private void EmitReadPad() {
        var bit = NewLabel();

        Op(opcode: LdaZeroPage, operand: Held);
        Op(opcode: StaZeroPage, operand: Previous);
        LoadImmediate(value: 1);
        Abs(opcode: StaAbsolute, address: 0x4016);
        LoadImmediate(value: 0);
        Abs(opcode: StaAbsolute, address: 0x4016);
        Op(opcode: LdxImmediate, operand: 8);
        Mark(label: bit);
        Abs(opcode: LdaAbsolute, address: 0x4016);
        Op(opcode: LsrAccumulator);
        Op(opcode: RolZeroPage, operand: Held);
        Op(opcode: Dex);
        m_program.Branch(opcode: Bne, label: bit);
        Op(opcode: LdaZeroPage, operand: Previous);
        Op(opcode: EorImmediate, operand: 0xFF);
        Op(opcode: AndZeroPage, operand: Held);
        Op(opcode: StaZeroPage, operand: Pressed);
        Op(opcode: LdaZeroPage, operand: Held);
        Op(opcode: EorImmediate, operand: 0xFF);
        Op(opcode: AndZeroPage, operand: Previous);
        Op(opcode: StaZeroPage, operand: Released);
        Op(opcode: Rts);
    }
    // Steps each active voice: when its wait runs out it writes its next step's registers, rewinds a track at its
    // terminator, and stops an effect there with its channel silenced. X walks the voices, Y their register blocks.
    private void EmitSoundTick() {
        var voice = NewLabel();
        var next = NewLabel();
        var fetch = NewLabel();
        var step = NewLabel();
        var stop = NewLabel();
        var held = NewLabel();
        var advanced = NewLabel();
        var silence = NewLabel();

        Op(opcode: LdxImmediate, operand: 3);
        Mark(label: voice);
        Op(opcode: LdaZeroPageX, operand: VoicePointerHigh);
        Far(branch: Beq, label: next);
        Op(opcode: DecZeroPageX, operand: VoiceWait);
        Far(branch: Bne, label: next);
        Op(opcode: LdaZeroPageX, operand: VoicePointerLow);
        Op(opcode: StaZeroPage, operand: SoundPointer);
        Op(opcode: LdaZeroPageX, operand: VoicePointerHigh);
        Op(opcode: StaZeroPage, operand: (SoundPointer + 1));
        Mark(label: fetch);
        Op(opcode: LdyImmediate, operand: 0);
        Op(opcode: LdaIndirectY, operand: SoundPointer);
        m_program.Branch(opcode: Bne, label: step);
        // The terminator: a track rewinds to its loop start, an effect (loop start zero) stops.
        Op(opcode: LdaZeroPageX, operand: VoiceLoopHigh);
        m_program.Branch(opcode: Beq, label: stop);
        Op(opcode: StaZeroPageX, operand: VoicePointerHigh);
        Op(opcode: StaZeroPage, operand: (SoundPointer + 1));
        Op(opcode: LdaZeroPageX, operand: VoiceLoopLow);
        Op(opcode: StaZeroPageX, operand: VoicePointerLow);
        Op(opcode: StaZeroPage, operand: SoundPointer);
        JumpTo(label: fetch);
        Mark(label: step);
        Op(opcode: StaZeroPageX, operand: VoiceWait);
        for (var field = 0; (field < 4); ++field) {
            Op(opcode: Iny);
            Op(opcode: LdaIndirectY, operand: SoundPointer);
            Op(opcode: StaZeroPage, operand: (SoundRow + field));
        }
        Op(opcode: Txa);
        Op(opcode: AslAccumulator);
        Op(opcode: AslAccumulator);
        Op(opcode: Tay);
        Op(opcode: LdaZeroPage, operand: SoundRow);
        Abs(opcode: StaAbsoluteY, address: 0x4000);
        Op(opcode: LdaZeroPage, operand: (SoundRow + 3));
        m_program.Branch(opcode: Beq, label: held);
        Op(opcode: LdaZeroPage, operand: (SoundRow + 1));
        Abs(opcode: StaAbsoluteY, address: 0x4002);
        Op(opcode: LdaZeroPage, operand: (SoundRow + 2));
        Abs(opcode: StaAbsoluteY, address: 0x4003);
        Mark(label: held);
        Op(opcode: Clc);
        Op(opcode: LdaZeroPageX, operand: VoicePointerLow);
        Op(opcode: AdcImmediate, operand: 5);
        Op(opcode: StaZeroPageX, operand: VoicePointerLow);
        m_program.Branch(opcode: Bcc, label: advanced);
        Op(opcode: IncZeroPageX, operand: VoicePointerHigh);
        Mark(label: advanced);
        JumpTo(label: next);
        Mark(label: stop);
        Op(opcode: StaZeroPageX, operand: VoicePointerHigh);
        Op(opcode: Txa);
        Op(opcode: AslAccumulator);
        Op(opcode: AslAccumulator);
        Op(opcode: Tay);
        m_program.Jump(opcode: LdaAbsoluteX, label: silence);
        Abs(opcode: StaAbsoluteY, address: 0x4000);
        Mark(label: next);
        Op(opcode: Dex);
        Far(branch: Bpl, label: voice);
        Op(opcode: Rts);
        Mark(label: silence);
        m_program.Bytes(DeckSoundCompiler.Silence(channel: 0), DeckSoundCompiler.Silence(channel: 1), DeckSoundCompiler.Silence(channel: 2), DeckSoundCompiler.Silence(channel: 3));
    }
    // Queues one cell (row, column, tile, palette in zero page) and, when its quadrant's attribute byte changes, that
    // byte too. Rows 30 and 31 lie under the attribute table and are dropped.
    private void EmitMapWrite() {
        var done = NewLabel();
        var same = NewLabel();
        var masks = NewLabel();
        var fields = NewLabel();

        Op(opcode: LdaZeroPage, operand: CellRow);
        Op(opcode: CmpImmediate, operand: VisibleRows);
        Far(branch: Bcs, label: done);
        Op(opcode: LdxZeroPage, operand: QueueCount);
        // $2000 + row * 32 + column.
        Op(opcode: LsrAccumulator);
        Op(opcode: LsrAccumulator);
        Op(opcode: LsrAccumulator);
        Op(opcode: OraImmediate, operand: 0x20);
        Abs(opcode: StaAbsoluteX, address: QueueHigh);
        Op(opcode: LdaZeroPage, operand: CellRow);
        for (var bit = 0; (bit < 5); ++bit) {
            Op(opcode: AslAccumulator);
        }
        Op(opcode: OraZeroPage, operand: CellColumn);
        Abs(opcode: StaAbsoluteX, address: QueueLow);
        Op(opcode: LdaZeroPage, operand: CellTile);
        Abs(opcode: StaAbsoluteX, address: QueueValue);
        Op(opcode: Inx);
        // The cell's quadrant within its attribute byte, (row & 2) | (column / 2 & 1), picks the field: its kept bits
        // in M0 and the palette moved into it in M2, both from tables.
        Op(opcode: LdaZeroPage, operand: CellColumn);
        Op(opcode: LsrAccumulator);
        Op(opcode: AndImmediate, operand: 1);
        Op(opcode: StaZeroPage, operand: M0);
        Op(opcode: LdaZeroPage, operand: CellRow);
        Op(opcode: AndImmediate, operand: 2);
        Op(opcode: OraZeroPage, operand: M0);
        Op(opcode: Tay);
        m_program.Jump(opcode: LdaAbsoluteY, label: masks);
        Op(opcode: StaZeroPage, operand: M0);
        Op(opcode: Tya);
        Op(opcode: AslAccumulator);
        Op(opcode: AslAccumulator);
        Op(opcode: OraZeroPage, operand: CellPalette);
        Op(opcode: Tay);
        m_program.Jump(opcode: LdaAbsoluteY, label: fields);
        Op(opcode: StaZeroPage, operand: M2);
        // The attribute byte: index (row / 4) * 8 + column / 4.
        Op(opcode: LdaZeroPage, operand: CellRow);
        Op(opcode: LsrAccumulator);
        Op(opcode: LsrAccumulator);
        Op(opcode: AslAccumulator);
        Op(opcode: AslAccumulator);
        Op(opcode: AslAccumulator);
        Op(opcode: StaZeroPage, operand: M1);
        Op(opcode: LdaZeroPage, operand: CellColumn);
        Op(opcode: LsrAccumulator);
        Op(opcode: LsrAccumulator);
        Op(opcode: OraZeroPage, operand: M1);
        Op(opcode: Tay);
        Op(opcode: LdaZeroPage, operand: M0);
        Abs(opcode: AndAbsoluteY, address: AttributeShadow);
        Op(opcode: OraZeroPage, operand: M2);
        Abs(opcode: CmpAbsoluteY, address: AttributeShadow);
        m_program.Branch(opcode: Beq, label: same);
        Abs(opcode: StaAbsoluteY, address: AttributeShadow);
        Abs(opcode: StaAbsoluteX, address: QueueValue);
        LoadImmediate(value: 0x23);
        Abs(opcode: StaAbsoluteX, address: QueueHigh);
        Op(opcode: Tya);
        Op(opcode: OraImmediate, operand: 0xC0);
        Abs(opcode: StaAbsoluteX, address: QueueLow);
        Op(opcode: Inx);
        Mark(label: same);
        Op(opcode: StxZeroPage, operand: QueueCount);
        Mark(label: done);
        Op(opcode: Rts);
        // Quadrants top left, top right, bottom left, bottom right: the bits each keeps, then each palette moved in.
        Mark(label: masks);
        m_program.Bytes(0xFC, 0xF3, 0xCF, 0x3F);
        Mark(label: fields);
        for (var quadrant = 0; (quadrant < 4); ++quadrant) {
            for (var palette = 0; (palette < 4); ++palette) {
                m_program.Bytes(((byte)(palette << (quadrant * 2))));
            }
        }
    }
    // Copies Count bytes from P to Q, or to the PPU's data port: whole 256-byte pages, then the remainder.
    private void EmitCopy(bool toPpu) {
        var tail = NewLabel();
        var page = NewLabel();
        var rest = NewLabel();
        var done = NewLabel();

        void Store() {
            if (toPpu) {
                Abs(opcode: StaAbsolute, address: 0x2007);
            } else {
                Op(opcode: StaIndirectY, operand: PointerQ);
            }
        }

        Op(opcode: LdyImmediate, operand: 0);
        Op(opcode: LdxZeroPage, operand: (Count + 1));
        m_program.Branch(opcode: Beq, label: tail);
        Mark(label: page);
        Op(opcode: LdaIndirectY, operand: PointerP);
        Store();
        Op(opcode: Iny);
        m_program.Branch(opcode: Bne, label: page);
        Op(opcode: IncZeroPage, operand: (PointerP + 1));
        if (!toPpu) {
            Op(opcode: IncZeroPage, operand: (PointerQ + 1));
        }
        Op(opcode: Dex);
        m_program.Branch(opcode: Bne, label: page);
        Mark(label: tail);
        Op(opcode: LdxZeroPage, operand: Count);
        m_program.Branch(opcode: Beq, label: done);
        Mark(label: rest);
        Op(opcode: LdaIndirectY, operand: PointerP);
        Store();
        Op(opcode: Iny);
        Op(opcode: Dex);
        m_program.Branch(opcode: Bne, label: rest);
        Mark(label: done);
        Op(opcode: Rts);
    }
    // Maps data page A at $8000: one 16 KiB bank on MMC1, two consecutive 8 KiB banks (R6, R7) on MMC3.
    private void EmitSelectPage() {
        if (m_board == DeckBoard.Mmc1) {
            // Bit 4 clear keeps PRG-RAM enabled.
            Op(opcode: AndImmediate, operand: 0x0F);
            for (var bit = 0; (bit < 5); ++bit) {
                Abs(opcode: StaAbsolute, address: 0xE000);
                if (bit < 4) {
                    Op(opcode: LsrAccumulator);
                }
            }
        } else {
            Op(opcode: AslAccumulator);
            Op(opcode: LdxImmediate, operand: 6);
            Abs(opcode: StxAbsolute, address: 0x8000);
            Abs(opcode: StaAbsolute, address: 0x8001);
            Op(opcode: LdxImmediate, operand: 7);
            Abs(opcode: StxAbsolute, address: 0x8000);
            Op(opcode: OraImmediate, operand: 1);
            Abs(opcode: StaAbsolute, address: 0x8001);
        }
        Op(opcode: Rts);
    }
    // A times T0, the low byte, by shifting the multiplier out from its top bit.
    private void EmitMultiply() {
        var loop = NewLabel();
        var skip = NewLabel();

        Op(opcode: StaZeroPage, operand: M0);
        LoadImmediate(value: 0);
        Op(opcode: LdxImmediate, operand: 8);
        Mark(label: loop);
        Op(opcode: AslAccumulator);
        Op(opcode: AslZeroPage, operand: M0);
        m_program.Branch(opcode: Bcc, label: skip);
        Op(opcode: Clc);
        Op(opcode: AdcZeroPage, operand: T0);
        Mark(label: skip);
        Op(opcode: Dex);
        m_program.Branch(opcode: Bne, label: loop);
        Op(opcode: Rts);
    }
    // A divided by T0: the quotient in A, the remainder in M1, both zero for a zero divisor.
    private void EmitDivide() {
        var go = NewLabel();
        var loop = NewLabel();
        var skip = NewLabel();

        Op(opcode: LdxZeroPage, operand: T0);
        m_program.Branch(opcode: Bne, label: go);
        LoadImmediate(value: 0);
        Op(opcode: StaZeroPage, operand: M1);
        Op(opcode: Rts);
        Mark(label: go);
        Op(opcode: StaZeroPage, operand: M0);
        LoadImmediate(value: 0);
        Op(opcode: LdxImmediate, operand: 8);
        Mark(label: loop);
        Op(opcode: AslZeroPage, operand: M0);
        Op(opcode: RolAccumulator);
        Op(opcode: CmpZeroPage, operand: T0);
        m_program.Branch(opcode: Bcc, label: skip);
        Op(opcode: SbcZeroPage, operand: T0);
        Op(opcode: IncZeroPage, operand: M0);
        Mark(label: skip);
        Op(opcode: Dex);
        m_program.Branch(opcode: Bne, label: loop);
        Op(opcode: StaZeroPage, operand: M1);
        Op(opcode: LdaZeroPage, operand: M0);
        Op(opcode: Rts);
    }
    // A shifted by T0 places; eight or more leaves zero.
    private void EmitShift(byte opcode) {
        var loop = NewLabel();
        var done = NewLabel();
        var zero = NewLabel();

        Op(opcode: LdxZeroPage, operand: T0);
        Op(opcode: CpxImmediate, operand: 8);
        m_program.Branch(opcode: Bcs, label: zero);
        Op(opcode: CpxImmediate, operand: 0);
        m_program.Branch(opcode: Beq, label: done);
        Mark(label: loop);
        Op(opcode: opcode);
        Op(opcode: Dex);
        m_program.Branch(opcode: Bne, label: loop);
        Mark(label: done);
        Op(opcode: Rts);
        Mark(label: zero);
        LoadImmediate(value: 0);
        Op(opcode: Rts);
    }
    private void EmitSaveSeal() {
        EmitSum();
        Abs(opcode: StaAbsolute, address: ((SaveBlock + 3) + m_layout.SaveDefaults.Length));
        LoadImmediate(value: 'P');
        Abs(opcode: StaAbsolute, address: SaveBlock);
        LoadImmediate(value: 'K');
        Abs(opcode: StaAbsolute, address: (SaveBlock + 1));
        LoadImmediate(value: m_document.Save!.Version);
        Abs(opcode: StaAbsolute, address: (SaveBlock + 2));
        Op(opcode: Rts);
    }
    // Carry set when the block's magic, version and checksum all hold.
    private void EmitSaveCheck() {
        var bad = NewLabel();

        Abs(opcode: LdaAbsolute, address: SaveBlock);
        Op(opcode: CmpImmediate, operand: 'P');
        m_program.Branch(opcode: Bne, label: bad);
        Abs(opcode: LdaAbsolute, address: (SaveBlock + 1));
        Op(opcode: CmpImmediate, operand: 'K');
        m_program.Branch(opcode: Bne, label: bad);
        Abs(opcode: LdaAbsolute, address: (SaveBlock + 2));
        Op(opcode: CmpImmediate, operand: m_document.Save!.Version);
        m_program.Branch(opcode: Bne, label: bad);
        EmitSum();
        Abs(opcode: CmpAbsoluteX, address: (SaveBlock + 3));
        m_program.Branch(opcode: Bne, label: bad);
        Op(opcode: Sec);
        Op(opcode: Rts);
        Mark(label: bad);
        Op(opcode: Clc);
        Op(opcode: Rts);
    }
    // Leaves the checksum in A, the byte sum of the version and payload folded by an odd pattern so a zeroed block
    // fails, and the payload length in X, so the stored checksum reads at SaveBlock + 3 + X.
    private void EmitSum() {
        var loop = NewLabel();
        var length = m_layout.SaveDefaults.Length;

        LoadImmediate(value: m_document.Save!.Version);
        Op(opcode: LdxImmediate, operand: 0);
        Mark(label: loop);
        Op(opcode: Clc);
        Abs(opcode: AdcAbsoluteX, address: (SaveBlock + 3));
        Op(opcode: Inx);
        Op(opcode: CpxImmediate, operand: length);
        m_program.Branch(opcode: Bne, label: loop);
        Op(opcode: EorImmediate, operand: 0x5A);
    }

    // ---- Packing -------------------------------------------------------------------------------------------------

    private byte[] Pack() {
        // The tile bank in both pattern tables.
        var tiles = CartridgeGraphics.Tiles(document: m_document);
        var chr = new byte[8192];

        tiles.CopyTo(array: chr, index: 0);
        tiles.CopyTo(array: chr, index: 4096);

        var banks = Math.Max(val1: 2, val2: (int)System.Numerics.BitOperations.RoundUpToPowerOf2(value: ((uint)(m_pages.Count + 1))));
        byte[] image;

        try {
            image = (m_board switch {
                DeckBoard.Nrom => m_program.Build(chr: chr, horizontal: true),
                DeckBoard.Mmc1 => m_program.Build(chr: chr, mapper: 1, prgRomSize: (banks * PageSize), battery: (m_document.Save is not null), horizontal: true),
                _ => m_program.Build(chr: chr, mapper: 4, prgRomSize: (banks * PageSize), battery: (m_document.Save is not null), horizontal: true),
            });
        } catch (ArgumentException overrun) {
            throw new CartridgeCapacityException(message: $"The document's code does not fit the 16 KiB the {m_board} board keeps at $C000: {overrun.Message}", innerException: overrun);
        }
        for (var page = 0; (page < m_pages.Count); ++page) {
            m_pages[page].ToArray().CopyTo(array: image, index: (16 + (page * PageSize)));
        }

        return image;
    }
}
