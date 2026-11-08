using Xunit;

namespace Puck.SignedDistance.Tests;

// A document change rebuilds its object graph, so an unchanged program reaches the compiler as a fresh but equal
// definition. These laws pin what that costs and what it may not change.
public sealed class SdfShadeAdmissionLawTests {
    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value: value);

    // A program whose shared subexpression (the tint * clock product) feeds two outputs, built as a fresh graph each
    // call the way a deserialized document arrives. Parameter declarations may be renamed, reordered or padded with an
    // unused one; the order of a commutative operation's operands may be swapped.
    private static SdfShadeDefinition Source(bool respell = false, bool share = true, float roughness = 0.4f) {
        SdfShadeExpression Shared() => new("*", respell
            ? [new("component", [new("input", Name: "clock")], Name: "0"), new("parameter", Name: respell ? "k" : "tint")]
            : [new("parameter", Name: "tint"), new("component", [new("input", Name: "clock")], Name: "0")]);
        var shared = Shared();
        SdfShadeParameter tint = new(respell ? "k" : "tint", SdfShadeType.Float, [Bits(value: 0.25f)]);
        SdfShadeParameter rough = new(respell ? "r" : "rough", SdfShadeType.Float, [Bits(value: roughness)]);
        SdfShadeParameter unused = new("unused", SdfShadeType.Float, [Bits(value: 9f)]);
        IReadOnlyList<SdfShadeParameter> parameters = respell ? [unused, rough, tint] : [tint, rough];

        return new("shared", parameters, [
            new(SdfShadePhase.Surface, "roughness", new("+", [new("parameter", Name: respell ? "r" : "rough"), share ? shared : Shared()])),
            new(SdfShadePhase.Surface, "emissive", new("saturate", [share ? shared : Shared()])),
        ]);
    }

    [Fact]
    public void AnEqualSourceReusesTheProgramAnUnrelatedSourceCompiledBetween() {
        var first = SdfShadeProgram.Compile(definition: Source());
        _ = SdfShadeProgram.Compile(definition: Source(roughness: 0.9f));
        var again = SdfShadeProgram.Compile(definition: Source());

        Assert.Same(expected: first, actual: again);
    }

    [Fact]
    public void ARefusalIsReusedWithItsDiagnostic() {
        static SdfShadeDefinition Undefined() => new("bad", [], [new(SdfShadePhase.Surface, "roughness", new("parameter", Name: "missing"))]);
        var first = Assert.Throws<SdfShadeException>(testCode: () => SdfShadeProgram.Compile(definition: Undefined()));
        var again = Assert.Throws<SdfShadeException>(testCode: () => SdfShadeProgram.Compile(definition: Undefined()));

        Assert.Equal(expected: (first.Code, first.Message), actual: (again.Code, again.Message));
    }

    [Fact]
    public void ACanonicalIdentityIgnoresSpellingSharingAndParameterDeclarations() {
        var program = SdfShadeProgram.Compile(definition: Source());
        var respelled = SdfShadeProgram.Compile(definition: Source(respell: true));
        var copied = SdfShadeProgram.Compile(definition: Source(share: false));

        Assert.Equal(expected: program.Identity, actual: respelled.Identity);
        Assert.Equal(expected: program.Identity, actual: copied.Identity);
        Assert.Equal(expected: program.ParameterWords.ToArray(), actual: respelled.ParameterWords.ToArray());
        // The canonical document compiles to the program it came from.
        Assert.Equal(expected: program.Identity, actual: SdfShadeProgram.Compile(definition: program.Definition).Identity);
    }

    [Fact]
    public void ParameterDataNeverMovesIdentityButReachesTheSlots() {
        var program = SdfShadeProgram.Compile(definition: Source());
        var other = SdfShadeProgram.Compile(definition: Source(roughness: 0.9f));

        Assert.Equal(expected: program.Identity, actual: other.Identity);
        Assert.NotEqual(expected: program.ParameterWords.ToArray(), actual: other.ParameterWords.ToArray());
    }
}
