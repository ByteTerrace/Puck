namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for <see cref="DisposeAfterDependents{TResource}"/>, which keeps a device alive until every image made on it is
/// released: retiring the device while a submitted frame's lease still defers an image's release disposes the device
/// only after that image, a retire with nothing outstanding disposes at once, nothing new may depend on a retired
/// device, and a dependent released more often than it was added is refused.
/// </summary>
public sealed class DisposeAfterDependentsLawTests {
    [Fact]
    public void ARetiredDeviceIsDisposedOnlyAfterALeaseDeferredImageIsReleased() {
        var order = new List<string>();
        var device = new DisposeAfterDependents<Recorded>(resource: new Recorded(name: "device", order: order));
        var image = new LeasedImage(device: device, order: order);

        image.Lease();
        image.Retire();
        device.Retire();

        Assert.Empty(collection: order);
        Assert.False(condition: device.IsDisposed);

        image.ReleaseLease();

        Assert.Equal(actual: order, expected: ["image", "device"]);
        Assert.True(condition: device.IsDisposed);
        Assert.Equal(expected: 0, actual: device.Dependents);
    }
    [Fact]
    public void ARetireWithNothingOutstandingDisposesAtOnceAndOnlyOnce() {
        var order = new List<string>();
        var device = new DisposeAfterDependents<Recorded>(resource: new Recorded(name: "device", order: order));
        var image = new LeasedImage(device: device, order: order);

        image.Retire();
        device.Retire();
        device.Retire();

        Assert.Equal(actual: order, expected: ["image", "device"]);
    }
    [Fact]
    public void NothingNewDependsOnARetiredDeviceAndAnExtraReleaseIsRefused() {
        var device = new DisposeAfterDependents<Recorded>(resource: new Recorded(name: "device", order: []));

        _ = Assert.Throws<InvalidOperationException>(testCode: device.RemoveDependent);

        device.AddDependent();
        device.Retire();

        _ = Assert.Throws<InvalidOperationException>(testCode: device.AddDependent);
        Assert.False(condition: device.IsDisposed);

        device.RemoveDependent();

        Assert.True(condition: device.IsDisposed);
    }

    private sealed class Recorded(string name, List<string> order) : IDisposable {
        public void Dispose() => order.Add(item: name);
    }
    // Models a camera target ring: an image a submitted frame leases is released by the lease, not by the retire.
    private sealed class LeasedImage {
        private readonly DisposeAfterDependents<Recorded> m_device;
        private readonly List<string> m_order;

        private int m_leases;
        private bool m_released;
        private bool m_retired;

        public LeasedImage(DisposeAfterDependents<Recorded> device, List<string> order) {
            m_device = device;
            m_order = order;
            device.AddDependent();
        }

        private void ReleaseWhenFree() {
            if (
                m_retired &&
                (0 == m_leases) &&
                !m_released
            ) {
                m_released = true;
                m_order.Add(item: "image");
                m_device.RemoveDependent();
            }
        }

        public void Lease() => ++m_leases;
        public void ReleaseLease() {
            --m_leases;
            ReleaseWhenFree();
        }
        public void Retire() {
            m_retired = true;
            ReleaseWhenFree();
        }
    }
}
