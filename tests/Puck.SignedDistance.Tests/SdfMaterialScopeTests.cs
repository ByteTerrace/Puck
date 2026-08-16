using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfMaterialScopeTests {
    [Fact]
    public void OutOfOrderDisposeCanBeRetriedAfterTheInnerScopeCloses() {
        var builder = new SdfProgramBuilder();
        var outer = builder.BeginMaterialScope();
        var inner = builder.BeginMaterialScope();

        _ = Assert.Throws<InvalidOperationException>(testCode: outer.Dispose);

        inner.Dispose();
        outer.Dispose();

        Assert.NotNull(@object: outer.MaterialEnd);
        _ = builder.Build();
    }
}
