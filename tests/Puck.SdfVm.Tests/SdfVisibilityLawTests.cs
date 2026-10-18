using System.Numerics;
using System.Text.RegularExpressions;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the visibility record's shared words have one statement, <see cref="SdfVisibility"/>, which a
/// pick decodes with and the kernels read through their generated spellings. A dynamic-transform slot fits every lane
/// that carries it, so a table's last slot survives the instruction's float lane exactly, and a slot past it is refused
/// by name.
/// </summary>
public sealed partial class SdfVisibilityLawTests {
    private static string Root => RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);

    [Fact]
    public void EveryTableSlotSurvivesEachLaneThatCarriesIt() {
        var last = SdfProgram.MaxDynamicTransformSlot;

        // The table's slot count is exactly what the slot bits carry.
        Assert.Equal(actual: (last + 1L), expected: (1L << SdfProgram.DynamicTransformSlotBits));
        // The instruction's float data lane holds the last slot exactly and tells it from its neighbour.
        Assert.Equal(actual: ((int)((float)last)), expected: last);
        Assert.NotEqual(actual: ((float)last), expected: ((float)(last - 1)));
        // The kernels index a slot's rows at three times the slot plus two in 32 bits.
        Assert.True(condition: (((3UL * ((ulong)last)) + 2UL) <= uint.MaxValue));
        Assert.True(condition: (SdfProgram.NoDynamicTransformSlot < 0));

        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.ResetPoint().TransformDynamic(slot: last).Sphere(material: material, radius: 1f);
        Assert.Equal(expected: (last + 1), actual: builder.Build().RequiredDynamicTransformCapacity);
    }
    [Fact]
    public void ASlotPastTheLanesIsRefusedByName() {
        var past = (SdfProgram.MaxDynamicTransformSlot + 1);

        Assert.Equal(expected: "slot", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().TransformDynamic(slot: past)).ParamName);
        Assert.Equal(expected: "slot", actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().BeginInstanceDynamic(boundOffset: Vector3.Zero, boundRadius: 1f, slot: past)).ParamName);
    }
    [Fact]
    public void AnIdentityRoundTripsThroughItsSharedFields() {
        foreach (var kind in Enum.GetValues<SdfVisibilityKind>()) {
            foreach (var source in new[] { 0U, 1U, 12345U, SdfVisibility.SourceMask }) {
                var identity = SdfVisibility.IdentityOf(kind: kind, source: source);

                Assert.Equal(expected: kind, actual: SdfVisibility.KindOf(identity: identity));
                Assert.Equal(expected: source, actual: SdfVisibility.SourceOf(identity: identity));
            }
        }
        Assert.Equal(expected: 0U, actual: SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Background, source: 0));
    }
    [Fact]
    public void TheKernelsUnpackATransformSlotWordAsTheProgramPacksIt() {
        var header = SdfIsaHlsl.Generate();

        // The generated spelling is the C# pair: the static word, and the inverse written against the generated sentinel.
        Assert.Matches(
            actualString: header,
            expectedRegexPattern: $@"#define SDF_TRANSFORM_SLOT_STATIC_WORD\s+{SdfProgram.StaticTransformSlotWord}u\n"
        );
        Assert.Contains(
            actualString: header,
            expectedSubstring: "#define SDF_TRANSFORM_SLOT_UNPACK(word) ((int)(word) + SDF_TRANSFORM_SLOT_NONE)\n"
        );

        // That inverse, evaluated as the kernels evaluate it, unpacks every word the program packs.
        foreach (var slot in ((int[])[SdfProgram.NoDynamicTransformSlot, 0, 7, SdfProgram.MaxDynamicTransformSlot])) {
            var word = SdfProgram.PackTransformSlot(slot: slot);

            Assert.Equal(
                actual: (((int)word) + SdfProgram.NoDynamicTransformSlot),
                expected: SdfProgram.UnpackTransformSlot(word: word)
            );
        }
    }
    [Fact]
    public void NoKernelSpellsTheIdentityFieldsOrTheSlotSentinelByHand() {
        var spellers = Directory.EnumerateFiles(path: Root, searchPattern: "*.hlsl*", searchOption: SearchOption.AllDirectories)
            .Select(selector: path => Path.GetRelativePath(path: path, relativeTo: Root).Replace(newChar: '/', oldChar: '\\'))
            .Where(predicate: static path => !path.StartsWith(comparisonType: StringComparison.Ordinal, value: "isa/"))
            .Where(predicate: path => HandSpelledPattern().IsMatch(input: CodeOf(path: path)))
            .Order(comparer: StringComparer.Ordinal);

        Assert.Empty(collection: spellers);
    }

    // A source's code with its line comments removed.
    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: Root, path2: path)), replacement: string.Empty);
    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
    // A winner's, record's, light's or volume's transform slot set to or compared with a bare literal, the identity's
    // mask or shift written out, or a packed transform-slot word (a rigid segment's plan.z, a part binding's binding.x)
    // offset by one or compared with zero by hand rather than through SDF_TRANSFORM_SLOT_UNPACK and
    // SDF_TRANSFORM_SLOT_STATIC_WORD.
    [GeneratedRegex(pattern: @"\b(?:\w*[fF]rameSlot|\w*[dD]ynamicSlot|currentSlot|rigidSlot|composeSlot|savedFieldSlot|slot)\s*(?:=|==|!=|>=|<=|<|>)\s*-?[01]\b(?!\.)|0x3FFFFFFF|>>\s*30u?\b|<<\s*30u?\b|\b(?:plan\.z|binding\.x)\s*(?:[-+]\s*1u?\b|[=!]=\s*0u?\b)")]
    private static partial Regex HandSpelledPattern();
}
