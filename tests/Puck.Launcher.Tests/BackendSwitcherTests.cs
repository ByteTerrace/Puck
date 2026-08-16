using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Xunit;

namespace Puck.Launcher.Tests;

public sealed class BackendSwitcherTests {
    [Fact]
    public void ReadbackFollowsTheActivePresenter() {
        using var first = new TestPresenter(marker: 1);
        using var second = new TestPresenter(marker: 2);
        using var switcher = new BackendSwitcher(
            current: first,
            currentName: "first",
            other: second,
            otherName: "second"
        );
        var source = Surface.SameDeviceImage(
            format: SurfaceFormat.R8G8B8A8Unorm,
            height: 1U,
            imageHandle: 11,
            imageViewHandle: 12,
            width: 1U
        );

        switcher.Activate(
            binding: default,
            height: 1U,
            width: 1U
        );
        var firstRead = switcher.ReadSurface(surface: source);

        switcher.Switch();
        var secondRead = switcher.ReadSurface(surface: source);

        Assert.Equal(expected: ((byte)1), actual: firstRead.Pixels.Span[0]);
        Assert.Equal(expected: ((byte)2), actual: secondRead.Pixels.Span[0]);
        Assert.Equal(expected: source, actual: first.LastReadSurface);
        Assert.Equal(expected: source, actual: second.LastReadSurface);
    }
    [Fact]
    public void ReadbackReportsAnUnsupportedActivePresenter() {
        using var presenter = new PresenterWithoutReadback();
        using var switcher = new BackendSwitcher(
            current: presenter,
            currentName: "plain",
            other: null,
            otherName: null
        );
        var source = Surface.SameDeviceImage(
            format: SurfaceFormat.R8G8B8A8Unorm,
            height: 1U,
            imageHandle: 11,
            imageViewHandle: 12,
            width: 1U
        );

        var exception = Assert.Throws<NotSupportedException>(testCode: () => switcher.ReadSurface(surface: source));

        Assert.Contains(expectedSubstring: "plain", actualString: exception.Message);
    }

    private sealed class TestPresenter(byte marker) : PresenterWithoutReadback, IPresentSurfaceReadback {
        private readonly byte[] m_pixels = [marker, 0, 0, 255];

        public Surface LastReadSurface { get; private set; }

        public Surface ReadSurface(Surface surface) {
            LastReadSurface = surface;

            return Surface.CpuPixels(
                pixels: m_pixels,
                width: surface.Width,
                height: surface.Height,
                format: surface.Format
            );
        }
    }
    private class PresenterWithoutReadback : ISurfacePresenter {
        public void Activate(NativeSurfaceBinding binding, uint width, uint height) { }
        public void BeginFrame(uint width, uint height) { }
        public void Deactivate() { }
        public void Present(Surface surface) { }
        public void Dispose() { }
    }
}
