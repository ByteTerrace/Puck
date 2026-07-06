namespace Puck.Demo.Creator;

/// <summary>
/// What a creation is FOR — the editor frames, previews, and bakes differently per intent, so the tool always knows
/// which target the player is authoring toward.
/// </summary>
public enum CreatorIntent {
    /// <summary>A 3D world object (an avatar, scenery, a cabinet prop) — orbit camera, full 3D preview.</summary>
    Object = 0,
    /// <summary>2D sprite/background art destined for the brick bake — head-on framing against a matte backdrop, so
    /// what the player sees is what quantizes.</summary>
    Sprite = 1,
}
