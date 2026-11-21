using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class LinterTests {
    [InlineData("a != 0 ? 1 / a : 0", true)]
    [InlineData("a == 0 ? 0 : 1 / a", true)]
    [InlineData("a == 0 ? 0 : maximum(1, 7 % a)", true)]
    [InlineData("a ? 0 : (b ? 1 / a : 0)", true)]
    [InlineData("a ? 1 / 0 : 0", true)]
    [InlineData("a ? 1 / (2) : 0", false)]
    [InlineData("a ? 1 : 0", false)]
    [InlineData("1 / a", false)]
    [InlineData("1 / a > 0 ? 1 : 0", false)]
    [Theory]
    public void TernaryDivisionWarnsThatTheOtherArmDoesNotGuardIt(string expression, bool warns) {
        var diagnostics = new DiagnosticBag();
        PuckLinter.Lint(PuckParser.ParseDocument($"rule \"guarded\" {{\nlocal value = {expression}\na = value\n}}"), diagnostics);
        Assert.Equal(warns, diagnostics.Any(diagnostic => diagnostic.Code == PuckDiagnosticCodes.EagerConditionalArithmetic));
    }
    // A library's declaration is for its importers: one read by an importer (bare, dotted, or as a function a gate
    // calls) or by another of its own declarations is used, and one nothing in the set reads is reported against it.
    [Fact]
    public void ALibraryDeclarationNoSourceReadsIsReportedAgainstTheLibrary() {
        using var files = new TemporaryDirectory();
        var library = files.WriteText(name: "lib.puck", text: "let base = 2\nlet table = { step: base }\nfunction held(x) = x > table.step\nlet idle = 4\ntemplate unused(n) {\n  width: n\n}\n");
        var world = files.WriteText(name: "world.puck", text: "schema: \"puck.world.definition.v1\"\ndocumentId: \"world\"\n\nimport \"lib\"\n\nstate {\n  world {\n    slot count = 0\n  }\n}\n\nrule \"r\" {\n  if held(count) {\n    count = 0\n  }\n}\n");
        var use = new PuckLibraryUse();

        foreach (var path in (ReadOnlySpan<string>)[library, world]) {
            _ = WorldSourceDiagnostics.Diagnose(libraries: use, source: File.ReadAllText(path: path), sourcePath: path);
        }

        Assert.Equal(
            actual: use.Unused().Select(selector: static finding => (Path.GetFileName(path: finding.SourcePath), finding.Message.Split(separator: '\'')[1])),
            expected: [("lib.puck", "idle"), ("lib.puck", "unused")]
        );
    }
    // Every child a node declares is walked, so a constant read only where an effect takes a value is a use.
    [InlineData("schedule due in cooldown")]
    [InlineData("deal cooldown from deck to hand")]
    [Theory]
    public void AConstantReadOnlyInAnEffectsValueIsAUse(string effect) {
        var diagnostics = new DiagnosticBag();

        PuckLinter.Lint(PuckParser.ParseDocument($"let cooldown = 2\nrule \"r\" {{\n{effect}\n}}"), diagnostics);

        Assert.DoesNotContain(collection: diagnostics, filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.LintUnusedLet));
    }
    [Fact]
    public void CompositionReferencesCountAsUses() {
        var source = """
            let seed = 7
            let island = "island"
            let endpoint = "gate"
            let width = 4
            module terrain(value: Int) { host { width: value } }
            world island = terrain(value: seed)
            border island.arch(1), neighbour.under(endpoint) { width: width }
            """;
        var diagnostics = new DiagnosticBag();

        PuckLinter.Lint(PuckParser.ParseDocument(source), diagnostics);

        Assert.DoesNotContain(collection: diagnostics, filter: diagnostic => (diagnostic.Code == "PUCK_LINT_001"));
    }
    [Fact]
    public void TestLinterPassesWhenConstantUsed() {
        var source = @"puck: 1
let USED_VAL = ""alpha""
host {
    authority: USED_VAL
}
";
        var doc = PuckParser.ParseDocument(source);

        Assert.NotNull(@object: doc);

        var diags = new DiagnosticBag();

        PuckLinter.Lint(
            diagnostics: diags,
            document: doc
        );

        Assert.DoesNotContain(
            collection: diags,
            filter: d => (d.Code == "PUCK_LINT_001")
        );
    }
    // A source, the lint finding it draws, and the name that finding must mention.
    [InlineData("puck: 1\nentities [\n    {\n        name: \"Box\"\n        color: \"#FF0000\"\n    }\n]\n", "PUCK_LINT_004", "#FF0000")]
    [InlineData("puck: 1\nlet UNUSED_VAL = 42\nhost {\n    authority: \"test\"\n}\n", "PUCK_LINT_001", "UNUSED_VAL")]
    [InlineData("puck: 1\ntemplate UnusedTemplate() {\n    components []\n}\nhost {\n    authority: \"test\"\n}\n", "PUCK_LINT_001", "UnusedTemplate")]
    [Theory]
    public void TheLinterReportsItsFindingByName(string source, string code, string mentions) {
        var diagnostics = new DiagnosticBag();

        PuckLinter.Lint(
            diagnostics: diagnostics,
            document: PuckParser.ParseDocument(source)
        );

        Assert.Contains(
            collection: diagnostics,
            filter: d => ((d.Code == code) && d.Message.Contains(value: mentions))
        );
    }
}
