using Puck.SdfVm;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // Camera views use the same per-view quality, pin and policy path as player views. A filmed view renders an image of
    // its own, whose height its mesh demand measures projected sizes in.
    private SdfViewSnapshot ResolveResolution(SdfViewSnapshot view, string name, uint width, uint height) =>
        ((Presenter?.DressResolution(height: height, name: name, view: view, width: width) ?? view) with { DemandHeight = height });
}
