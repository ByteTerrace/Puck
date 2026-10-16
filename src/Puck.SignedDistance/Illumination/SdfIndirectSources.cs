namespace Puck.SignedDistance;

/// <summary>The independently enabled origins of indirect light.</summary>
[Flags]
public enum SdfIndirectSources {
    /// <summary>No indirect source contributes.</summary>
    None = 0,
    /// <summary>Authored lights reflected once at stored hits.</summary>
    Direct = 1,
    /// <summary>Earlier sweeps reflected again at stored hits.</summary>
    Feedback = 2,
    /// <summary>Material emission at stored hits.</summary>
    Emission = 4,
    /// <summary>The physical sky environment at world exits.</summary>
    Sky = 8,
    /// <summary>Captured screen and portal emission.</summary>
    Screens = 16,
    /// <summary>Every independently accounted source.</summary>
    All = Direct | Feedback | Emission | Sky | Screens,
}
