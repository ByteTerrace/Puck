using System.Text.RegularExpressions;
using Puck.Abstractions.Counting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfKernelSet"/>: a load reads one file per kernel, named by its stem, and counts once into the
/// <c>shaders.sdf-kernels</c> counts it was handed, with exactly the bytes it read; the kernels it returns are those
/// files' bytes; a load that finds a kernel missing still counts as a load that started; both kinds are classified as
/// deterministic within one backend, since SPIR-V and DXIL sets differ in size; changing any one kernel changes the
/// set's content key, which keys a host's persistent pipeline cache; every kernel the build compiles reads its host's
/// interface on both backends, stamp included; and the stamp moves with the instruction set.
/// </summary>
public sealed class SdfKernelSetLawTests {
    private static readonly string[] Stems = [
        "sdf-world-surface", "sdf-mesh-bake-mask", "sdf-brick-bake", "sdf-sky-runs", "sdf-world-views", "sdf-resolve",
        "sdf-composite", "sdf-sky-environment", "sdf-sky-environment-reduce", "sdf-screen-emission", "sdf-mesh-bake", "sdf-mesh-pyramid",
        "sdf-glass",
    ];

    private static WorkCounterSet Work() =>
        new(
            kinds: [SdfKernelSet.Loads, SdfKernelSet.BytecodeBytes],
            name: SdfKernelSet.LoadWorkSourceName
        );

    [Fact]
    public void ALoadCountsOnceWithEveryByteItRead() {
        using var scratch = new TemporaryDirectory(prefix: "puck-test-");
        var directory = scratch.RootPath;

        var expected = 0L;

        for (var index = 0; (index < Stems.Length); index++) {
            var bytes = Enumerable.Repeat(count: (index + 1), element: ((byte)index)).ToArray();

            File.WriteAllBytes(
                bytes: bytes,
                path: Path.Combine(
                    path1: directory,
                    path2: $"{Stems[index]}.comp.spv"
                )
            );
            expected += bytes.Length;
        }

        var work = Work();
        var kernels = SdfKernelSet.Load(
            bytecodeExtension: ".spv",
            directory: directory,
            work: work
        );

        Assert.Equal(expected: 1L, actual: work.Read(kind: SdfKernelSet.Loads));
        Assert.Equal(expected: expected, actual: work.Read(kind: SdfKernelSet.BytecodeBytes));
        // Passed through: each kernel is its own file's bytes.
        Assert.Equal(expected: new byte[] { 0 }, actual: kernels[SdfKernel.Surface].ToArray());
        Assert.Equal(expected: Enumerable.Repeat(count: 5, element: ((byte)4)), actual: kernels[SdfKernel.Views].ToArray());

        _ = SdfKernelSet.Load(
            bytecodeExtension: ".spv",
            directory: directory,
            work: work
        );

        Assert.Equal(expected: 2L, actual: work.Read(kind: SdfKernelSet.Loads));
        Assert.Equal(expected: (2L * expected), actual: work.Read(kind: SdfKernelSet.BytecodeBytes));
    }
    [Fact]
    public void ALoadThatFindsAKernelMissingStillCountsAsALoad() {
        using var scratch = new TemporaryDirectory(prefix: "puck-test-");
        var directory = scratch.RootPath;

        var work = Work();

        _ = Assert.ThrowsAny<IOException>(testCode: () => SdfKernelSet.Load(
            bytecodeExtension: ".spv",
            directory: directory,
            work: work
        ));
        Assert.Equal(expected: 1L, actual: work.Read(kind: SdfKernelSet.Loads));
        Assert.Equal(expected: 0L, actual: work.Read(kind: SdfKernelSet.BytecodeBytes));
    }
    [Fact]
    public void TheProcessSourceIsNamedAndClassified() {
        Assert.Equal(expected: "shaders.sdf-kernels", actual: SdfKernelSet.LoadWork.Name);
        Assert.Equal(
            expected: ["shaders.sdf-kernels.loads", "shaders.sdf-kernels.bytecode-bytes"],
            actual: SdfKernelSet.LoadWork.WorkKinds.ToArray().Select(selector: static kind => kind.Name)
        );
        Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: SdfKernelSet.Loads.Class);
        Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: SdfKernelSet.BytecodeBytes.Class);
    }
    [Fact]
    public void ChangingAnyOneKernelChangesTheContentKey() {
        var baseline = SdfTestPipelines.Kernels();
        var keys = SdfKernelSet.Kernels
            .Select(selector: kernel => baseline.With(bytecode: new byte[] { 2 }, kernel: kernel).ContentKey())
            .ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Equal(expected: SdfKernelSet.Kernels.Count, actual: keys.Count);
        Assert.DoesNotContain(collection: keys, expected: baseline.ContentKey());
    }
    // What the build compiles is what a host installs: every kernel reflects, on both backends, exactly the bindings its
    // interface places, and its pass block under the name this host's instruction-set stamp gives it.
    [Fact]
    public void EveryCompiledKernelReadsItsHostsInterfaceOnBothBackends() {
        using var reflector = SdfTestPipelines.Reflector();

        foreach (var kernel in SdfKernelSet.Kernels) {
            var stem = SdfKernelSet.StemOf(kernel: kernel);

            foreach (var extension in (OperatingSystem.IsWindows() ? [".spv", ".dxil"] : new[] { ".spv" })) {
                var reflected = reflector.Read(bytecode: File.ReadAllBytes(path: Path.Combine(
                    path1: SdfKernelSet.DefaultDirectory,
                    path2: $"{stem}.comp{extension}"
                )));

                Assert.Null(@object: SdfKernelSet.InterfaceMismatch(kernel: kernel, reflected: reflected));
                Assert.Contains(
                    collection: reflected,
                    filter: binding => (binding.Name == ("passGroup" + SdfKernelSet.LayoutOf(kernel: kernel).Interface.Stamp))
                );
            }
        }
    }
    // Both interfaces the kernels read carry this host's instruction set in their pass blocks' variable names.
    [Fact]
    public void TheKernelInterfacesCarryTheHostsInstructionSetStamp() {
        foreach (var layout in ((ReadOnlySpan<ShaderInterfaceLayout>)[SdfWorldInterfaces.WorldLayout, SdfWorldInterfaces.BrickBakeLayout, SdfWorldInterfaces.ResolveParameters.Layout])) {
            Assert.Equal(expected: SdfWorldInterfaces.Stamp, actual: layout.Interface.Stamp);
            Assert.Equal(
                expected: ("passGroup" + SdfWorldInterfaces.Stamp),
                actual: layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)).BlockVariableName
            );
        }

        // The bake's interface carries the field's instruction set alone, as the bake kernels do.
        Assert.Equal(expected: SdfWorldInterfaces.FieldStamp, actual: SdfWorldInterfaces.MeshBakeLayout.Interface.Stamp);
        Assert.NotEqual(expected: SdfWorldInterfaces.Stamp, actual: SdfWorldInterfaces.FieldStamp);
        Assert.All(
            action: kernel => Assert.Same(expected: SdfWorldInterfaces.MeshBakeLayout, actual: SdfKernelSet.LayoutOf(kernel: kernel)),
            collection: [SdfKernel.MeshBakeMask, SdfKernel.MeshBake]
        );

        // The recorded fingerprints are the model's: the host names the instruction set its kernels were generated against.
        Assert.Equal(expected: SdfIsaFingerprint.Value, actual: SdfIsaHlsl.DescribeFingerprint());
        Assert.Equal(expected: SdfIsaFingerprint.Field, actual: SdfIsaHlsl.DescribeFieldFingerprint());
        Assert.Equal(expected: SdfIsaHlsl.StampOf(fingerprint: SdfIsaFingerprint.Value), actual: SdfWorldInterfaces.Stamp);
        Assert.Equal(expected: SdfIsaHlsl.StampOf(fingerprint: SdfIsaFingerprint.Field), actual: SdfWorldInterfaces.FieldStamp);
    }
    // The renderer's declarations are no part of the field's instruction set: the bake's stamp, which keys its kernels'
    // bytecode and through it the stored meshes, is the fingerprint of the field's include and the encoding alone, so a
    // renderer constant added to the model moves the whole instruction set's stamp and leaves the bake's. The field's
    // include names nothing the renderer's declares, and the renderer's builds on it.
    [Fact]
    public void ARendererConstantMovesNoBakeStamp() {
        var field = SdfIsaHlsl.Generate();
        var render = SdfRenderHlsl.Generate();
        var encoding = SdfEncodingProbe.Describe();
        var withRenderConstant = render.Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: "#define SDF_LIGHT_STAGE_PROBE 3u\n#endif // SDF_RENDER_HLSLI",
            oldValue: "#endif // SDF_RENDER_HLSLI"
        );

        Assert.NotEqual(expected: render, actual: withRenderConstant);
        Assert.Contains(actualString: render, expectedSubstring: $"#include \"{SdfIsaHlsl.FileName}\"");
        Assert.Equal(expected: SdfIsaFingerprint.Field, actual: SdfIsaHlsl.FingerprintOf(encoding: encoding, include: field));
        Assert.NotEqual(
            expected: SdfIsaHlsl.FingerprintOf(encoding: encoding, include: (field + render)),
            actual: SdfIsaHlsl.FingerprintOf(encoding: encoding, include: (field + withRenderConstant))
        );

        // The renderer's own vocabulary stays out of the field's include, which the bake compiles against.
        foreach (var prefix in ((ReadOnlySpan<string>)["SDF_LIGHT_", "SDF_MESH_", "SDF_VISIBILITY_", "SDF_DEBUG_VIEW_", "SDF_REFERENCE_WHITE", "SDF_LOCAL_", "SDF_SKY_", "SDF_AIR_", "SDF_INDIRECT_SOURCES_"])) {
            Assert.DoesNotContain(actualString: field, expectedSubstring: ("#define " + prefix));
            Assert.Contains(actualString: render, expectedSubstring: ("#define " + prefix));
        }
    }
    // Kernels compiled against another instruction set carry another stamp, so no include beside them can make them pass
    // for this host's: an instruction set whose two opcodes, or whose two cell modes, trade values; one whose instruction
    // headers carry the shape and the blend in each other's lanes; and one whose builder or packer puts a field
    // elsewhere (a rotation's Y and W, a sampled region's Y and Z dimension bitfields, a weathering's Edge and Lines, a
    // sweep's start and end radii, a cell displacement's frequency and amplitude, a stroked path's start and end radii, a
    // rigid leaf's rotation X and Y, a path edge's two radii).
    [Fact]
    public void AnInstructionSetEncodedOtherwiseCarriesAnotherStamp() {
        var include = SdfIsaHlsl.Generate();
        var encoding = SdfEncodingProbe.Describe();
        var calls = SdfEncodingProbe.Calls();

        foreach (var (otherInclude, otherEncoding) in ((ReadOnlySpan<(string, string)>)[
            (Swapped(first: "SDF_OP_TRANSLATE", include: include, second: "SDF_OP_ROTATE"), encoding),
            (Swapped(first: "SDF_CELL_MODE_F1", include: include, second: "SDF_CELL_MODE_F2_MINUS_F1"), encoding),
            (Swapped(first: "SDF_INSTRUCTION_SHAPE(v)", include: include, second: "SDF_INSTRUCTION_BLEND(v)"), encoding),
        ])) {
            Assert.NotEqual(expected: SdfWorldInterfaces.Stamp, actual: SdfIsaHlsl.StampOf(fingerprint: SdfIsaHlsl.FingerprintOf(encoding: otherEncoding, include: otherInclude)));
        }
        foreach (var other in SdfEncodingTrades.Traded(calls: calls)) {
            var otherEncoding = SdfEncodingProbe.Describe(calls: [.. calls.Select(selector: call => ((call.Name == other.Name) ? other : call))]);

            Assert.NotEqual(expected: SdfWorldInterfaces.Stamp, actual: SdfIsaHlsl.StampOf(fingerprint: SdfIsaHlsl.FingerprintOf(encoding: otherEncoding, include: include)));
        }
        // A packer that writes a rigid leaf's rotation X and Y, or a stroked path edge's two radii, in each other's places.
        foreach (var wordsOf in ((ReadOnlySpan<Func<SdfProgram, uint[]>>)[SdfEncodingTrades.RigidLeafRotationXySwapped, SdfEncodingTrades.PathRadiiSwapped])) {
            Assert.NotEqual(expected: SdfWorldInterfaces.Stamp, actual: SdfIsaHlsl.StampOf(fingerprint: SdfIsaHlsl.FingerprintOf(encoding: SdfEncodingProbe.Describe(calls: calls, wordsOf: wordsOf), include: include)));
        }
    }

    // The include with two defines' values traded.
    private static string Swapped(string include, string first, string second) {
        var define = new Regex(options: RegexOptions.Multiline, pattern: $@"^(#define (?:{Regex.Escape(str: first)}|{Regex.Escape(str: second)}) +)(.+)$");
        var values = define.Matches(input: include).ToDictionary(elementSelector: static match => match.Groups[2].Value, keySelector: static match => match.Groups[1].Value.TrimEnd().Split(separator: ' ')[1]);
        var swapped = define.Replace(
            evaluator: match => (match.Groups[1].Value + values[((match.Groups[1].Value.TrimEnd().Split(separator: ' ')[1] == first) ? second : first)]),
            input: include
        );

        Assert.Equal(expected: 2, actual: values.Count);
        Assert.NotEqual(actual: swapped, expected: include);

        return swapped;
    }
}
