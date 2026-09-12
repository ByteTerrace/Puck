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
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "read"), Operation: null, Value: Element(array: "table", index: CartridgeExpressions.Of(state: "cursor"))),
                // indirect = table[pointers[cursor]]
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "indirect"), Operation: null, Value: Element(array: "table", index: Element(array: "pointers", index: CartridgeExpressions.Of(state: "cursor")))),
                // table[0] = table[0] + 5
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "table", Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 0))), Operation: ExpressionOp.Add, Value: CartridgeExpressions.Of(constant: 5)),
                // table[cursor] = 99
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "table", Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "cursor"))), Operation: null, Value: CartridgeExpressions.Of(constant: 99)),
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
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "read"), Operation: null, Value: Element(array: "table", index: CartridgeExpressions.Of(state: "past"))),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "table", Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "past"))), Operation: null, Value: CartridgeExpressions.Of(constant: 200)),
            ])],
        };
        using var machine = Run(document: document, frames: 12, out var result);
        Assert.Equal(expected: 0, actual: machine.Read(address: result.Variables["read"]));
        Assert.Equal(expected: 1, actual: machine.Read(address: result.Arrays["table"]));
        Assert.Equal(expected: 2, actual: machine.Read(address: result.Arrays["table"] + 1));
        Assert.Equal(expected: 3, actual: machine.Read(address: result.Arrays["table"] + 2));
    }

    [Theory]
    [InlineData(ExpressionOp.Multiply, 13, 11, 143)]
    [InlineData(ExpressionOp.Multiply, 16, 16, 0)]
    [InlineData(ExpressionOp.Multiply, 200, 3, 88)]
    [InlineData(ExpressionOp.Multiply, 7, 0, 0)]
    [InlineData(ExpressionOp.Multiply, 0, 7, 0)]
    [InlineData(ExpressionOp.Divide, 143, 11, 13)]
    [InlineData(ExpressionOp.Divide, 255, 1, 255)]
    [InlineData(ExpressionOp.Divide, 7, 2, 3)]
    [InlineData(ExpressionOp.Divide, 3, 7, 0)]
    [InlineData(ExpressionOp.Divide, 100, 0, 0)]
    [InlineData(ExpressionOp.Modulo, 143, 11, 0)]
    [InlineData(ExpressionOp.Modulo, 7, 2, 1)]
    [InlineData(ExpressionOp.Modulo, 3, 7, 3)]
    [InlineData(ExpressionOp.Modulo, 255, 16, 15)]
    [InlineData(ExpressionOp.Modulo, 100, 0, 0)]
    [InlineData(ExpressionOp.ShiftLeft, 5, 3, 40)]
    [InlineData(ExpressionOp.ShiftLeft, 255, 1, 254)]
    [InlineData(ExpressionOp.ShiftLeft, 1, 7, 128)]
    [InlineData(ExpressionOp.ShiftLeft, 3, 0, 3)]
    [InlineData(ExpressionOp.ShiftRight, 40, 3, 5)]
    [InlineData(ExpressionOp.ShiftRight, 255, 7, 1)]
    [InlineData(ExpressionOp.ShiftRight, 1, 1, 0)]
    [InlineData(ExpressionOp.ShiftRight, 3, 0, 3)]
    public void ExtendedArithmeticAgreesAcrossTargets(ExpressionOp operation, int left, int right, int expected) {
        foreach (var target in new[] { "cgb", "agb" }) {
            var document = Blank(target: target, title: "MATH") with {
                Variables = [
                    new CartridgeVariable(Name: "value", Initial: left),
                    new CartridgeVariable(Name: "operand", Initial: right),
                    new CartridgeVariable(Name: "done", Initial: 0),
                ],
                Rules = [Once(name: "apply", actions: [
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "value"), Operation: operation, Value: CartridgeExpressions.Of(state: "operand")),
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
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "cells", Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "slot"))), Operation: ExpressionOp.Multiply, Value: CartridgeExpressions.Of(constant: 7)),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "cells", Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 1))), Operation: ExpressionOp.Divide, Value: CartridgeExpressions.Of(constant: 3)),
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
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "missing", Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 0))), Operation: null, Value: CartridgeExpressions.Of(constant: 1))])] }, fragment: "Unknown array");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "table", Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 5))), Operation: null, Value: CartridgeExpressions.Of(constant: 1))])] }, fragment: "outside array");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "table"), Operation: null, Value: CartridgeExpressions.Of(constant: 1))])] }, fragment: "requires an index");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "x", Key: "0"), Operation: null, Value: CartridgeExpressions.Of(constant: 1))])] }, fragment: "cannot carry an index");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "x"), Operation: ExpressionOp.Divide, Value: CartridgeExpressions.Of(constant: 0))])] }, fragment: "zero divisor");
        Refuses(document: document with { Rules = [Once(name: "r", actions: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "x"), Operation: ExpressionOp.ShiftLeft, Value: CartridgeExpressions.Of(constant: 8))])] }, fragment: "eight or more");
        Refuses(document: document with { Arrays = [new CartridgeArray(Name: "x", Initial: [1])] }, fragment: "reuse a variable name");
        Refuses(document: document with { Arrays = [.. Enumerable.Range(start: 0, count: 29).Select(selector: i => new CartridgeArray(Name: $"big{i}", Initial: new int[256]))] }, fragment: "state budget");
    }

    private static CartridgeDocument Blank(string target, string title) => CartridgeDocuments.Create(target: target, title: title);

    private static ValueExpression Element(string array, ValueExpression index) => CartridgeExpressions.Of(state: array, key: CartridgeExpressions.Key(index: index));

    // Guards the body behind a "done" latch so the measured state is the first frame's result, not a per-frame rerun.
    private static CartridgeRule Once(string name, CartridgeStatement[] actions) => new(
        Name: name,
        When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(state: "done"), comparison: ActionStateComparison.Equal, right: CartridgeExpressions.Of(constant: 0)),
        Body: [.. actions, new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "done"), Operation: null, Value: CartridgeExpressions.Of(constant: 1))]);

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
