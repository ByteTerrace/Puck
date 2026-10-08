using Puck.Abstractions.Counting;
using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Games.Tests;

[Collection(name: DocumentCompositionCollection.Name)]
public sealed class WorldCompileBootWorkLawTests {
    [Fact]
    public void ColdAndHeldSourceCompilesLeaveTheSameDeterministicAdmissionWork() {
        using var files = new TemporaryDirectory();

        files.WriteBytes(name: "base.world.json", bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()));
        files.WriteText(name: "middle.world.json", text: """{"basis":"base"}""");
        var path = files.WriteText(name: "subject.puck", text: "basis: \"middle\"\ndocumentId: \"compile-counts\"");
        var cache = new WorldCompileCache(directory: files.PathOf(name: "s71-counters-compile-cache"));
        var catalog = TestMachines.Catalog();

        WorldBootWork Boot() {
            WorldDefinitionFileSource.ForgetComposedDocuments();
            var work = new WorldBootWork();
            using var attribution = WorldBootWork.Attribute(work: work);

            Assert.True(condition: cache.TryCompile(compiled: out var compiled, failure: out var failure, path: path),
                userMessage: failure?.Diagnostics.FormatReport(filePath: path));
            Assert.True(condition: WorldSourceLoader.TryLoadForAdmission(admission: out _, catalog: catalog,
                catalogFingerprint: catalog.CompositionFingerprint, document: compiled!.Document!, path: path, reason: out var reason, sourceCompilation: compiled),
                userMessage: reason);
            return work;
        }

        var cold = Boot();
        var held = Boot();

        Assert.Equal(expected: 1L, actual: cold.Read(kind: WorldBootWork.Compiles));
        Assert.Equal(expected: 0L, actual: held.Read(kind: WorldBootWork.Compiles));
        Assert.True(condition: (cold.Read(kind: WorldBootWork.CompileCompositions) > 0L));
        Assert.True(condition: (cold.Read(kind: WorldBootWork.CompileDocumentsRead) > 0L));
        Assert.Equal(expected: 0L, actual: held.Read(kind: WorldBootWork.CompileCompositions));
        Assert.Equal(expected: 0L, actual: held.Read(kind: WorldBootWork.CompileDocumentsRead));
        foreach (var kind in WorldBootWork.Kinds) {
            if (kind.Class == WorkClass.Deterministic) {
                Assert.Equal(expected: held.Read(kind: kind), actual: cold.Read(kind: kind));
            }
        }
        Assert.Equal(expected: 2L, actual: cold.Read(kind: WorldBootWork.DocumentsRead));
        Assert.Equal(expected: 3L, actual: cold.Read(kind: WorldBootWork.Compositions));
    }
    [Fact]
    public void NestedCompilationAttributionRestoresOrdinaryCountsAndRetainsAllWork() {
        var work = new WorldBootWork();
        using var attribution = WorldBootWork.Attribute(work: work);

        using (WorldBootWork.AttributeCompilation()) {
            WorldBootWork.Count(kind: WorldBootWork.DocumentsRead);
            using (WorldBootWork.AttributeCompilation()) { WorldBootWork.Count(kind: WorldBootWork.Compositions); }
            WorldBootWork.Count(kind: WorldBootWork.CompositionsShared);
        }
        WorldBootWork.Count(kind: WorldBootWork.DocumentsRead);
        WorldBootWork.Count(kind: WorldBootWork.Compositions);
        WorldBootWork.Count(kind: WorldBootWork.CompositionsShared);
        foreach (var kind in new[] { WorldBootWork.CompileDocumentsRead, WorldBootWork.CompileCompositions, WorldBootWork.CompileCompositionsShared }) {
            Assert.Equal(expected: WorkClass.Pacing, actual: kind.Class);
            Assert.Equal(expected: 1L, actual: work.Read(kind: kind));
        }
        foreach (var kind in new[] { WorldBootWork.DocumentsRead, WorldBootWork.Compositions, WorldBootWork.CompositionsShared }) {
            Assert.Equal(expected: WorkClass.Pacing, actual: kind.Class);
            Assert.Equal(expected: 1L, actual: work.Read(kind: kind));
        }
    }
}
