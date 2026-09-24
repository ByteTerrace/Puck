using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class SourceMapOriginTests {
    private static readonly SourceSpan SharedSpan = new(Column: 3, Length: 4, Line: 7, Offset: 20);

    [Fact]
    public void EqualSpansFromDifferentImportsRetainDistinctDefinitionPaths() {
        var map = new SourceMap();

        using (map.PushOrigin(moduleInstance: "first", sourcePath: "first.puck")) {
            map.Register(jsonPointer: "/state/world/0", span: SharedSpan);
        }
        using (map.PushOrigin(moduleInstance: "second", sourcePath: "second.puck")) {
            map.Register(jsonPointer: "/state/world/1", span: SharedSpan);
        }

        Assert.True(condition: map.TryGetOrigin(jsonPointer: "/state/world/0", origin: out var first));
        Assert.True(condition: map.TryGetOrigin(jsonPointer: "/state/world/1", origin: out var second));
        Assert.Equal(SharedSpan, first.Span);
        Assert.Equal(SharedSpan, second.Span);
        Assert.Equal("first.puck", first.SourcePath);
        Assert.Equal("second.puck", second.SourcePath);
    }
    [Fact]
    public void InstancesOfOneDefinitionRetainDistinctInstancePaths() {
        var map = new SourceMap();

        using (map.PushOrigin(moduleInstance: "left", sourcePath: "shared.puck")) {
            map.Register(jsonPointer: "/state/world/0", span: SharedSpan);
        }
        using (map.PushOrigin(moduleInstance: "right", sourcePath: "shared.puck")) {
            map.Register(jsonPointer: "/state/world/1", span: SharedSpan);
        }

        Assert.True(condition: map.TryGetOrigin(jsonPointer: "/state/world/0", origin: out var left));
        Assert.True(condition: map.TryGetOrigin(jsonPointer: "/state/world/1", origin: out var right));
        Assert.Equal("shared.puck", left.SourcePath);
        Assert.Equal("shared.puck", right.SourcePath);
        Assert.Equal("left", left.ModuleInstancePath);
        Assert.Equal("right", right.ModuleInstancePath);
    }
    [Fact]
    public void NestedScopesAndSnapshotsRestoreCompleteOrigins() {
        var map = new SourceMap();

        using (map.PushOrigin(moduleInstance: "outer", sourcePath: "outer.puck")) {
            map.Register(jsonPointer: "/before", span: SharedSpan);
            var snapshot = map.Snapshot();

            using (map.PushOrigin(moduleInstance: "inner", sourcePath: "inner.puck")) {
                map.Register(jsonPointer: "/nested", span: SharedSpan);
            }
            map.Restore(snapshot: snapshot);
            map.Register(jsonPointer: "/after", span: SharedSpan);
        }

        Assert.False(condition: map.TryGetOrigin(jsonPointer: "/nested", origin: out _));
        Assert.True(condition: map.TryGetOrigin(jsonPointer: "/before", origin: out var before));
        Assert.True(condition: map.TryGetOrigin(jsonPointer: "/after", origin: out var after));
        Assert.Equal("outer.puck", before.SourcePath);
        Assert.Equal("outer", before.ModuleInstancePath);
        Assert.Equal(actual: after, expected: before);

        using (map.PushOrigin(moduleInstance: "outer", sourcePath: "outer.puck"))
        using (map.PushOrigin(moduleInstance: "inner", sourcePath: "inner.puck")) {
            map.Register(jsonPointer: "/nestedAgain", span: SharedSpan);
        }
        Assert.True(condition: map.TryGetOrigin(jsonPointer: "/nestedAgain", origin: out var nested));
        Assert.Equal("inner.puck", nested.SourcePath);
        Assert.Equal("outer/inner", nested.ModuleInstancePath);
    }
    [Fact]
    public void IsolatedEntryScopesRestoreTheirParentAcrossNestingAndExceptions() {
        var map = new SourceMap();

        map.Register(jsonPointer: "/root", span: SharedSpan);

        Assert.Throws<InvalidOperationException>(testCode: ((Action)(() => {
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
        })));

        Assert.True(condition: map.TryGetSpan(jsonPointer: "/root", span: out _));
        Assert.False(condition: map.TryGetSpan(jsonPointer: "/outer", span: out _));
        Assert.False(condition: map.TryGetSpan(jsonPointer: "/inner", span: out _));
    }
}
