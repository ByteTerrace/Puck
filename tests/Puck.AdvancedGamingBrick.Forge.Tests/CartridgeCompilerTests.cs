using System.Text;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;


namespace Puck.AdvancedGamingBrick.Forge.Tests;

public sealed class CartridgeCompilerTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AuthoredRulesExecuteOnBothNativeMachines(string target) {
        var draft = new CartridgeDraft(document: CartridgeDocuments.Create(target: target, title: "INPUT"));
        draft.Set(pointer: "/variables", json: """[{"name":"x","initial":255},{"name":"released","initial":0},{"name":"compared","initial":0}]""");
        draft.Set(pointer: "/rules", json: """
            [
              {"name":"press","when":{"$type":"compareValue","left":"$key:right:pressed","comparison":"Equal","right":"1","kind":"Int"},"body":[{"kind":"set","target":{"state":"x"},"operation":"Add","value":"2"}]},
              {"name":"release","when":{"$type":"compareValue","left":"$key:right:released","comparison":"Equal","right":"1","kind":"Int"},"body":[{"kind":"set","target":{"state":"released"},"operation":"Add","value":"1"}]},
              {"name":"compare","when":{"$type":"compareValue","left":"x","comparison":"Equal","right":"1","kind":"Int"},"body":[{"kind":"set","target":{"state":"compared"},"value":"42"}]}
            ]
            """);
        draft.Set(pointer: "/tiles/-", json: """{"name":"solid","pixels":["11111111","11111111","11111111","11111111","11111111","11111111","11111111","11111111"]}""");
        draft.Set(pointer: "/sprites/-", json: """{"name":"cursor","tile":"1","x":"x","y":"24","visible":"1"}""");
        var doc = draft.Check();
        ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var result = compiler.Compile(document: doc);
        Assert.Equal(expected: result.Rom, actual: compiler.Compile(document: CartridgeDocuments.Parse(utf8: CartridgeDocuments.Canonicalize(document: doc).Bytes)).Rom);
        using var machine = new MachineProbe(result: result);
        machine.Run(pressed: false, frames: 12);
        Assert.Equal(expected: 255, actual: machine.Read(address: result.Variables["x"]));
        machine.Run(pressed: true, frames: 5);
        Assert.Equal(expected: 1, actual: machine.Read(address: result.Variables["x"]));
        Assert.Equal(expected: 42, actual: machine.Read(address: result.Variables["compared"]));
        machine.Run(pressed: false, frames: 5);
        Assert.Equal(expected: 1, actual: machine.Read(address: result.Variables["released"]));
        var spriteX = target == "agb" ? 0x07000002u : 0xFE01u;
        Assert.Equal(expected: target == "agb" ? 1 : 9, actual: machine.Read(address: spriteX));
        Assert.Equal(expected: 1, actual: machine.Read(address: target == "agb" ? 0x07000004u : 0xFE02u));
        Assert.Equal(expected: target == "agb" ? 0x11 : 0xFF, actual: machine.Read(address: target == "agb" ? 0x06000020u : 0x8010u));
        Assert.NotEqual(expected: machine.Pixel(x: 0, y: 0), actual: machine.Pixel(x: 1, y: 24));
    }

    [Theory]
    [InlineData(ActionStateComparison.Equal, 128, 128, true)]
    [InlineData(ActionStateComparison.Equal, 128, 127, false)]
    [InlineData(ActionStateComparison.NotEqual, 128, 128, false)]
    [InlineData(ActionStateComparison.NotEqual, 128, 127, true)]
    [InlineData(ActionStateComparison.Less, 127, 128, true)]
    [InlineData(ActionStateComparison.Less, 128, 128, false)]
    [InlineData(ActionStateComparison.LessOrEqual, 128, 128, true)]
    [InlineData(ActionStateComparison.LessOrEqual, 129, 128, false)]
    [InlineData(ActionStateComparison.Greater, 255, 128, true)]
    [InlineData(ActionStateComparison.Greater, 128, 128, false)]
    [InlineData(ActionStateComparison.GreaterOrEqual, 127, 128, false)]
    [InlineData(ActionStateComparison.GreaterOrEqual, 128, 128, true)]
    public void ComparisonsAreUnsignedAndAgree(ActionStateComparison comparison, int left, int right, bool matches) {
        foreach (var target in new[] { "cgb", "agb" }) {
            var document = CartridgeDocuments.Create(target: target, title: "COMPARE") with {
                Variables = [new CartridgeVariable(Name: "result", Initial: 0)],
                Rules = [new CartridgeRule(Name: "rule", When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(constant: left), comparison: comparison, right: CartridgeExpressions.Of(constant: right)), Body: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "result"), Operation: null, Value: CartridgeExpressions.Of(constant: 1))])],
            };
            ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
            var result = compiler.Compile(document: document);
            using var machine = new MachineProbe(result: result);
            machine.Run(pressed: false, frames: 12);
            Assert.Equal(expected: matches ? 1 : 0, actual: machine.Read(address: result.Variables["result"]));
        }
    }

    [Fact]
    public void ByteOperationsUseCurrentVariablesAndWrapOnBothTargets() {
        foreach (var target in new[] { "cgb", "agb" }) {
            var document = CartridgeDocuments.Create(target: target, title: "OPERATIONS") with {
                Variables = [new CartridgeVariable(Name: "add", Initial: 0), new CartridgeVariable(Name: "sub", Initial: 0), new CartridgeVariable(Name: "and", Initial: 0), new CartridgeVariable(Name: "or", Initial: 0), new CartridgeVariable(Name: "xor", Initial: 0), new CartridgeVariable(Name: "done", Initial: 0)],
                Rules = [new CartridgeRule(Name: "once", When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(state: "done"), comparison: ActionStateComparison.Equal, right: CartridgeExpressions.Of(constant: 0)), Body: [
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "add"), Operation: null, Value: CartridgeExpressions.Of(constant: 255)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "add"), Operation: ExpressionOp.Add, Value: CartridgeExpressions.Of(constant: 2)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "sub"), Operation: ExpressionOp.Subtract, Value: CartridgeExpressions.Of(state: "add")),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "and"), Operation: null, Value: CartridgeExpressions.Of(constant: 240)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "and"), Operation: ExpressionOp.BitAnd, Value: CartridgeExpressions.Of(constant: 60)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "or"), Operation: null, Value: CartridgeExpressions.Of(constant: 240)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "or"), Operation: ExpressionOp.BitOr, Value: CartridgeExpressions.Of(constant: 60)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "xor"), Operation: null, Value: CartridgeExpressions.Of(constant: 240)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "xor"), Operation: ExpressionOp.BitXor, Value: CartridgeExpressions.Of(constant: 60)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "done"), Operation: null, Value: CartridgeExpressions.Of(constant: 1)),
                ])],
            };
            ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
            var result = compiler.Compile(document: document);
            using var machine = new MachineProbe(result: result);
            machine.Run(pressed: false, frames: 12);
            Assert.Equal(expected: 1, actual: machine.Read(address: result.Variables["add"]));
            Assert.Equal(expected: 255, actual: machine.Read(address: result.Variables["sub"]));
            Assert.Equal(expected: 48, actual: machine.Read(address: result.Variables["and"]));
            Assert.Equal(expected: 252, actual: machine.Read(address: result.Variables["or"]));
            Assert.Equal(expected: 204, actual: machine.Read(address: result.Variables["xor"]));
        }
    }

    [Fact]
    public void DraftEditsAreAtomicAndValidationNamesBadReferences() {
        var draft = new CartridgeDraft(document: CartridgeDocuments.Create(target: "agb", title: "DRAFT"));
        var before = draft.Show();
        Assert.Throws<ArgumentException>(testCode: () => draft.Set(pointer: "/tiles/99", json: "{}"));
        Assert.Equal(expected: before, actual: draft.Show());
        Assert.Throws<System.Text.Json.JsonException>(testCode: () => draft.Set(pointer: "/scrollX", json: """{"tokens":[],"tokens":[]}"""));
        Assert.Equal(expected: before, actual: draft.Show());
        draft.Set(pointer: "/scrollX", json: "\"missing\"");
        Assert.Throws<Puck.Assets.Documents.DocumentValidationException>(testCode: () => draft.Check());
        draft.Undo();
        Assert.Equal(expected: before, actual: draft.Show());
        draft.Set(pointer: "/title", json: "\"EDITED\"");
        Assert.Equal(expected: "EDITED", actual: draft.Check().Title);
        Assert.Throws<System.Text.Json.JsonException>(testCode: () => CartridgeDocuments.Parse(utf8: Encoding.UTF8.GetBytes(s: draft.Show().Replace(oldValue: "\"schema\":", newValue: "\"typo\":"))));
    }

    private sealed class MachineProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;
        public MachineProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "document"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "document"); }
        }
        public void Run(bool pressed, int frames) {
            m_agb?.RunFrames(keys: pressed ? AgbKeys.Right : AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: pressed ? JoypadButtons.Right : JoypadButtons.None, frames: frames);
        }
        public byte Read(uint address) => m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: (ushort)address);
        public uint Pixel(int x, int y) => m_agb?.ReadPixel(x: x, y: y) ?? m_hgb!.ReadPixel(x: x, y: y);
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
