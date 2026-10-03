using Puck.SdfVm;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // Camera views use the same per-view quality, pin and policy path as player views.
    private SdfViewSnapshot ResolveResolution(SdfViewSnapshot view, string name, uint width, uint height) =>
        (Presenter?.DressResolution(height: height, name: name, view: view, width: width) ?? view);
}
