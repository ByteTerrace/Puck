using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the <c>shaders.sdf-kernels</c> counts: a kernel-set load counts once, with exactly the bytes of every kernel
/// file it read, into the counts it was handed; the kernels it returns are those files' bytes; a load that finds a
/// kernel missing still counts as a load that started; and both kinds are classified as deterministic within one
/// backend, since SPIR-V and DXIL sets differ in size.
/// </summary>
public sealed class SdfWorldKernelsLoadWorkLawTests {
    private static readonly string[] Stems = [
        "sdf-world-ambient", "sdf-beam", "sdf-brick-bake", "sdf-brick-upload", "sdf-world-composite", "sdf-cull-args",
        "sdf-frame-upload", "sdf-instance-cull", "sdf-world-primary", "sdf-sky", "sdf-world-surface", "sdf-world-views",
        "sdf-world-views-core", "sdf-world-views-folds",
    ];

    private static WorkCounterSet Work() =>
        new(
            kinds: [SdfWorldKernels.Loads, SdfWorldKernels.BytecodeBytes],
            name: SdfWorldKernels.LoadWorkSourceName
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
            var kernels = SdfWorldKernels.Load(
                bytecodeExtension: ".spv",
                directory: directory,
                work: work
            );

            Assert.Equal(expected: 1L, actual: work.Read(kind: SdfWorldKernels.Loads));
            Assert.Equal(expected: expected, actual: work.Read(kind: SdfWorldKernels.BytecodeBytes));
            // Passed through: each kernel is its own file's bytes.
            Assert.Equal(expected: new byte[] { 0 }, actual: kernels.Ambient.ToArray());
            Assert.Equal(expected: Enumerable.Repeat(count: 14, element: ((byte)13)), actual: kernels.ViewsFolds.ToArray());

            _ = SdfWorldKernels.Load(
                bytecodeExtension: ".spv",
                directory: directory,
                work: work
            );

            Assert.Equal(expected: 2L, actual: work.Read(kind: SdfWorldKernels.Loads));
            Assert.Equal(expected: (2L * expected), actual: work.Read(kind: SdfWorldKernels.BytecodeBytes));
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

            _ = Assert.ThrowsAny<IOException>(testCode: () => SdfWorldKernels.Load(
                bytecodeExtension: ".spv",
                directory: directory,
                work: work
            ));
            Assert.Equal(expected: 1L, actual: work.Read(kind: SdfWorldKernels.Loads));
            Assert.Equal(expected: 0L, actual: work.Read(kind: SdfWorldKernels.BytecodeBytes));
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void TheProcessSourceIsNamedAndClassified() {
        Assert.Equal(expected: "shaders.sdf-kernels", actual: SdfWorldKernels.LoadWork.Name);
        Assert.Equal(
            expected: ["shaders.sdf-kernels.loads", "shaders.sdf-kernels.bytecode-bytes"],
            actual: SdfWorldKernels.LoadWork.WorkKinds.ToArray().Select(selector: static kind => kind.Name)
        );
        Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: SdfWorldKernels.Loads.Class);
        Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: SdfWorldKernels.BytecodeBytes.Class);
    }
}
