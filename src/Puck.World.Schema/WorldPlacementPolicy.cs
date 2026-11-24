namespace Puck.World;

/// <summary>Representation and bounded-work rules shared by authoring and presentation.</summary>
public static class WorldPlacementPolicy {
    /// <summary>The most analytic placement colliders materialized in a world.</summary>
    public const int MaxSolidPlacementColliders = 32_768;
    /// <summary>The timeline replay hold per frame, in seconds.</summary>
    public const float TimelineSecondsPerFrame = (8f / 60f);

    /// <summary>Returns the first slot after every authored screen, including gaps in authored indices.</summary>
    public static int DerivedFaceBase(WorldDefinition definition) => ((definition.Screens.Count == 0)
        ? 0 : checked((definition.Screens.Max(screen => screen.Index) + 1)));
    /// <summary>Returns one past the highest screen index a definition declares, authored screens and derived faces
    /// alike: the span of screen identities a presentation of it may bind. It bounds no count; a residency holds a
    /// slot only for the screens resident at once.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The span.</returns>
    public static int ScreenSpan(WorldDefinition definition) => checked((DerivedFaceBase(definition: definition) + definition.DerivedFaceScreens));
}
