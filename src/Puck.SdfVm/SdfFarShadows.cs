namespace Puck.SdfVm;

/// <summary>How the key light's far shadow cascade keeps up with what moves (<c>render.farShadows</c>, a device table's
/// row), read through <see cref="SdfFrame.FarShadows"/> while <see cref="SdfUpdateRates.ShadowFar"/> is on: the cascade is
/// drawn again at least <see cref="Hertz"/> times a second of presentation time and held between, and a held cascade is
/// drawn again at once when the light turns past <see cref="TurnRadiansFor"/>. Presentation only.</summary>
/// <param name="Hertz">The refreshes a second, from 1 through <see cref="SdfUpdateRateTiers.EveryFrameHertz"/>, which
/// refreshes it every frame.</param>
/// <param name="TurnRadians">The light turn, in radians, a held cascade tolerates; zero for the turn that moves the
/// shadow of a caster at the far end of the cascade's depth span by one of its texels (see
/// <see cref="TurnRadiansFor"/>).</param>
public readonly record struct SdfFarShadows(int Hertz, float TurnRadians) {
    /// <summary>Gets the rate of a world that authors none: every frame, holding a cascade only through a one-texel light
    /// turn.</summary>
    public static SdfFarShadows EveryFrame { get; } = new(Hertz: SdfUpdateRateTiers.EveryFrameHertz, TurnRadians: 0f);

    /// <summary>Returns the light turn, in radians, past which a held far cascade is drawn again: the authored
    /// <see cref="TurnRadians"/>, or where none is authored the turn that moves the shadow of a caster at the far end of
    /// the cascade's depth span (<see cref="SdfMeshShadowCamera.Far"/>, its sphere and the casters' reach toward the light)
    /// by one of its texels.</summary>
    /// <param name="camera">The far cascade's camera.</param>
    /// <param name="texels">The texels along each side of its depth.</param>
    /// <returns>The turn, in radians.</returns>
    public float TurnRadiansFor(in SdfMeshShadowCamera camera, int texels) => ((TurnRadians > 0f)
        ? TurnRadians
        : (((2f * camera.HalfHeight) / Math.Max(val1: texels, val2: 1)) / Math.Max(val1: camera.Far, val2: 1f)));
    /// <summary>Returns whether a far cascade at this rate holds a static layer under the levers a frame enables: one
    /// refreshed at a reduced rate (<see cref="SdfUpdateRates.ShadowFar"/> on and <see cref="Hertz"/> below
    /// <see cref="SdfUpdateRateTiers.EveryFrameHertz"/>) draws the static scene's casters only when a static draw changes
    /// or its fit no longer stands, and each refresh draws the moving casters over a copy of them.</summary>
    /// <param name="rates">The levers the frame enables (<see cref="SdfFrame.UpdateRates"/>).</param>
    /// <returns>Whether the far cascade holds a static layer.</returns>
    public bool HoldsStaticLayer(SdfUpdateRates rates) => (((rates & SdfUpdateRates.ShadowFar) != 0) && (Hertz < SdfUpdateRateTiers.EveryFrameHertz));
}
