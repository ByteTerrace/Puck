using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // Camera registrations and session feeds own their controller, just as they own their view identity and history.
    // Nothing is allocated until that world's authored render lever enables adaptation.
    private float ResolveDynamicScale(ref WorldDynamicResolutionController? controller, string name, uint width, uint height, bool enabled) {
        if (!enabled) {
            controller?.Reset();
            return 0;
        }
        controller ??= new WorldDynamicResolutionController();
        return controller.UpdateFrame(context: in m_frameContext, work: Runtime?.NodeOf(instance: name),
            width: Math.Max(val1: 1u, val2: width), height: Math.Max(val1: 1u, val2: height),
            floor: WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Quarter), ceiling: 1);
    }
}
