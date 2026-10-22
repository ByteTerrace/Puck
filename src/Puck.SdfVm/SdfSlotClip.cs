using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// A run of dynamic-transform slots, one body's, drawn on both sides of a portal it straddles: every mesh draw in the
/// run is clipped to the front of the glass it straddles, and a copy of it, moved and scaled through the portal pair,
/// is drawn clipped to the front of the counterpart's glass, where the part of the body past the glass
/// shows. Presentation only.
/// </summary>
/// <param name="FirstSlot">The run's first slot.</param>
/// <param name="SlotCount">The run's slot count.</param>
/// <param name="Plane">The plane the draws are clipped by, <c>(n, d)</c>: the glass, its normal out of the door.</param>
/// <param name="CopyRotation">The rotation of the motion the copy is drawn through.</param>
/// <param name="CopyTranslation">The translation of that motion, after the rotation and scale.</param>
/// <param name="CopyPlane">The plane the copy is clipped by: the counterpart's glass, its normal out of the door.</param>
/// <param name="CopyScale">The uniform scale of that motion: the pair's scale, one for a pair of faces of one
/// size.</param>
public readonly record struct SdfSlotClip(int FirstSlot, int SlotCount, Vector4 Plane, Quaternion CopyRotation, Vector3 CopyTranslation, Vector4 CopyPlane, float CopyScale = 1f) {
    /// <summary>Clips every draw of a list whose frame slot falls in a run to that run's glass and appends its carried
    /// copy. A draw in no run is left as it is.</summary>
    /// <param name="draws">The draws, clipped in place and extended with the copies.</param>
    /// <param name="clips">The runs.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> or <paramref name="clips"/> is
    /// <see langword="null"/>.</exception>
    public static void Apply(List<SdfMeshDraw> draws, IReadOnlyList<SdfSlotClip> clips) {
        ArgumentNullException.ThrowIfNull(argument: draws);
        ArgumentNullException.ThrowIfNull(argument: clips);

        var count = draws.Count;

        for (var index = 0; (index < count); index++) {
            var draw = draws[index];

            foreach (var clip in clips) {
                if (
                    (draw.FrameSlot is not { } slot) ||
                    (slot < clip.FirstSlot) ||
                    (slot >= (clip.FirstSlot + clip.SlotCount))
                ) {
                    continue;
                }

                draws[index] = (draw with { ClipPlane = clip.Plane });
                draws.Add(item: (draw with {
                    CarryRotation = clip.CopyRotation,
                    CarryScale = clip.CopyScale,
                    CarryTranslation = clip.CopyTranslation,
                    ClipPlane = clip.CopyPlane,
                    Identity = new Copy(Of: draw.Identity),
                }));

                break;
            }
        }
    }

    // The identity of a draw's carried copy: stable across frames while the draw's is, and never equal to the draw's.
    private sealed record Copy(object Of);
}
