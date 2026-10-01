using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for <see cref="GpuImageLeases"/>: an image its owner disposes while a reader leases it is disposed only once the
/// last lease retires, an unleased one at once; a lease retired twice is refused by name and counts once; a handle no
/// owner created there is not leased; and leasing and retiring allocate nothing once the table has grown.
/// </summary>
public sealed class GpuImageLeasesLawTests {
    [Fact]
    public void AnImageItsOwnerDropsLivesUntilItsLastLeaseRetires() {
        var images = new GpuImageLeases();
        var factory = new CountingImages();
        var image = images.Wrap(factory: factory).Create(format: GpuPixelFormat.R8G8B8A8Unorm, height: 1, name: default, usage: GpuImageUsage.Sampled, width: 1);

        Assert.True(condition: images.TryLease(imageHandle: image.ImageHandle, lease: out var first));
        Assert.True(condition: images.TryLease(imageHandle: image.ImageHandle, lease: out var second));
        Assert.Equal(expected: image.ImageViewHandle, actual: first.ImageViewHandle);

        image.Dispose();
        image.Dispose();
        Assert.Equal(expected: (0, 1), actual: (factory.Disposed, images.Deferred));

        first.Retire();
        Assert.Equal(expected: (0, 1), actual: (factory.Disposed, images.Deferred));

        second.Retire();
        Assert.Equal(expected: (1, 0), actual: (factory.Disposed, images.Deferred));
        Assert.False(condition: images.TryLease(imageHandle: image.ImageHandle, lease: out _));
    }
    [Fact]
    public void ALeaseRetiredTwiceIsRefusedByNameAndCountsOnce() {
        var images = new GpuImageLeases();
        var factory = new CountingImages();
        var image = images.Wrap(factory: factory).Create(format: GpuPixelFormat.R8G8B8A8Unorm, height: 1, name: default, usage: GpuImageUsage.Sampled, width: 1);

        Assert.True(condition: images.TryLease(imageHandle: image.ImageHandle, lease: out var first));
        Assert.True(condition: images.TryLease(imageHandle: image.ImageHandle, lease: out var second));
        image.Dispose();
        first.Retire();

        // Retired twice while another lease holds the image: refused, and the image stays alive for the other lease.
        Assert.Contains(expectedSubstring: "retired twice", actualString: Assert.Throws<InvalidOperationException>(testCode: first.Retire).Message);
        Assert.Equal(expected: (0, 1), actual: (factory.Disposed, images.Deferred));

        // Retired twice after its slot holds a newer lease on another image: refused, and the newer lease still counts.
        var other = images.Wrap(factory: factory).Create(format: GpuPixelFormat.R8G8B8A8Unorm, height: 1, name: default, usage: GpuImageUsage.Sampled, width: 1);

        Assert.True(condition: images.TryLease(imageHandle: other.ImageHandle, lease: out var reused));
        Assert.Throws<InvalidOperationException>(testCode: first.Retire);
        other.Dispose();
        Assert.Equal(expected: 0, actual: factory.Disposed);

        second.Retire();
        reused.Retire();
        Assert.Equal(expected: (2, 0), actual: (factory.Disposed, images.Deferred));
        Assert.Throws<InvalidOperationException>(testCode: second.Retire);
    }
    [Fact]
    public void ALeaseStaysRetiredAfterThousandsOfReusesOfItsSlot() {
        var images = new GpuImageLeases();
        var factory = new CountingImages();
        var image = images.Wrap(factory: factory).Create(format: GpuPixelFormat.R8G8B8A8Unorm, height: 1, name: default, usage: GpuImageUsage.Sampled, width: 1);

        Assert.True(condition: images.TryLease(imageHandle: image.ImageHandle, lease: out var first));
        first.Retire();

        for (var reuse = 1; (reuse < 2048); reuse++) {
            Assert.True(condition: images.TryLease(imageHandle: image.ImageHandle, lease: out var transient));
            transient.Retire();
        }

        Assert.True(condition: images.TryLease(imageHandle: image.ImageHandle, lease: out var pending));
        image.Dispose();
        Assert.Equal(expected: 0, actual: factory.Disposed);
        Assert.Throws<InvalidOperationException>(testCode: first.Retire);
        Assert.Equal(expected: (0, 1), actual: (factory.Disposed, images.Deferred));
        pending.Retire();
        Assert.Equal(expected: (1, 0), actual: (factory.Disposed, images.Deferred));
    }
    [Fact]
    public void AnUnleasedImageIsDisposedAtOnceAndAStrangerIsNotLeased() {
        var images = new GpuImageLeases();
        var factory = new CountingImages();
        var image = images.Wrap(factory: factory).Create(format: GpuPixelFormat.R8G8B8A8Unorm, height: 1, name: default, usage: GpuImageUsage.Sampled, width: 1);

        Assert.False(condition: images.TryLease(imageHandle: 0x7777, lease: out _));
        image.Dispose();
        Assert.Equal(expected: 1, actual: factory.Disposed);
    }
    [Fact]
    public void LeasingAndRetiringAllocateNothingOnceGrown() {
        var images = new GpuImageLeases();
        var image = images.Wrap(factory: new CountingImages()).Create(format: GpuPixelFormat.R8G8B8A8Unorm, height: 1, name: default, usage: GpuImageUsage.Sampled, width: 1);
        var list = new LeaseRetireList();

        void Frame() {
            for (var lease = 0; (lease < 4); lease++) {
                if (images.TryLease(imageHandle: image.ImageHandle, lease: out var held)) {
                    list.Hold(lease: in held);
                }
            }

            list.RetireAll();
        }

        Frame();
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            for (var frame = 0; (frame < 16); frame++) {
                Frame();
            }
        }));
    }

    // Creates images with distinct handles and counts their disposals.
    private sealed class CountingImages : IGpuImageFactory {
        private nint m_next = 0x100;

        public int Disposed { get; private set; }

        public IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name) => new Image(owner: this, handle: (m_next += 0x10), format: format, width: width, height: height, usage: usage);
        public IGpuImage CreateDepth(in GpuDepthAttachment attachment, uint width, uint height, in GpuObjectName name) => new Image(owner: this, handle: (m_next += 0x10), format: attachment.Format, width: width, height: height, usage: GpuImageUsage.DepthAttachment);

        private sealed class Image(CountingImages owner, nint handle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) : IGpuImage {
            public GpuPixelFormat Format => format;
            public uint Height => height;
            public nint ImageHandle => handle;
            public nint ImageViewHandle => (handle + 1);
            public GpuImageUsage Usage => usage;
            public uint Width => width;

            public void Dispose() => owner.Disposed++;
        }
    }
}
