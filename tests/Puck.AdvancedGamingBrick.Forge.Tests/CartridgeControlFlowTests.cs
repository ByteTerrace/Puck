using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers nested branches, counted loops and loop exits, and pins the per-frame reservation against real hardware.</summary>
public sealed class CartridgeControlFlowTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void RepeatWalksItsIndexAndSumsAnArray(string target) {
        var document = Blank(target: target, title: "LOOP") with {
            Variables = [
                new CartridgeVariable(Name: "row", Initial: 0),
                new CartridgeVariable(Name: "total", Initial: 0),
                new CartridgeVariable(Name: "done", Initial: 0),
            ],
            Arrays = [new CartridgeArray(Name: "values", Initial: [1, 2, 3, 4, 5, 6])],
            Rules = [Once(name: "sum", body: [
                Repeat(count: 6, index: "row", body: [
                    Set(target: "total", operation: "add", value: new CartridgeValue(Array: "values", Index: new CartridgeValue(Variable: "row"))),
                ]),
            ])],
        };
        using var machine = Run(document: document, frames: 12, out var result);
        Assert.Equal(expected: 21, actual: machine.Read(address: result.Variables["total"]));
        Assert.Equal(expected: 6, actual: machine.Read(address: result.Variables["row"]));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void BreakLeavesTheIndexAtTheIterationThatBroke(string target) {
        var document = Blank(target: target, title: "BREAK") with {
            Variables = [
                new CartridgeVariable(Name: "slot", Initial: 0),
                new CartridgeVariable(Name: "seen", Initial: 0),
                new CartridgeVariable(Name: "done", Initial: 0),
            ],
            Arrays = [new CartridgeArray(Name: "cells", Initial: [4, 4, 0, 4, 4])],
            Rules = [Once(name: "scan", body: [
                Repeat(count: 5, index: "slot", body: [
                    new CartridgeStatement(
                        Kind: "if",
                        When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Array: "cells", Index: new CartridgeValue(Variable: "slot")), Comparison: "eq", Right: new CartridgeValue(Constant: 0))],
                        Then: [new CartridgeStatement(Kind: "break")]),
                    Set(target: "seen", operation: "add", value: new CartridgeValue(Constant: 1)),
                ]),
            ])],
        };
        using var machine = Run(document: document, frames: 12, out var result);
        Assert.Equal(expected: 2, actual: machine.Read(address: result.Variables["slot"]));
        Assert.Equal(expected: 2, actual: machine.Read(address: result.Variables["seen"]));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void NestedLoopsAndElseArmsAgree(string target) {
        var document = Blank(target: target, title: "NESTED") with {
            Variables = [
                new CartridgeVariable(Name: "outer", Initial: 0),
                new CartridgeVariable(Name: "inner", Initial: 0),
                new CartridgeVariable(Name: "cursor", Initial: 0),
                new CartridgeVariable(Name: "evens", Initial: 0),
                new CartridgeVariable(Name: "odds", Initial: 0),
                new CartridgeVariable(Name: "done", Initial: 0),
            ],
            Arrays = [new CartridgeArray(Name: "grid", Initial: new int[12])],
            Rules = [Once(name: "fill", body: [
                // grid[cursor] = outer * 4 + inner, walked by a running cursor rather than a per-cell multiply.
                Repeat(count: 3, index: "outer", body: [
                    Repeat(count: 4, index: "inner", body: [
                        new CartridgeStatement(
                            Kind: "set",
                            Target: new CartridgeTarget(Array: "grid", Index: new CartridgeValue(Variable: "cursor")),
                            Operation: "set",
                            Value: new CartridgeValue(Variable: "cursor")),
                        new CartridgeStatement(
                            Kind: "if",
                            When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "inner"), Comparison: "lt", Right: new CartridgeValue(Constant: 2))],
                            Then: [Set(target: "evens", operation: "add", value: new CartridgeValue(Constant: 1))],
                            Else: [Set(target: "odds", operation: "add", value: new CartridgeValue(Constant: 1))]),
                        Set(target: "cursor", operation: "add", value: new CartridgeValue(Constant: 1)),
                    ]),
                ]),
            ])],
        };
        using var machine = Run(document: document, frames: 12, out var result);
        Assert.Equal(expected: 12, actual: machine.Read(address: result.Variables["cursor"]));
        Assert.Equal(expected: 6, actual: machine.Read(address: result.Variables["evens"]));
        Assert.Equal(expected: 6, actual: machine.Read(address: result.Variables["odds"]));
        for (var cell = 0; cell < 12; ++cell) {
            Assert.Equal(expected: cell, actual: machine.Read(address: result.Arrays["grid"] + (uint)cell));
        }
    }

    /// <summary>
    /// Calibrates <see cref="CartridgeCost.FrameBudget"/> against hardware: a document the validator accepts at
    /// the ceiling must still complete one rule pass per hardware frame. A budget too generous shows up here as a
    /// counter that falls behind the frames actually run.
    /// </summary>
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void ADocumentAtTheBudgetKeepsUpWithTheFrameRate(string target) {
        const int Frames = 40;
        var document = Blank(target: target, title: "BUDGET") with {
            Variables = [
                new CartridgeVariable(Name: "slot", Initial: 0),
                new CartridgeVariable(Name: "ticks", Initial: 0),
                new CartridgeVariable(Name: "sink", Initial: 0),
            ],
            Arrays = [new CartridgeArray(Name: "cells", Initial: new int[180])],
            Rules = [new CartridgeRule(Name: "work", When: [], Body: [
                Repeat(count: LargestAcceptedSweep(target: target), index: "slot", body: [
                    Set(target: "sink", operation: "add", value: new CartridgeValue(Array: "cells", Index: new CartridgeValue(Variable: "slot"))),
                ]),
                Set(target: "ticks", operation: "add", value: new CartridgeValue(Constant: 1)),
            ])],
        };
        using var machine = Run(document: document, frames: Frames, out var result);
        var ticks = machine.Read(address: result.Variables["ticks"]);
        Assert.InRange(actual: ticks, low: Frames - 4, high: Frames);
    }

    [Fact]
    public void ValidationRefusesUnboundedAndMalformedControlFlow() {
        var document = Blank(target: "cgb", title: "REFUSE") with {
            Variables = [new CartridgeVariable(Name: "i", Initial: 0), new CartridgeVariable(Name: "x", Initial: 0)],
        };
        Refuses(document: document with { Rules = [Rule(body: [new CartridgeStatement(Kind: "break")])] }, fragment: "inside a repeat");
        Refuses(document: document with { Rules = [Rule(body: [Repeat(count: 0, index: "i", body: [Set(target: "x", operation: "add", value: new CartridgeValue(Constant: 1))])])] }, fragment: "iteration count");
        Refuses(document: document with { Rules = [Rule(body: [new CartridgeStatement(Kind: "repeat", Count: 4, Index: "missing", Body: [Set(target: "x", operation: "add", value: new CartridgeValue(Constant: 1))])])] }, fragment: "Unknown state variable");
        Refuses(document: document with { Rules = [Rule(body: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "x"), Operation: "set", Value: new CartridgeValue(Constant: 1), Count: 3)])] }, fragment: "cannot carry 'count'");
        Refuses(document: document with { Rules = [Rule(body: [new CartridgeStatement(Kind: "loop")])] }, fragment: "Expected set, if, repeat, break, map, blit, plot, save, load, play, stop, clock, fade or blend");
        // A sweep the hardware could not finish inside one frame must not validate.
        Refuses(document: document with {
            Arrays = [new CartridgeArray(Name: "cells", Initial: new int[255])],
            Rules = [Rule(body: [Repeat(count: 255, index: "i", body: [Set(target: "x", operation: "add", value: new CartridgeValue(Array: "cells", Index: new CartridgeValue(Variable: "i")))])])],
        }, fragment: "per-frame reservation");
    }

    // The widest sweep of one indexed add the validator still accepts, found by bisection rather than a pinned number
    // that would drift the moment a cost constant moves.
    private static int LargestAcceptedSweep(string target) {
        var low = 1;
        var high = CartridgeLimits.RepeatCount;
        while (low < high) {
            var probe = (low + high + 1) / 2;
            if (CartridgeDocuments.Validate(document: Sweep(target: target, count: probe)).Count == 0) { low = probe; } else { high = probe - 1; }
        }

        return low;
    }

    private static CartridgeDocument Sweep(string target, int count) => Blank(target: target, title: "SWEEP") with {
        Variables = [new CartridgeVariable(Name: "slot", Initial: 0), new CartridgeVariable(Name: "sink", Initial: 0)],
        Arrays = [new CartridgeArray(Name: "cells", Initial: new int[180])],
        Rules = [new CartridgeRule(Name: "work", When: [], Body: [
            Repeat(count: count, index: "slot", body: [
                Set(target: "sink", operation: "add", value: new CartridgeValue(Array: "cells", Index: new CartridgeValue(Variable: "slot"))),
            ]),
        ])],
    };

    private static CartridgeDocument Blank(string target, string title) => CartridgeDocuments.Create(target: target, title: title);

    private static CartridgeStatement Set(string target, string operation, CartridgeValue value) =>
        new(Kind: "set", Target: new CartridgeTarget(Variable: target), Operation: operation, Value: value);

    private static CartridgeStatement Repeat(int count, string index, CartridgeStatement[] body) =>
        new(Kind: "repeat", Count: count, Index: index, Body: body);

    private static CartridgeRule Rule(CartridgeStatement[] body) => new(Name: "rule", When: [], Body: body);

    // Guards the body behind a "done" latch so the measured state is the first frame's result, not a per-frame rerun.
    private static CartridgeRule Once(string name, CartridgeStatement[] body) => new(
        Name: name,
        When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "done"), Comparison: "eq", Right: new CartridgeValue(Constant: 0))],
        Body: [.. body, Set(target: "done", operation: "set", value: new CartridgeValue(Constant: 1))]);

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
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "control"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "control"); }
        }
        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public byte Read(uint address) => m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: (ushort)address);
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
