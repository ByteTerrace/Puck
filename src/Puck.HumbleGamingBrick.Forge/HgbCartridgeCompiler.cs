using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.HumbleGamingBrick.Forge;

/// <summary>Compiles cartridge documents into native CGB machine code and graphics.</summary>
public sealed class HgbCartridgeCompiler : ICartridgeCompiler {
    /// <inheritdoc />
    public string Target => "cgb";

    /// <inheritdoc />
    public CartridgeCompilation Compile(CartridgeDocument document) {
        var source = CartridgeDocuments.Canonicalize(document: document);
        document = source.Document;
        if (document.Target != Target) {
            throw new ArgumentException(message: "This compiler requires target cgb.", paramName: nameof(document));
        }

        var variables = document.Variables.Select(selector: (v, i) => (v.Name, Address: (uint)(0xC200 + i))).ToDictionary(keySelector: v => v.Name, elementSelector: v => v.Address, comparer: StringComparer.Ordinal);
        var emitter = new Sm83Emitter();
        var data = new RomDataBuilder(text: new TextModule(emitter: emitter, bg: new BgModule(emitter: emitter), fontTileBase: 0));
        var input = new InputModule(emitter: emitter);
        var palette = data.Add(name: "palette", bytes: CartridgeGraphics.Halfwords(values: document.Palette));
        var tiles = data.Add(name: "tiles", bytes: CartridgeGraphics.Tiles(document: document));
        var map = data.Add(name: "map", bytes: document.Map.Select(selector: static tile => (byte)tile).ToArray());
        var trampoline = data.Add(name: "dma", bytes: FrameworkKernel.BuildDmaTrampolineBlob());
        var boot = emitter.NewLabel();
        var loop = emitter.NewLabel();
        var spec = new FrameworkBootSpec(BgPalettes: palette, ObjPalettes: palette, Tiles: tiles, TileByteCount: tiles.Length, InitialMap: map, Lcdc: 0x93, InitialState: 0);
        FrameworkKernel.EmitPrologue(emitter: emitter, bootLabel: boot);
        emitter.MarkLabel(label: boot);
        FrameworkKernel.EmitBootPrologue(emitter: emitter, spec: spec, dmaTrampoline: trampoline);
        Initialize();
        FrameworkKernel.EmitBootEpilogue(emitter: emitter, spec: spec);
        emitter.MarkLabel(label: loop);
        FrameworkKernel.EmitHaltWait(emitter: emitter);
        emitter.LoadAFromAddress(address: FrameworkMemoryMap.InputHeld);
        emitter.StoreAToAddress(address: 0xC240);
        input.EmitTick();
        Frame();
        emitter.JumpAbsolute(label: loop);
        input.EmitLibrary();
        var rom = FrameworkCartridge.Build(title: document.Title, routine: emitter.ToArray(baseAddress: Hw.EntryAddress), data: data.ToArray());
        return new CartridgeCompilation(Rom: rom, SourceHash: source.Hash, Target: Target, Variables: variables);

        void Initialize() {
            foreach (var variable in document.Variables) {
                emitter.LoadAImmediate(value: (byte)variable.Initial);
                emitter.StoreAToAddress(address: (ushort)variables[variable.Name]);
            }
        }
        void Frame() {
            foreach (var rule in document.Rules) {
                var end = emitter.NewLabel();
                foreach (var condition in rule.When) {
                    if (condition.Kind == "key") {
                        emitter.LoadAFromAddress(address: condition.Mode == "pressed" ? FrameworkMemoryMap.InputPressed : FrameworkMemoryMap.InputHeld);
                        if (condition.Mode == "released") {
                            emitter.ComplementA();
                            emitter.Load(destination: Reg8.B, source: Reg8.A);
                            emitter.LoadAFromAddress(address: 0xC240);
                            emitter.Arithmetic(op: AluOp.And, source: Reg8.B);
                        }
                        emitter.ArithmeticImmediate(op: AluOp.And, value: Key(key: condition.Key!));
                        emitter.JumpAbsolute(condition: Condition.Zero, label: end);
                    } else {
                        // Reverse > and <= so all comparisons use carry/zero without signed arithmetic.
                        var reverse = condition.Comparison is "gt" or "le";
                        Load(value: reverse ? condition.Left! : condition.Right!);
                        emitter.Load(destination: Reg8.B, source: Reg8.A);
                        Load(value: reverse ? condition.Right! : condition.Left!);
                        emitter.Arithmetic(op: AluOp.Compare, source: Reg8.B);
                        emitter.JumpAbsolute(condition: condition.Comparison switch {
                            "eq" => Condition.NotZero, "ne" => Condition.Zero,
                            "lt" or "gt" => Condition.NoCarry, _ => Condition.Carry,
                        }, label: end);
                    }
                }
                foreach (var action in rule.Actions) {
                    Load(value: action.Value);
                    if (action.Operation != "set") {
                        emitter.Load(destination: Reg8.B, source: Reg8.A);
                        emitter.LoadAFromAddress(address: (ushort)variables[action.Variable]);
                        emitter.Arithmetic(op: action.Operation switch {
                            "add" => AluOp.Add, "subtract" => AluOp.Subtract, "and" => AluOp.And, "or" => AluOp.Or, _ => AluOp.Xor,
                        }, source: Reg8.B);
                    }
                    emitter.StoreAToAddress(address: (ushort)variables[action.Variable]);
                }
                emitter.MarkLabel(label: end);
            }
            Load(value: document.ScrollX); emitter.StoreAToAddress(address: 0xFF43);
            Load(value: document.ScrollY); emitter.StoreAToAddress(address: 0xFF42);
            for (var i = 0; i < document.Sprites.Length; ++i) {
                var sprite = document.Sprites[i];
                var hide = emitter.NewLabel(); var done = emitter.NewLabel();
                Load(value: sprite.Visible); emitter.ArithmeticImmediate(op: AluOp.Compare, value: 0);
                emitter.JumpAbsolute(condition: Condition.Zero, label: hide);
                Load(value: sprite.Tile);
                if (document.Tiles.Length < 256) { emitter.ArithmeticImmediate(op: AluOp.Compare, value: (byte)document.Tiles.Length); emitter.JumpAbsolute(condition: Condition.NoCarry, label: hide); }
                emitter.StoreAToAddress(address: (ushort)(0xC102 + i * 4));
                Load(value: sprite.X); emitter.ArithmeticImmediate(op: AluOp.Compare, value: 160); emitter.JumpAbsolute(condition: Condition.NoCarry, label: hide);
                emitter.ArithmeticImmediate(op: AluOp.Add, value: 8); emitter.StoreAToAddress(address: (ushort)(0xC101 + i * 4));
                Load(value: sprite.Y); emitter.ArithmeticImmediate(op: AluOp.Compare, value: 144); emitter.JumpAbsolute(condition: Condition.NoCarry, label: hide);
                emitter.ArithmeticImmediate(op: AluOp.Add, value: 16); emitter.StoreAToAddress(address: (ushort)(0xC100 + i * 4));
                emitter.JumpAbsolute(label: done);
                emitter.MarkLabel(label: hide); emitter.XorA(); emitter.StoreAToAddress(address: (ushort)(0xC100 + i * 4));
                emitter.MarkLabel(label: done);
            }
        }

        void Load(CartridgeValue value) {
            if (value.Constant is { } constant) {
                emitter.LoadAImmediate(value: (byte)constant);
            } else {
                emitter.LoadAFromAddress(address: (ushort)variables[value.Variable!]);
            }
        }
    }

    private static byte Key(string key) => key switch {
        "right" => 1, "left" => 2, "up" => 4, "down" => 8, "a" => 16, "b" => 32, "select" => 64, "start" => 128,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(key)),
    };
}
