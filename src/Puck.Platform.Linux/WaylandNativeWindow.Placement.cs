namespace Puck.Platform.Linux;

// Live placement under xdg-shell. A toplevel's size is its client's to choose wherever the compositor's configure leaves
// it open (a zero width and height), and Vulkan WSI sizes the surface from the swap chain the presenter rebuilds when
// ResizeCount moves, so a size request is the resize a configure event makes. The protocol gives a client no way to
// read or choose where its toplevel stands, nor which output shows it, so moving and centring refuse by name.
internal sealed partial class WaylandNativeWindow : INativeWindowPlacement {
    private const string PlacesItsOwnToplevels = "a Wayland compositor places its toplevels itself; xdg-shell gives a client no position to set";

    /// <inheritdoc/>
    public NativeWindowPlacement Placement => new(
        Client: null,
        Fullscreen: false,
        Height: Height,
        Monitor: null,
        MonitorArea: null,
        Width: Width,
        WorkArea: null
    );

    /// <inheritdoc/>
    public bool TrySetClientSize(uint width, uint height, out string? refusal) {
        refusal = NativeWindowPlacement.RefuseClientSize(height: height, width: width);
        if (refusal is not null) {
            return false;
        }
        if (!IsOpen) {
            refusal = "the window is closed";
            return false;
        }
        if ((width != Width) || (height != Height)) {
            Width = width;
            Height = height;
            ResizeCount++;
        }

        return true;
    }
    /// <inheritdoc/>
    public bool TryMoveClient(int x, int y, out string? refusal) {
        refusal = PlacesItsOwnToplevels;
        return false;
    }
    /// <inheritdoc/>
    public bool TryCenter(out string? refusal) {
        refusal = PlacesItsOwnToplevels;
        return false;
    }
    /// <inheritdoc/>
    public bool TrySetFullscreen(bool fullscreen, out string? refusal) => NativeWindowPlacement.WithoutFullscreen(fullscreen: fullscreen, kind: DisplayKind, refusal: out refusal);
}
