using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers addressable array state and the arithmetic neither instruction set supplies directly.</summary>
public sealed class CartridgeMemoryTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void ArraysInitializeAndIndexAtRuntime(string target) {
        var document = Blank(target: target, title: "ARRAYS") with {
            Variables = [
                new CartridgeVariable(Name: "cursor", Initial: 3),
                new CartridgeVariable(Name: "read", Initial: 0),
                new CartridgeVariable(Name: "indirect", Initial: 0),
                new CartridgeVariable(Name: "done", Initial: 0),
            ],
            Arrays = [
                new CartridgeArray(Name: "table", Initial: [10, 20, 30, 40, 50]),
                new CartridgeArray(Name: "pointers", Initial: [4, 3, 2, 1, 0]),
            ],
            Rules = [Once(name: "probe", actions: [
                // read = table[cursor]
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "read"), Operation: "set", Value: Element(array: "table", index: new CartridgeValue(Variable: "cursor"))),
                // indirect = table[pointers[cursor]]
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "indirect"), Operation: "set", Value: Element(array: "table", index: Element(array: "pointers", index: new CartridgeValue(Variable: "cursor")))),
                // table[0] = table[0] + 5
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "table", Index: new CartridgeValue(Constant: 0)), Operation: "add", Value: new CartridgeValue(Constant: 5)),
                // table[cursor] = 99
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "table", Index: new CartridgeValue(Variable: "cursor")), Operation: "set", Value: new CartridgeValue(Constant: 99)),
            ])],
        };
        using var machine = Run(document: document, frames: 12, out var result);
        Assert.Equal(expected: 40, actual: machine.Read(address: result.Variables["read"]));
        Assert.Equal(expected: 20, actual: machine.Read(address: result.Variables["indirect"]));
        Assert.Equal(expected: 15, actual: machine.Read(address: result.Arrays["table"]));
        Assert.Equal(expected: 99, actual: machine.Read(address: result.Arrays["table"] + 3));
        Assert.Equal(expected: 50, actual: machine.Read(address: result.Arrays["table"] + 4));
        Assert.Equal(expected: 4, actual: machine.Read(address: result.Arrays["pointers"]));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void OutOfRangeIndexReadsZeroAndDiscardsTheWrite(string target) {
        var document = Blank(target: target, title: "BOUNDS") with {
            Variables = [
                new CartridgeVariable(Name: "past", Initial: 9),
                new CartridgeVariable(Name: "read", Initial: 77),
                new CartridgeVariable(Name: "done", Initial: 0),
            ],
            Arrays = [new CartridgeArray(Name: "table", Initial: [1, 2, 3])],
            Rules = [Once(name: "probe", actions: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "read"), Operation: "set", Value: Element(array: "table", index: new CartridgeValue(Variable: "past"))),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "table", Index: new CartridgeValue(Variable: "past")), Operation: "set", Value: new CartridgeValue(Constant: 200)),
            ])],
        };
        using var machine = Run(document: document, frames: 12, out var result);
        Assert.Equal(expected: 0, actual: machine.Read(address: result.Variables["read"]));
        Assert.Equal(expected: 1, actual: machine.Read(address: result.Arrays["table"]));
        Assert.Equal(expected: 2, actual: machine.Read(address: result.Arrays["table"] + 1));
        Assert.Equal(expected: 3, actual: machine.Read(address: result.Arrays["table"] + 2));
    }

    [Theory]
    [InlineData("mul", 13, 11, 143)]
    [InlineData("mul", 16, 16, 0)]
    [InlineData("mul", 200, 3, 88)]
    [InlineData("mul", 7, 0, 0)]
    [InlineData("mul", 0, 7, 0)]
    [InlineData("div", 143, 11, 13)]
    [InlineData("div", 255, 1, 255)]
    [InlineData("div", 7, 2, 3)]
    [InlineData("div", 3, 7, 0)]
    [InlineData("div", 100, 0, 0)]
    [InlineData("mod", 143, 11, 0)]
    [InlineData("mod", 7, 2, 1)]
    [InlineData("mod", 3, 7, 3)]
    [InlineData("mod", 255, 16, 15)]
    [InlineData("mod", 100, 0, 0)]
    [InlineData("shl", 5, 3, 40)]
    [InlineData("shl", 255, 1, 254)]
    [InlineData("shl", 1, 7, 128)]
    [InlineData("shl", 3, 0, 3)]
    [InlineData("shr", 40, 3, 5)]
    [InlineData("shr", 255, 7, 1)]
    [InlineData("shr", 1, 1, 0)]
    [InlineData("shr", 3, 0, 3)]
    public void ExtendedArithmeticAgreesAcrossTargets(string operation, int left, int right, int expected) {
        foreach (var target in new[] { "cgb", "agb" }) {
            var document = Blank(target: target, title: "MATH") with {
                Variables = [
                    new CartridgeVariable(Name: "value", Initial: left),
                    new CartridgeVariable(Name: "operand", Initial: right),
                    new CartridgeVariable(Name: "done", Initial: 0),
                ],
                Rules = [Once(name: "apply", actions: [
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "value"), Operation: operation, Value: new CartridgeValue(Variable: "operand")),
                ])],
            };
            using var machine = Run(document: document, frames: 12, out var result);
            Assert.Equal(expected: expected, actual: machine.Read(address: result.Variables["value"]));
        }
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void ArithmeticWritesThroughAnIndexedDestination(string target) {
        var document = Blank(target: target, title: "INDEXED") with {
            Variables = [
                new CartridgeVariable(Name: "slot", Initial: 2),
                new CartridgeVariable(Name: "done", Initial: 0),
            ],
            Arrays = [new CartridgeArray(Name: "cells", Initial: [6, 6, 6, 6])],
            Rules = [Once(name: "apply", actions: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "cells", Index: new CartridgeValue(Variable: "slot")), Operation: "mul", Value: new CartridgeValue(Constant: 7)),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "cells", Index: new CartridgeValue(Constant: 1)), Operation: "div", Value: new CartridgeValue(Constant: 3)),
            ])],
        };
        using var machine = Run(document: document, frames: 12, out var result);
        Assert.Equal(expected: 6, actual: machine.Read(address: result.Arrays["cells"]));
        Assert.Equal(expected: 2, actual: machine.Read(address: result.Arrays["cells"] + 1));
        Assert.Equal(expected: 42, actual: machine.Read(address: result.Arrays["cells"] + 2));
        Assert.Equal(expected: 6, actual: machine.Read(address: result.Arrays["cells"] + 3));
    }

    [Fact]
    public void ValidationRefusesUnsoundMemoryAndArithmetic() {
        var document = Blank(target: "cgb", title: "REFUSE") with {
            Variables = [new CartridgeVariable(Name: "x", Initial: 0)],
            Arrays = [new CartridgeArray(Name: "table", Initial: [1, 2, 3])],
        };
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "missing", Index: new CartridgeValue(Constant: 0)), Operation: "set", Value: new CartridgeValue(Constant: 1))])] }, fragment: "Unknown array");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "table", Index: new CartridgeValue(Constant: 5)), Operation: "set", Value: new CartridgeValue(Constant: 1))])] }, fragment: "outside array");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "table"), Operation: "set", Value: new CartridgeValue(Constant: 1))])] }, fragment: "requires an index");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "x", Index: new CartridgeValue(Constant: 0)), Operation: "set", Value: new CartridgeValue(Constant: 1))])] }, fragment: "cannot carry an index");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "x"), Operation: "div", Value: new CartridgeValue(Constant: 0))])] }, fragment: "zero divisor");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "x"), Operation: "shl", Value: new CartridgeValue(Constant: 8))])] }, fragment: "eight or more");
        Refuses(document: document with { Arrays = [new CartridgeArray(Name: "x", Initial: [1])] }, fragment: "reuse a variable name");
        Refuses(document: document with { Arrays = [.. Enumerable.Range(start: 0, count: 29).Select(selector: i => new CartridgeArray(Name: $"big{i}", Initial: new int[256]))] }, fragment: "state budget");
    }

    private static CartridgeDocument Blank(string target, string title) => CartridgeDocuments.Create(target: target, title: title);

    private static CartridgeValue Element(string array, CartridgeValue index) => new(Array: array, Index: index);

    // Guards the body behind a "done" latch so the measured state is the first frame's result, not a per-frame rerun.
    private static CartridgeRule Once(string name, CartridgeStatement[] actions) => new(
        Name: name,
        When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "done"), Comparison: "eq", Right: new CartridgeValue(Constant: 0))],
        Body: [.. actions, new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "done"), Operation: "set", Value: new CartridgeValue(Constant: 1))]);

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }

    private static MachineProbe Run(CartridgeDocument document, int frames, out CartridgeCompilation result) {
        ICartridgeCompiler compiler = document.Target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        result = compiler.Compile(document: document);
        var machine = new MachineProbe(result: result);
        machine.Run(frames: frames);
        return machine;
    }

    private sealed class MachineProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;
        public MachineProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "memory"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "memory"); }
        }
        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public byte Read(uint address) => m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: (ushort)address);
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
