using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class SourceMapOriginTests {
    private static readonly SourceSpan SharedSpan = new(Column: 3, Length: 4, Line: 7, Offset: 20);

    [Fact]
    public void EqualSpansFromDifferentImportsRetainDistinctDefinitionPaths() {
        var map = new SourceMap();

        using (map.PushOrigin(sourcePath: "first.puck", moduleInstance: "first")) {
            map.Register(jsonPointer: "/state/world/0", span: SharedSpan);
        }
        using (map.PushOrigin(sourcePath: "second.puck", moduleInstance: "second")) {
            map.Register(jsonPointer: "/state/world/1", span: SharedSpan);
        }

        Assert.True(map.TryGetOrigin("/state/world/0", out var first));
        Assert.True(map.TryGetOrigin("/state/world/1", out var second));
        Assert.Equal(SharedSpan, first.Span);
        Assert.Equal(SharedSpan, second.Span);
        Assert.Equal("first.puck", first.SourcePath);
        Assert.Equal("second.puck", second.SourcePath);
    }
    [Fact]
    public void InstancesOfOneDefinitionRetainDistinctInstancePaths() {
        var map = new SourceMap();

        using (map.PushOrigin(sourcePath: "shared.puck", moduleInstance: "left")) {
            map.Register(jsonPointer: "/state/world/0", span: SharedSpan);
        }
        using (map.PushOrigin(sourcePath: "shared.puck", moduleInstance: "right")) {
            map.Register(jsonPointer: "/state/world/1", span: SharedSpan);
        }

        Assert.True(map.TryGetOrigin("/state/world/0", out var left));
        Assert.True(map.TryGetOrigin("/state/world/1", out var right));
        Assert.Equal("shared.puck", left.SourcePath);
        Assert.Equal("shared.puck", right.SourcePath);
        Assert.Equal("left", left.ModuleInstancePath);
        Assert.Equal("right", right.ModuleInstancePath);
    }
    [Fact]
    public void NestedScopesAndSnapshotsRestoreCompleteOrigins() {
        var map = new SourceMap();

        using (map.PushOrigin(sourcePath: "outer.puck", moduleInstance: "outer")) {
            map.Register(jsonPointer: "/before", span: SharedSpan);
            var snapshot = map.Snapshot();

            using (map.PushOrigin(sourcePath: "inner.puck", moduleInstance: "inner")) {
                map.Register(jsonPointer: "/nested", span: SharedSpan);
            }
            map.Restore(snapshot);
            map.Register(jsonPointer: "/after", span: SharedSpan);
        }

        Assert.False(map.TryGetOrigin("/nested", out _));
        Assert.True(map.TryGetOrigin("/before", out var before));
        Assert.True(map.TryGetOrigin("/after", out var after));
        Assert.Equal("outer.puck", before.SourcePath);
        Assert.Equal("outer", before.ModuleInstancePath);
        Assert.Equal(before, after);

        using (map.PushOrigin(sourcePath: "outer.puck", moduleInstance: "outer"))
        using (map.PushOrigin(sourcePath: "inner.puck", moduleInstance: "inner")) {
            map.Register(jsonPointer: "/nestedAgain", span: SharedSpan);
        }
        Assert.True(map.TryGetOrigin("/nestedAgain", out var nested));
        Assert.Equal("inner.puck", nested.SourcePath);
        Assert.Equal("outer/inner", nested.ModuleInstancePath);
    }
    [Fact]
    public void IsolatedEntryScopesRestoreTheirParentAcrossNestingAndExceptions() {
        var map = new SourceMap();

        map.Register(jsonPointer: "/root", span: SharedSpan);

        Assert.Throws<InvalidOperationException>((Action)(() => {
            using (map.PushIsolatedEntries()) {
                map.Register(jsonPointer: "/outer", span: SharedSpan);
                using (map.PushIsolatedEntries()) {
                    map.Register(jsonPointer: "/inner", span: SharedSpan);
                    Assert.True(condition: map.TryGetSpan(jsonPointer: "/inner", span: out _));
                    Assert.False(condition: map.TryGetSpan(jsonPointer: "/outer", span: out _));
                }
                Assert.True(condition: map.TryGetSpan(jsonPointer: "/outer", span: out _));
                Assert.False(condition: map.TryGetSpan(jsonPointer: "/inner", span: out _));
                throw new InvalidOperationException(message: "exercise restoration");
            }
        }));

        Assert.True(condition: map.TryGetSpan(jsonPointer: "/root", span: out _));
        Assert.False(condition: map.TryGetSpan(jsonPointer: "/outer", span: out _));
        Assert.False(condition: map.TryGetSpan(jsonPointer: "/inner", span: out _));
    }
}
