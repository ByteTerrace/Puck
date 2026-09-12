using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers the expression and predicate vocabulary a cartridge shares with the rule engine: a composed operand
/// evaluates on both machines with the compiler allocating whatever it holds in flight, and a gate composes through
/// all, any and not rather than being a conjunction of flat conditions.
/// </summary>
/// <remarks>Every case is authored through the infix spelling, because that is the text a document carries and the
/// only spelling an author writes; building tokens by hand would test a path nothing uses.</remarks>
public sealed class CartridgeExpressionTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void ANestedExpressionEvaluatesInOperandOrder(string target) {
        // (7 + 3) * 4 is 40; evaluating left to right without the grouping would give 7 + 12, and dividing before
        // subtracting would give something else again. One expression pins the whole order.
        var result = Compile(target: target, slots: ["a", "b", "out"], seeds: [7, 3, 0], body: [
            Write(slot: "out", value: "(a + b) * 4"),
        ]);

        Assert.Equal(expected: 40, actual: Read(result: result, slot: "out"));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AnExpressionSubtractsInTheOrderItIsWritten(string target) {
        // The operand order a stack machine is easy to get backwards: 9 - 4 is 5, never 251.
        var result = Compile(target: target, slots: ["a", "b", "out"], seeds: [9, 4, 0], body: [
            Write(slot: "out", value: "a - b"),
        ]);

        Assert.Equal(expected: 5, actual: Read(result: result, slot: "out"));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AComparisonInsideAnExpressionYieldsOneOrZero(string target) {
        var result = Compile(target: target, slots: ["a", "b", "greater", "lesser"], seeds: [9, 4, 0, 0], body: [
            Write(slot: "greater", value: "a > b"),
            Write(slot: "lesser", value: "a < b"),
        ]);

        Assert.Equal(expected: 1, actual: Read(result: result, slot: "greater"));
        Assert.Equal(expected: 0, actual: Read(result: result, slot: "lesser"));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void BoundsAndChoicesEvaluateOnBothMachines(string target) {
        var result = Compile(target: target, slots: ["a", "b", "low", "high", "held", "clamped"], seeds: [9, 4, 0, 0, 0, 0], body: [
            Write(slot: "low", value: "minimum(a, b)"),
            Write(slot: "high", value: "maximum(a, b)"),
            Write(slot: "held", value: "a > b ? a : b"),
            Write(slot: "clamped", value: "clamp(a, 1, 5)"),
        ]);

        Assert.Equal(expected: 4, actual: Read(result: result, slot: "low"));
        Assert.Equal(expected: 9, actual: Read(result: result, slot: "high"));
        Assert.Equal(expected: 9, actual: Read(result: result, slot: "held"));
        Assert.Equal(expected: 5, actual: Read(result: result, slot: "clamped"));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AnElementReadsThroughAComputedIndex(string target) {
        var result = Compile(target: target, slots: ["i", "out"], seeds: [1, 0], arrays: [new CartridgeArray(Name: "cells", Initial: [10, 20, 30, 40])], body: [
            Write(slot: "out", value: "cells[i + 2] + cells[i]"),
        ]);

        // cells[3] + cells[1] is 40 + 20. A constant-folded index would read cells[2] and give the wrong sum.
        Assert.Equal(expected: 60, actual: Read(result: result, slot: "out"));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AnyHoldsWhenEitherArmDoes(string target) {
        var document = Document(target: target, slots: ["a", "b", "fired"], seeds: [0, 0, 0], arrays: [], body: [
            Write(slot: "fired", value: "1"),
        ]);

        // Neither arm holds, so an any gate must refuse: the control that makes the positive case mean something.
        var quiet = Compile(document: document with {
            Rules = [document.Rules[0] with { When = Any(left: "a == 1", right: "b == 1") }],
        }, target: target);

        Assert.Equal(expected: 0, actual: Read(result: quiet, slot: "fired"));

        // The second arm holds on its own.
        var loud = Compile(document: document with {
            Variables = [new CartridgeVariable(Name: "a", Initial: 0), new CartridgeVariable(Name: "b", Initial: 1), new CartridgeVariable(Name: "fired", Initial: 0)],
            Rules = [document.Rules[0] with { When = Any(left: "a == 1", right: "b == 1") }],
        }, target: target);

        Assert.Equal(expected: 1, actual: Read(result: loud, slot: "fired"));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void NotInvertsItsArm(string target) {
        var document = Document(target: target, slots: ["a", "fired"], seeds: [0, 0], arrays: [], body: [
            Write(slot: "fired", value: "1"),
        ]);
        var gate = new ActionPredicate.Not(Predicate: Gate(text: "a == 1"));

        var inverted = Compile(document: document with { Rules = [document.Rules[0] with { When = gate }] }, target: target);

        Assert.Equal(expected: 1, actual: Read(result: inverted, slot: "fired"));

        var suppressed = Compile(document: document with {
            Variables = [new CartridgeVariable(Name: "a", Initial: 1), new CartridgeVariable(Name: "fired", Initial: 0)],
            Rules = [document.Rules[0] with { When = gate }],
        }, target: target);

        Assert.Equal(expected: 0, actual: Read(result: suppressed, slot: "fired"));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AButtonReadComposesUnderNot(string target) {
        // No button is held in the probe, so the negated read is what fires. The point is that input is an operand
        // like any other rather than a condition kind that only conjunction can reach.
        var document = Document(target: target, slots: ["fired"], seeds: [0], arrays: [], body: [
            Write(slot: "fired", value: "1"),
        ]);

        var result = Compile(document: document with {
            Rules = [document.Rules[0] with { When = new ActionPredicate.Not(Predicate: CartridgeExpressions.Pressing(button: "a", mode: "held")) }],
        }, target: target);

        Assert.Equal(expected: 1, actual: Read(result: result, slot: "fired"));
    }

    [Fact]
    public void AnOperationTheRuleLanguageHasButACartridgeDoesNotIsRefusedByName() {
        var document = Document(target: "cgb", slots: ["a", "out"], seeds: [9, 0], arrays: [], body: [
            Write(slot: "out", value: "squareRoot(a)"),
        ]);

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Message.Contains(value: "a cartridge does not", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void AWideSlotInsideAComposedExpressionIsRefused() {
        var document = Document(target: "cgb", slots: ["out"], seeds: [0], arrays: [], body: [
            Write(slot: "out", value: "score + 1"),
        ]) with {
            Variables = [new CartridgeVariable(Name: "out", Initial: 0), new CartridgeVariable(Name: "score", Initial: 0, Max: CartridgeLimits.WideMaximum)],
        };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Message.Contains(value: "wide slot", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void AnExpressionDeeperThanTheMachineSpendsIsRefused() {
        // Each nested addition holds one more operand in flight than the last.
        var deep = string.Join(separator: " + ", values: Enumerable.Range(start: 0, count: 2).Select(selector: static _ => "1"));

        for (var depth = 0; depth <= CartridgeLimits.NarrowMaximum; ++depth) {
            deep = $"(1 + ({deep}))";
            if (CartridgeExpressions.Depth(expression: ValueExpression.Parse(text: deep)) > CartridgeExpressions.MaxDepth) {
                break;
            }
        }

        var document = Document(target: "cgb", slots: ["out"], seeds: [0], arrays: [], body: [Write(slot: "out", value: deep)]);

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Message.Contains(value: "values at once", comparisonType: StringComparison.Ordinal));
    }

    private static ActionPredicate Gate(string text) {
        var parts = text.Split(separator: ' ');

        return CartridgeExpressions.Gate(
            left: ValueExpression.Parse(text: parts[0]),
            comparison: ((parts[1] == "==") ? ActionStateComparison.Equal : ActionStateComparison.NotEqual),
            right: ValueExpression.Parse(text: parts[2]));
    }

    private static ActionPredicate Any(string left, string right) =>
        new ActionPredicate.Any(Predicates: [Gate(text: left), Gate(text: right)]);

    private static CartridgeStatement Write(string slot, string value) =>
        new(Kind: "set", Target: new CartridgeTarget(State: slot), Value: ValueExpression.Parse(text: value));

    private static CartridgeDocument Document(string target, string[] slots, int[] seeds, CartridgeArray[] arrays, CartridgeStatement[] body) =>
        CartridgeDocuments.Create(target: target, title: "EXPR") with {
            Variables = [.. slots.Select(selector: (name, index) => new CartridgeVariable(Name: name, Initial: seeds[index]))],
            Arrays = arrays,
            Rules = [new CartridgeRule(Name: "work", Body: body)],
        };

    private static CartridgeCompilation Compile(string target, string[] slots, int[] seeds, CartridgeStatement[] body, CartridgeArray[]? arrays = null) =>
        Compile(document: Document(target: target, slots: slots, seeds: seeds, arrays: (arrays ?? []), body: body), target: target);

    private static CartridgeCompilation Compile(CartridgeDocument document, string target) {
        Assert.Empty(collection: CartridgeDocuments.Validate(document: document));

        return (((target == "agb") ? new AgbCartridgeCompiler() : (ICartridgeCompiler)new HgbCartridgeCompiler()).Compile(document: document));
    }

    private static int Read(CartridgeCompilation result, string slot) {
        using var probe = new ExpressionProbe(result: result);

        probe.Run(frames: 12);

        return probe.Read(address: result.Variables[slot]);
    }

    private sealed class ExpressionProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;

        public ExpressionProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "expression"); } else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "expression"); }
        }

        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public byte Read(uint address) => (m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: (ushort)address));
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
