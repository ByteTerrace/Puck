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
              {"name":"press","when":[{"kind":"key","key":"right","mode":"pressed"}],"body":[{"kind":"set","target":{"variable":"x"},"operation":"add","value":{"constant":2}}]},
              {"name":"release","when":[{"kind":"key","key":"right","mode":"released"}],"body":[{"kind":"set","target":{"variable":"released"},"operation":"add","value":{"constant":1}}]},
              {"name":"compare","when":[{"kind":"compare","left":{"variable":"x"},"comparison":"eq","right":{"constant":1}}],"body":[{"kind":"set","target":{"variable":"compared"},"operation":"set","value":{"constant":42}}]}
            ]
            """);
        draft.Set(pointer: "/tiles/-", json: """{"name":"solid","pixels":["11111111","11111111","11111111","11111111","11111111","11111111","11111111","11111111"]}""");
        draft.Set(pointer: "/sprites/-", json: """{"name":"cursor","tile":{"constant":1},"x":{"variable":"x"},"y":{"constant":24},"visible":{"constant":1}}""");
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
    [InlineData("eq", 128, 128, true)]
    [InlineData("eq", 128, 127, false)]
    [InlineData("ne", 128, 128, false)]
    [InlineData("ne", 128, 127, true)]
    [InlineData("lt", 127, 128, true)]
    [InlineData("lt", 128, 128, false)]
    [InlineData("le", 128, 128, true)]
    [InlineData("le", 129, 128, false)]
    [InlineData("gt", 255, 128, true)]
    [InlineData("gt", 128, 128, false)]
    [InlineData("ge", 127, 128, false)]
    [InlineData("ge", 128, 128, true)]
    public void ComparisonsAreUnsignedAndAgree(string comparison, int left, int right, bool matches) {
        foreach (var target in new[] { "cgb", "agb" }) {
            var document = CartridgeDocuments.Create(target: target, title: "COMPARE") with {
                Variables = [new CartridgeVariable(Name: "result", Initial: 0)],
                Rules = [new CartridgeRule(Name: "rule", When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Constant: left), Comparison: comparison, Right: new CartridgeValue(Constant: right))], Body: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "result"), Operation: "set", Value: new CartridgeValue(Constant: 1))])],
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
                Rules = [new CartridgeRule(Name: "once", When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "done"), Comparison: "eq", Right: new CartridgeValue(Constant: 0))], Body: [
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "add"), Operation: "set", Value: new CartridgeValue(Constant: 255)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "add"), Operation: "add", Value: new CartridgeValue(Constant: 2)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "sub"), Operation: "subtract", Value: new CartridgeValue(Variable: "add")),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "and"), Operation: "set", Value: new CartridgeValue(Constant: 240)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "and"), Operation: "and", Value: new CartridgeValue(Constant: 60)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "or"), Operation: "set", Value: new CartridgeValue(Constant: 240)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "or"), Operation: "or", Value: new CartridgeValue(Constant: 60)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "xor"), Operation: "set", Value: new CartridgeValue(Constant: 240)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "xor"), Operation: "xor", Value: new CartridgeValue(Constant: 60)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "done"), Operation: "set", Value: new CartridgeValue(Constant: 1)),
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
        Assert.Throws<System.Text.Json.JsonException>(testCode: () => draft.Set(pointer: "/scrollX", json: """{"constant":1,"constant":2}"""));
        Assert.Equal(expected: before, actual: draft.Show());
        draft.Set(pointer: "/scrollX", json: """{"variable":"missing"}""");
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
