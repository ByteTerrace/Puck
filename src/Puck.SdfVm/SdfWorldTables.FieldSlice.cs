using System.Numerics;
using System.Runtime.InteropServices;

using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The live field slice's table (sdfFieldSliceWords): the colours of the image a residency's SdfLiveFieldSlice shows, and
// the plane they were sampled on, which the pass block carries (fieldSlice*) for the composite to draw. The table is
// written only when the image shown changes, so a standing slice owes the upload nothing; with no slice shown the pass
// block's opacity is zero and the composite reads nothing of it.
public sealed partial class SdfWorldTables {
    private const int FieldSliceRegionIndex = 12;

    private readonly GpuRegion m_fieldSliceRegion;
    // The image the table holds, compared by reference, and the opacity the pass block carries; null and zero for none.
    private SdfLiveFieldSlice.Image? m_fieldSliceImage;
    private float m_fieldSliceOpacity;
    // One more each time what the slice shows changes (its image or its opacity): the tables' and the composite's
    // signatures fold it, so a still view renders the slice's change.
    private ulong m_fieldSliceRevision;

    /// <summary>Writes the image a live slice shows into the slice table, when it is not the image the table holds, and
    /// carries its plane and opacity into the pass block; a slice that shows nothing makes the pass block draw nothing.
    /// Runs in the residency's preparation, before the tables' signature is updated.</summary>
    /// <param name="slice">The residency's live slice.</param>
    public void StageFieldSlice(SdfLiveFieldSlice slice) {
        ArgumentNullException.ThrowIfNull(slice);

        var shown = slice.Shown;
        var image = shown?.Image;
        var opacity = (shown?.Opacity ?? 0f);

        if (ReferenceEquals(objA: image, objB: m_fieldSliceImage) && (opacity == m_fieldSliceOpacity)) { return; }
        if ((image is not null) && !ReferenceEquals(objA: image, objB: m_fieldSliceImage)) {
            var bytes = (image.Words.Length * sizeof(uint));

            if (bytes > m_fieldSliceRegion.ShadowBytes) {
                Retire(replaced: m_fieldSliceRegion.Grow(byteCount: bytes), carryingRegion: m_fieldSliceRegion);
                m_bindingRevision++;
            }
            _ = m_fieldSliceRegion.Write(bytes: MemoryMarshal.AsBytes(span: image.Words.AsSpan()), offset: 0);
        }
        m_fieldSliceImage = image;
        m_fieldSliceOpacity = opacity;
        m_fieldSliceRevision++;
    }

    // The pass block's slice values (SdfPassValues.FieldSlice); all zero while no slice shows.
    private SdfFieldSliceValues FieldSliceValues {
        get {
            if ((m_fieldSliceImage is not { } image) || !(m_fieldSliceOpacity > 0f)) { return default; }

            var plane = image.Plane;

            return new SdfFieldSliceValues(
                AxisU: new Vector4(value: plane.AxisU, w: plane.Half),
                AxisV: new Vector4(value: plane.AxisV, w: plane.Resolution),
                Origin: new Vector4(value: plane.CentrePoint, w: Math.Clamp(value: m_fieldSliceOpacity, min: 0f, max: 1f))
            );
        }
    }
}
