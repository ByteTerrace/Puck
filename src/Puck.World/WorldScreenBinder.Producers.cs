
namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // Whether any screen shows external content: a camera, a capture or a probe output.
    private bool BindsExternal() {
        foreach (var slot in m_slots.Values) {
            if (ShowsExternal(source: ShownOf(screen: slot.Index))) {
                return true;
            }
        }

        return false;
    }
    // Whether a screen shows external content or a HUD frame names it: the consumers WorldCaptureFills converts the fills
    // for before any capture is armed, and the only reads the capture gate resolves to a fill.
    private bool ConsumesExternal() {
        if (BindsExternal()) {
            return true;
        }

        foreach (var (source, _) in m_frameSourceReferences.Keys) {
            if (ShowsExternal(source: source)) {
                return true;
            }
        }

        return false;
    }
}
