using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>Compiles cartridge documents into BIOS-independent Thumb code and mode-0 graphics.</summary>
public sealed class AgbCartridgeCompiler : ICartridgeCompiler {
    /// <inheritdoc />
    public string Target => "agb";

    /// <inheritdoc />
    public CartridgeCompilation Compile(CartridgeDocument document) {
        var source = CartridgeDocuments.Canonicalize(document: document);
        document = source.Document;
        if (document.Target != Target) {
            throw new ArgumentException(message: "This compiler requires target agb.", paramName: nameof(document));
        }

        var variables = document.Variables.Select(selector: (v, i) => (v.Name, Address: (uint)(0x02000040 + i))).ToDictionary(keySelector: v => v.Name, elementSelector: v => v.Address, comparer: StringComparer.Ordinal);
        var emitter = new ThumbEmitter();
        var kernel = new AgbForgeKernel(emitter: emitter);
        var data = new List<byte>();
        kernel.EmitBootPrologue();
        StoreHalf(address: 0x04000000, value: 0x80); // Forced blank while copying video memory.
        var palette = Add(bytes: CartridgeGraphics.Halfwords(values: document.Palette));
        Copy(sourceAddress: palette, destination: 0x05000000, count: 32);
        Copy(sourceAddress: palette, destination: 0x05000200, count: 32);
        var tileBytes = CartridgeGraphics.Tiles(document: document);
        var tiles = Add(bytes: tileBytes);
        Copy(sourceAddress: tiles, destination: 0x06000000, count: tileBytes.Length);
        Copy(sourceAddress: tiles, destination: 0x06010000, count: tileBytes.Length);
        Copy(sourceAddress: Add(bytes: CartridgeGraphics.Halfwords(values: document.Map)), destination: 0x0600F800, count: 2048);
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
            StoreVariable(name: variable.Name);
        }
        StoreHalf(address: 0x04000008, value: 31 << 8);
        StoreHalf(address: 0x04000000, value: 0x1140);
        Flush();
        var loop = emitter.NewLabel(); emitter.MarkLabel(label: loop);
        kernel.EmitFrameSyncCall();
        foreach (var rule in document.Rules) {
            var end = emitter.NewLabel();
            foreach (var condition in rule.When) {
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
                    Require(condition: ThumbCondition.NotEqual, end: end);
                } else {
                    Load(value: condition.Left!, register: LowRegister.R0);
                    Load(value: condition.Right!, register: LowRegister.R1);
                    emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R0, source: LowRegister.R1);
                    Require(condition: condition.Comparison switch {
                        "eq" => ThumbCondition.Equal, "ne" => ThumbCondition.NotEqual, "lt" => ThumbCondition.CarryClear,
                        "le" => ThumbCondition.UnsignedLowerOrSame, "gt" => ThumbCondition.UnsignedHigher, _ => ThumbCondition.CarrySet,
                    }, end: end);
                }
            }
            foreach (var action in rule.Actions) {
                Load(value: action.Value, register: LowRegister.R1);
                if (action.Operation == "set") {
                    emitter.MoveRegister(destination: LowRegister.R0, source: LowRegister.R1);
                } else {
                    Load(value: new CartridgeValue(Variable: action.Variable), register: LowRegister.R0);
                    switch (action.Operation) {
                        case "add": emitter.AddRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R1); break;
                        case "subtract": emitter.SubtractRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R1); break;
                        default: emitter.Alu(op: action.Operation switch { "and" => ThumbAlu.And, "or" => ThumbAlu.Or, _ => ThumbAlu.ExclusiveOr }, destination: LowRegister.R0, source: LowRegister.R1); break;
                    }
                }
                StoreVariable(name: action.Variable);
            }
            emitter.MarkLabel(label: end);
            Flush();
        }
        Load(value: document.ScrollX, register: LowRegister.R0); StoreResult(address: 0x04000010);
        Load(value: document.ScrollY, register: LowRegister.R0); StoreResult(address: 0x04000012);
        for (var i = 0; i < document.Sprites.Length; ++i) {
            var sprite = document.Sprites[i];
            var hide = emitter.NewLabel(); var done = emitter.NewLabel();
            Load(value: sprite.Visible, register: LowRegister.R0);
            emitter.CompareImmediate(register: LowRegister.R0, value: 0); Require(condition: ThumbCondition.NotEqual, end: hide);
            Load(value: sprite.Tile, register: LowRegister.R0);
            if (document.Tiles.Length < 256) { emitter.CompareImmediate(register: LowRegister.R0, value: (byte)document.Tiles.Length); Require(condition: ThumbCondition.CarryClear, end: hide); }
            StoreResult(address: (uint)(0x07000004 + i * 8));
            Load(value: sprite.X, register: LowRegister.R0); emitter.CompareImmediate(register: LowRegister.R0, value: 240); Require(condition: ThumbCondition.CarryClear, end: hide);
            StoreResult(address: (uint)(0x07000002 + i * 8));
            Load(value: sprite.Y, register: LowRegister.R0); emitter.CompareImmediate(register: LowRegister.R0, value: 160); Require(condition: ThumbCondition.CarryClear, end: hide);
            StoreResult(address: (uint)(0x07000000 + i * 8));
            emitter.Branch(label: done);
            emitter.MarkLabel(label: hide); StoreHalf(address: (uint)(0x07000000 + i * 8), value: 0x0200);
            emitter.MarkLabel(label: done); Flush();
        }
        // BL supplies the Thumb-1 long jump; this loop never returns or consumes stack.
        emitter.Call(label: loop);
        kernel.EmitLibrary();
        var rom = AgbForgeCartridge.Build(title: document.Title, gameCode: document.GameCode, routine: emitter.ToArray(baseAddress: AgbForgeCartridge.CodeAddress), data: data.ToArray());
        return new CartridgeCompilation(Rom: rom, SourceHash: source.Hash, Target: Target, Variables: variables);

        void Flush() {
            var resume = emitter.NewLabel();
            emitter.Branch(label: resume);
            emitter.EmitLiteralPool();
            emitter.MarkLabel(label: resume);
        }
        uint Add(byte[] bytes) { var address = AgbForgeCartridge.DataAddress + (uint)data.Count; data.AddRange(collection: bytes); return address; }
        void Load(CartridgeValue value, LowRegister register) {
            if (value.Constant is { } constant) {
                emitter.MoveImmediate(destination: register, value: (byte)constant);
            } else { emitter.LoadConstant(destination: LowRegister.R2, value: variables[value.Variable!]); emitter.LoadByte(baseRegister: LowRegister.R2, byteOffset: 0, destination: register); }
        }
        void StoreVariable(string name) {
            emitter.LoadConstant(destination: LowRegister.R2, value: variables[name]);
            emitter.StoreByte(baseRegister: LowRegister.R2, byteOffset: 0, source: LowRegister.R0);
        }
        void StoreResult(uint address) {
            emitter.LoadConstant(destination: LowRegister.R2, value: address);
            emitter.StoreHalf(baseRegister: LowRegister.R2, byteOffset: 0, source: LowRegister.R0);
        }
        void StoreHalf(uint address, uint value) { emitter.LoadConstant(destination: LowRegister.R0, value: value); StoreResult(address: address); }
        void Require(ThumbCondition condition, int end) {
            var accepted = emitter.NewLabel();
            emitter.Branch(condition: condition, label: accepted);
            emitter.Branch(label: end);
            emitter.MarkLabel(label: accepted);
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
        "a" => 1, "b" => 2, "select" => 4, "start" => 8, "right" => 16, "left" => 32, "up" => 64, "down" => 128,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(key)),
    };
}
