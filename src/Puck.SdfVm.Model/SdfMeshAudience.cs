namespace Puck.SdfVm;

/// <summary>
/// The audiences one residency's draws are disclosed to, so a single residency of a world serves viewers of several
/// disclosures. A draw carries a tag (<c>SdfMeshDraw.Audience</c>, staged in its record's selection flags from
/// <see cref="SdfMeshRecord.AudienceShift"/>): <see cref="Neutral"/> for a draw every view draws, <see cref="Own"/> for
/// one shown only to the residency's own viewers, and <see cref="DrawTagOf"/> of a view audience for one shown only to
/// that audience's views. A view carries its audience (<c>SdfViewSnapshot.Audience</c>), zero for the residency's own,
/// which its selection reads from the controls (<see cref="ControlShift"/>). A shadow cascade, which every view of the
/// residency reads, draws for its own viewers while every view is theirs, and the neutral draws alone
/// (<see cref="NeutralOnly"/>) while any view draws for another audience, so no view sees the shadow of a draw it is
/// withheld.
/// </summary>
/// <remarks>The invariant: a view's selection keeps no draw whose tag is neither neutral nor its own audience's, and what
/// a view reads from a pass staged for others (the cascades) holds only draws every view reading it may select. The
/// selection kernel (<c>passes/sdf-mesh-occlusion.comp.hlsl</c>) enforces it for every raster pass, from the controls
/// <c>SdfMeshOcclusion.Stage</c> writes, whose audience argument has no default; a view derived inside another view (a
/// window, mirror or portal its viewer sees) is staged with its viewer's audience. Only the draws of rigid copies carry a
/// tag other than neutral (<c>SdfMeshDresser</c>); a bake group's draws are neutral, so a tagged placement is never merged
/// into a group. A tagged draw is never static (<c>SdfMeshDraw.IsStatic</c>), so the irradiance volume, the distant
/// shadow map and the far product, which every viewer reads alike, hold none.</remarks>
public static class SdfMeshAudience {
    /// <summary>The tag of a draw every view draws.</summary>
    public const byte Neutral = 0;
    /// <summary>The tag of a draw only the residency's own views (view audience zero) draw.</summary>
    public const byte Own = 1;
    /// <summary>The largest view audience a draw tag can name.</summary>
    public const byte MaxViewAudience = (byte.MaxValue - 1);
    /// <summary>The selection audience whose draw tag lies past every tag a draw can carry, so a pass staged with it
    /// selects the neutral draws alone: what every audience may see, which a residency's shadow cascades draw while its
    /// views serve more than one audience.</summary>
    public const byte NeutralOnly = byte.MaxValue;
    /// <summary>The bit of the selection controls' fourth word the view's audience starts at (eight bits).</summary>
    public const int ControlShift = 24;

    /// <summary>Returns the draw tag a view of an audience draws besides <see cref="Neutral"/>.</summary>
    /// <param name="viewAudience">The view audience: zero for the residency's own, at most <see cref="MaxViewAudience"/>.</param>
    /// <returns>The tag.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="viewAudience"/> exceeds <see cref="MaxViewAudience"/>.</exception>
    public static byte DrawTagOf(byte viewAudience) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: viewAudience, other: MaxViewAudience);

        return (byte)(viewAudience + 1);
    }
}
