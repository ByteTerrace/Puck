using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// Which of a bake group's two resident detail tiers one draw stands for, so each view draws the group at the detail its
/// own pixels need rather than at the tier the residency's finest view demands: the group's finer tier
/// (<see cref="SdfMeshBandRole.Fine"/>) or its floor, the mesh at its coarsest tier that the bake keeps beside the finer
/// one (<see cref="SdfMeshBandRole.Floor"/>, <c>SdfMeshPathBake.Group.KeptFloor</c>). A raster pass's draw selection
/// (<c>sdfSelectionBandKeeps</c>, <c>frame/sdf-mesh-selection.hlsli</c>) draws the floor where its cell projects to at
/// most <see cref="SdfMeshTier.PixelShare"/> of a display pixel over the nearest point of the group's sphere (a view), or
/// to at most that share of a texel (a shadow cascade), and the finer tier everywhere else.
/// <para>The invariant: in every pass, each placement of a banded group draws exactly one of its two tiers. Both tiers'
/// records carry the same sphere and the same floor cell, and the kernel decides from those words and the draw's pose
/// alone, so the two answers are each other's complement bit for bit; a fine draw yields to its floor only while every
/// one of the floor's records (<see cref="FloorRow"/>, <see cref="FloorRows"/>) stands in the mesh region, so a floor
/// still landing leaves the finer tier drawn and never a hole. <c>SdfMeshDresser</c> is the one writer of bands: it
/// gives a group's rows a band only where the bake keeps a floor of the group's own shape beside a finer tier it draws,
/// and every row of the placement takes the same sphere and cell.</para>
/// <para>Presentation only: a band chooses which resident mesh a pass rasterizes and reaches no simulation state.</para>
/// </summary>
/// <param name="Role">Which tier the draw stands for; <see cref="SdfMeshBandRole.None"/> for a draw every pass draws.</param>
/// <param name="Center">The center of the sphere the band decides by, in the draw's object space: the center of the box
/// holding both tiers' meshes.</param>
/// <param name="Radius">The sphere's radius, in object units: half the diagonal of that box.</param>
/// <param name="Pixel">The display pixel, in object units at the sphere's nearest point, at and under which the floor
/// stands: the floor's cell over <see cref="SdfMeshTier.PixelShare"/>.</param>
/// <param name="FloorRow">For a fine draw, the first staged row of the placement's floor; zero otherwise.</param>
/// <param name="FloorRows">For a fine draw, how many rows the placement's floor takes; zero otherwise.</param>
public readonly record struct SdfMeshBand(SdfMeshBandRole Role, Vector3 Center, float Radius, float Pixel, int FloorRow, int FloorRows) {
    /// <summary>Gets the band of a draw every pass draws.</summary>
    public static SdfMeshBand None => default;

    /// <summary>Returns the band both tiers of one placement share, before each takes its role: the sphere about the box
    /// holding both meshes, and the floor's cell as the pixel it stands at.</summary>
    /// <param name="low">The least corner of the box holding both tiers' meshes, in object space.</param>
    /// <param name="high">The greatest corner of that box.</param>
    /// <param name="floorTier">The floor's tier (<see cref="SdfMeshTier"/>).</param>
    /// <returns>The band, its role <see cref="SdfMeshBandRole.None"/>.</returns>
    public static SdfMeshBand Of(Vector3 low, Vector3 high, int floorTier) => new(
        Center: ((low + high) * 0.5f),
        FloorRow: 0,
        FloorRows: 0,
        Pixel: (SdfMeshTier.Cell(tier: floorTier) / SdfMeshTier.PixelShare),
        Radius: (0.5f * Vector3.Distance(value1: low, value2: high)),
        Role: SdfMeshBandRole.None
    );

    /// <summary>Gets whether the draw stands for a group's floor, which only a pass that can take the floor draws: no
    /// static scene, trace or other consumer of the placement's surface counts it beside the finer tier.</summary>
    public bool IsFloor => (Role == SdfMeshBandRole.Floor);

    /// <summary>Writes the band's words into a draw record's band field (<see cref="SdfMeshRecord.BandWord"/>): the
    /// sphere's center and radius as floats, the pixel as a float, positive for a fine draw, negative for a floor and zero
    /// for none, then the floor's first row and row count.</summary>
    /// <param name="words">The band field, <see cref="SdfMeshRecord.BandWords"/> words.</param>
    public void Write(Span<uint> words) {
        var pixel = Role switch {
            SdfMeshBandRole.Fine => Pixel,
            SdfMeshBandRole.Floor => -Pixel,
            _ => 0f,
        };

        words[0] = BitConverter.SingleToUInt32Bits(value: Center.X);
        words[1] = BitConverter.SingleToUInt32Bits(value: Center.Y);
        words[2] = BitConverter.SingleToUInt32Bits(value: Center.Z);
        words[3] = BitConverter.SingleToUInt32Bits(value: Radius);
        words[4] = BitConverter.SingleToUInt32Bits(value: pixel);
        words[5] = unchecked((uint)FloorRow);
        words[6] = unchecked((uint)FloorRows);
    }
}
/// <summary>Which of a bake group's resident tiers a draw stands for (<see cref="SdfMeshBand"/>).</summary>
public enum SdfMeshBandRole : byte {
    /// <summary>No band: every pass draws the draw.</summary>
    None = 0,
    /// <summary>The group's finer tier, drawn wherever its floor does not stand.</summary>
    Fine = 1,
    /// <summary>The group's floor, drawn only where it stands.</summary>
    Floor = 2,
}
