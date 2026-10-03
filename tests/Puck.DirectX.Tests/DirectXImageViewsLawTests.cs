using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Device-free laws for the process-local image view table.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXImageViewsLawTests {
    [Fact]
    public void AnExhaustedSlotNeverNamesAnotherView() {
        const BindingFlags Fields = BindingFlags.Static | BindingFlags.NonPublic;
        var gate = ((Lock)typeof(DirectXImageViews).GetField(bindingAttr: Fields, name: "Gate")!.GetValue(obj: null)!);
        var generations = ((List<uint>)typeof(DirectXImageViews).GetField(bindingAttr: Fields, name: "Generations")!.GetValue(obj: null)!);

        // Reach the last generation without billions of allocations. Hold the table's own gate throughout so
        // another law cannot observe or reuse the slot while its generation is being positioned.
        lock (gate) {
            var handle = DirectXImageViews.Register(view: new DirectXImageView());
            var slot = unchecked((uint)((long)handle));

            generations[checked((int)(slot - 1u))] = uint.MaxValue;
            var last = unchecked((nint)((((long)uint.MaxValue) << 32) | slot));
            nint replacement = 0;

            try {
                Assert.NotNull(@object: DirectXImageViews.Resolve(handle: last));
                DirectXImageViews.Release(handle: last);
                var view = new DirectXImageView();

                replacement = DirectXImageViews.Register(view: view);

                Assert.NotEqual(actual: unchecked((uint)((long)replacement)), expected: slot);
                Assert.Null(@object: DirectXImageViews.Resolve(handle: unchecked((nint)slot)));
                Assert.Null(@object: DirectXImageViews.Resolve(handle: last));
                DirectXImageViews.Release(handle: last);
                Assert.Same(expected: view, actual: DirectXImageViews.Resolve(handle: replacement));
            } finally {
                DirectXImageViews.Release(handle: last);
                DirectXImageViews.Release(handle: replacement);
            }
        }
    }
    [Fact]
    public void AStaleOrMalformedReleaseCannotFreeALiveView() {
        var stale = DirectXImageViews.Register(view: new DirectXImageView());

        DirectXImageViews.Release(handle: stale);
        DirectXImageViews.Release(handle: stale);
        var view = new DirectXImageView();
        var current = DirectXImageViews.Register(view: view);

        try {
            nint[] invalid = [0, stale, unchecked((nint)(1L << 32)), unchecked((nint)0x80000000L), -1];

            foreach (var handle in invalid) {
                Assert.Null(@object: DirectXImageViews.Resolve(handle: handle));
                DirectXImageViews.Release(handle: handle);
                Assert.Same(expected: view, actual: DirectXImageViews.Resolve(handle: current));
            }
        } finally {
            DirectXImageViews.Release(handle: current);
        }
    }
    [Fact]
    public void ParallelRegistrationsAndReleasesKeepTheirOwnViews() {
        Parallel.For(fromInclusive: 0, toExclusive: 256, body: _ => {
            var view = new DirectXImageView();
            var handle = DirectXImageViews.Register(view: view);

            try {
                Assert.Same(expected: view, actual: DirectXImageViews.Resolve(handle: handle));
            } finally {
                DirectXImageViews.Release(handle: handle);
            }
            Assert.Null(@object: DirectXImageViews.Resolve(handle: handle));
            DirectXImageViews.Release(handle: handle);
        });
    }
}
