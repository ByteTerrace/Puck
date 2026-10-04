using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfVisibilityCadenceLawTests {
    [Fact]
    public void RepeatedAmbientAndShadowWritesReplaceTheirOwnTally() {
        var root = RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);
        var visibility = File.ReadAllText(path: Path.Join(path1: root, path2: "frame/sdf-visibility.hlsli"));
        var ambient = File.ReadAllText(path: Path.Join(path1: root, path2: "surface/sdf-surface.hlsli"));
        var shadow = File.ReadAllText(path: Path.Join(path1: root, path2: "surface/sdf-shadow.hlsli"));
        var hit = File.ReadAllText(path: Path.Join(path1: root, path2: "passes/sdf-hit-stages.hlsli"));

        Assert.NotEqual(actual: SdfVisibility.ShadowQueriesWord, expected: SdfVisibility.AmbientQueriesWord);
        Assert.True(condition: (SdfVisibility.ShadowQueriesWord < (SdfWorldPackage.VisibilityRecordByteLength / sizeof(uint))));
        Assert.Contains(actualString: visibility, expectedSubstring: "sdfVisibilityStoreWord(record + word, min((uint)queries, SdfVisibilitySurfaceQueryMask));");
        Assert.Contains(actualString: hit, expectedSubstring: "SDF_VISIBILITY_AMBIENT_QUERIES_WORD");
        Assert.Contains(actualString: hit, expectedSubstring: "SDF_VISIBILITY_SHADOW_QUERIES_WORD");
        Assert.Contains(actualString: ambient, expectedSubstring: "sdfStoreVisibilityQueries(record, SDF_VISIBILITY_AMBIENT_QUERIES_WORD");
        Assert.Contains(actualString: shadow, expectedSubstring: "sdfStoreVisibilityQueries(record, SDF_VISIBILITY_SHADOW_QUERIES_WORD");
        Assert.DoesNotContain(actualString: ambient, expectedSubstring: "info.queries +=");
        Assert.DoesNotContain(actualString: shadow, expectedSubstring: "surfaceInfo.queries +=");
        Assert.All(SdfWorldPackage.NativeFragment.Resources.Where(predicate: static resource => (resource.From is not null)),
            static resource => Assert.True(condition: resource.PreservesPredecessor));
    }
}
