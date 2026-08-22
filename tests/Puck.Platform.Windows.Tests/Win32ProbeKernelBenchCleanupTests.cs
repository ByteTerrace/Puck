using System.Reflection;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class Win32ProbeKernelBenchCleanupTests {
    [Fact]
    public void PartialRingAcquisition_ReleasesEveryViewBeforeEveryTexture_AndSkipsEmptySlots() {
        var released = new List<nint>();
        nint[]?[] textures = [[11, 12], null, [13, 0]];
        nint[]?[] views = [[21, 0], [22], null];
        var bench = typeof(Win32RawInput).Assembly.GetType(
            name: "Puck.Platform.Windows.Win32ProbeKernelBench",
            throwOnError: true
        )!;
        var cleanup = bench.GetMethod(
            name: "ReleaseRingResources",
            bindingAttr: BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(nint[]?[]), typeof(nint[]?[]), typeof(Action<nint>)],
            modifiers: null
        )!;

        _ = cleanup.Invoke(
            obj: null,
            parameters: [textures, views, (Action<nint>)(resource => released.Add(item: resource))]
        );

        Assert.Equal(expected: [21, 22, 11, 12, 13], actual: released);
    }
}
