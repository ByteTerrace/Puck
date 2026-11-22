namespace Puck.SdfVm;

/// <summary>How the key light's far shadow entry keeps up with what moves (<c>render.farShadows</c>, a device table's row),
/// read through <see cref="SdfFrame.FarShadows"/> while <see cref="SdfUpdateRates.ShadowFar"/> is on: the moving casters
/// are drawn over the entry's static layer at most <see cref="Hertz"/> times a second, a redraw of the static layer is
/// spread over the frames of one such period, and the static layer holds through <see cref="TurnRadians"/> of the light's
/// turn (<see cref="SdfWorldShadows"/>). Presentation only.</summary>
/// <param name="Hertz">The refreshes a second, from 1 through <see cref="SdfUpdateRateTiers.EveryFrameHertz"/>, which
/// refreshes it every frame.</param>
/// <param name="TurnRadians">The light turn, in radians, a held static layer tolerates; zero for the turn that moves the
/// shadow of a caster as tall as the entry's reach by one of its texels.</param>
public readonly record struct SdfFarShadows(int Hertz, float TurnRadians) {
    /// <summary>Gets the rate of a world that authors none: every frame, holding the static layer only through a one-texel
    /// light turn.</summary>
    public static SdfFarShadows EveryFrame { get; } = new(Hertz: SdfUpdateRateTiers.EveryFrameHertz, TurnRadians: 0f);
}
