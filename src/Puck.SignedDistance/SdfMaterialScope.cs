namespace Puck.SignedDistance;

/// <summary>Constrains positional material recoloring to the materials added within a scope opened by
/// <see cref="SdfProgramBuilder.BeginMaterialScope"/>. This prevents <see cref="SdfProgramBuilder.WallpaperFold"/>
/// and <see cref="SdfProgramBuilder.RepeatPolar"/> strides from reaching materials owned by another emitter that
/// shares the same builder.
/// <para>
/// While a scope is open, any positional stride's reach — the largest additional material offset its fold could
/// produce (2× the stride for a hex wallpaper group's 3-coloring, 1× for every other wallpaper group, or
/// (sectorCount−1)× for <see cref="SdfProgramBuilder.RepeatPolar"/>) — is clamped, retroactively, to the largest value
/// that keeps every reachable material inside this scope's own added-material span (<see cref="MaterialBase"/> through
/// the material count at the moment the recolored shape is emitted). A caller that follows the existing convention
/// (add every material the fold recolors through before emitting the fold and the shapes it recolors) never triggers
/// the clamp. The mechanism is a safety net; authors should still add all recolored materials before emitting the fold.
/// </para>
/// <para>
/// A scope is also the span <see cref="SdfProgramBuilder.Build"/>'s positional-recolor gate judges a window emitted
/// inside it against (<see cref="MaterialEnd"/>) — including <see cref="SdfProgramBuilder.CellJitter"/>'s hashed
/// variants, which have no clamped form. That refusal is the isolation guarantee; the clamp above is the convenience.
/// </para>
/// <para>
/// With no scope open, <see cref="SdfProgramBuilder.WallpaperFold"/>/<see cref="SdfProgramBuilder.RepeatPolar"/> are
/// unaffected because this type's machinery does not run, and the gate judges a window against the whole palette —
/// with no scope, the whole palette is the scope.
/// </para></summary>
public sealed class SdfMaterialScope : IDisposable {
    private readonly SdfProgramBuilder m_builder;

    private bool m_disposed;

    internal SdfMaterialScope(SdfProgramBuilder builder, int materialBase) {
        m_builder = builder;
        MaterialBase = materialBase;
    }

    /// <summary>Gets the material index (a past/future <see cref="SdfProgramBuilder.AddMaterial"/> return value) this
    /// scope opened at. Materials added to the builder before the scope opened belong to an outer scope (or none) and
    /// are never touched by this scope's clamp; only materials added at or after this index, while the scope is open,
    /// bound a positional stride's legal reach.</summary>
    public int MaterialBase { get; }
    /// <summary>Gets the exclusive end of this scope's added-material span — the builder's material count at the
    /// moment the scope closed — or <see langword="null"/> while the scope is still open. A scope can still grow, so
    /// its end is not knowable at the moment a shape is emitted inside it; the builder records the scope identity with
    /// each positional recolor window and reads this at <see cref="SdfProgramBuilder.Build"/>, where every scope has
    /// closed.</summary>
    public int? MaterialEnd { get; internal set; }

    /// <summary>Closes the scope, returning the builder's innermost-open-scope state to whatever it was before this
    /// scope opened. Scopes must close in LIFO order relative to any other open scope — dispose the innermost one
    /// first (a <see langword="using"/> block does this automatically). If an out-of-order close throws, this scope
    /// remains open and may be disposed again after its inner scopes close.</summary>
    /// <exception cref="InvalidOperationException">This scope is not the innermost open scope.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_builder.EndMaterialScope(scope: this);
        m_disposed = true;
    }
}
