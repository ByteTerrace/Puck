using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The one light list. Every light a view's walk visits is a record of the lights table: the frame's own lights, then one
// face light (SdfLightKind.Face) for every resident screen slot, a screen showing a source and an emitter face alike, at
// index LightsBase + slot. So the view's light mask, the tiles' masks and the glass waves' masks cull screens and emitter
// faces by the reach every other ranged light is culled by (sdfLightReachesBox), and the irradiance volume traces a static
// face's visibility as it traces a static point light's, which the views' probe answer occludes it by (sdfViewsLightsSeen,
// SdfLightInputs.none). No second walk or mask exists for screens.
//
// Invariant: the lights table the kernels read, the host's view masks (LightMaskOf) and the irradiance volume's traced
// lights all read one SdfLights, Lights, which PackFaceLights writes once a pack, after the screens' slots and ranges
// settle. A face's index is its slot's, so it keeps its index while its screen holds the slot. Where the guarantee stops:
// a slot a new screen claims names its face at an index whose probe bits the volume measured for the face before it, until
// the volume relights them (a face's placement moves the volume's local key, SdfMeshIrradiance.RefreshLighting), as an
// index shift of the frame's own traced lights does.
public sealed partial class SdfWorldTables {
    // A record that lights nothing, for an index between faces whose slot no screen holds: no view selects it (its weight
    // is zero) and the volume traces none.
    private static readonly SdfLight NoFaceLight = new(Kind: SdfLightKind.Occluder, Direction: Vector3.Zero, Color: Vector3.Zero, Weight: 0f, Param: 0f, Shadows: false);

    private readonly SdfLights m_shadedLights = new();
    // The slots whose surface a transform provider placed (SetScreenSurface): a face that moves is not traced, as a light
    // riding a dynamic transform is not.
    private UInt128 m_screenCarried;
    private int m_faceLightBase;

    /// <summary>Gets the lights the residency shades, the one list every pass, view mask and the irradiance volume read:
    /// the latest packed frame's lights, then a face light (<see cref="SdfLightKind.Face"/>) at
    /// <see cref="FaceLightBase"/> plus the slot for every resident screen slot (<see cref="ResidentScreens"/>).</summary>
    public SdfLights Lights => m_shadedLights;
    /// <summary>Gets the index of screen slot zero's face light in <see cref="Lights"/>: the latest packed frame's light
    /// count.</summary>
    public int FaceLightBase => m_faceLightBase;
    /// <summary>Gets the resident screen slots the latest pack gave no face light, because the lights table holds no
    /// record past <see cref="SdfLights.MaxLights"/>: each shows its picture but lights nothing.</summary>
    public int DroppedFaceLights { get; private set; }

    // Writes the lights the residency shades from the frame's lights and the resident screens' faces, after the screens'
    // slots, surfaces and ranges settle (PackScreenReaches).
    private void PackFaceLights(SdfFrame frame) {
        var lights = frame.Lights;
        var first = lights.Count;
        var count = first;
        var faces = 0;
        var dropped = 0;

        m_shadedLights.CopyFrom(source: lights);
        m_faceLightBase = first;
        foreach (var slot in BitWords.Members(mask: m_screenResident)) {
            var index = (first + slot);

            if (index >= SdfLights.MaxLights) {
                dropped++;
                continue;
            }
            for (; (count < index); count++) { m_shadedLights.Set(index: count, light: NoFaceLight); }

            var (origin, right, up, extent) = ScreenFrameOf(screen: slot);
            var facing = Vector3.Cross(vector1: right, vector2: up);
            var length = facing.Length();

            m_shadedLights.Set(index: index, light: SdfLight.Face(
                area: ((4f * extent.X) * extent.Y),
                center: origin,
                facing: ((length > 0f) ? (facing / length) : Vector3.Zero),
                range: m_screenReach[slot],
                slot: slot,
                traced: !BitWords.Contains(mask: m_screenCarried, index: slot)
            ));
            count = (index + 1);
            faces++;
        }
        m_shadedLights.Count = count;
        DroppedFaceLights = dropped;
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.FaceLights, amount: faces);
        SdfCadenceWork.ProcessWork.Add(kind: SdfCadenceWork.FaceLightsDropped, amount: dropped);
    }
}
