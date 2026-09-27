using Puck.Abstractions.Counting;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfKernelSet"/>: a load reads one file per kernel, named by its stem, and counts once into the
/// <c>shaders.sdf-kernels</c> counts it was handed, with exactly the bytes it read; the kernels it returns are those
/// files' bytes; a load that finds a kernel missing still counts as a load that started; both kinds are classified as
/// deterministic within one backend, since SPIR-V and DXIL sets differ in size; and changing any one kernel changes the
/// set's content key, which keys a host's persistent pipeline cache.
/// </summary>
public sealed class SdfKernelSetLawTests {
    private static readonly string[] Stems = [
        "sdf-world-ambient", "sdf-beam", "sdf-brick-bake", "sdf-cull-args",
        "sdf-instance-cull", "sdf-world-primary", "sdf-world-shadow", "sdf-sky", "sdf-world-surface", "sdf-world-views",
        "sdf-world-views-core", "sdf-world-views-folds",
    ];

    private static WorkCounterSet Work() =>
        new(
            kinds: [SdfKernelSet.Loads, SdfKernelSet.BytecodeBytes],
            name: SdfKernelSet.LoadWorkSourceName
        );

    [Fact]
    public void ALoadCountsOnceWithEveryByteItRead() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-test-").FullName;

        try {
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
            Assert.Equal(expected: new byte[] { 0 }, actual: kernels[SdfKernel.Ambient].ToArray());
            Assert.Equal(expected: Enumerable.Repeat(count: 12, element: ((byte)11)), actual: kernels[SdfKernel.ViewsFolds].ToArray());

            _ = SdfKernelSet.Load(
                bytecodeExtension: ".spv",
                directory: directory,
                work: work
            );

            Assert.Equal(expected: 2L, actual: work.Read(kind: SdfKernelSet.Loads));
            Assert.Equal(expected: (2L * expected), actual: work.Read(kind: SdfKernelSet.BytecodeBytes));
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void ALoadThatFindsAKernelMissingStillCountsAsALoad() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-test-").FullName;

        try {
            var work = Work();

            _ = Assert.ThrowsAny<IOException>(testCode: () => SdfKernelSet.Load(
                bytecodeExtension: ".spv",
                directory: directory,
                work: work
            ));
            Assert.Equal(expected: 1L, actual: work.Read(kind: SdfKernelSet.Loads));
            Assert.Equal(expected: 0L, actual: work.Read(kind: SdfKernelSet.BytecodeBytes));
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
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
}
