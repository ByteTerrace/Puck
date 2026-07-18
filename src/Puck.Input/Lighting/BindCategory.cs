namespace Puck.Input.Lighting;

/// <summary>
/// The class of action a bound command belongs to, for the purpose of color-coding the keyboard. A palette maps
/// each category to a lamp color, so a whole family of binds reads as one hue at a glance. This is a
/// presentation grouping, not an engine concept — the host classifies its commands into these buckets.
/// </summary>
public enum BindCategory {
    /// <summary>No category / an unbound key.</summary>
    None = 0,
    /// <summary>Locomotion — walk, run, jump, strafe (the movement baseline).</summary>
    Movement,
    /// <summary>Camera and view control — look, orbit, zoom.</summary>
    Camera,
    /// <summary>World interaction — activate, boot, pick up, talk.</summary>
    Interact,
    /// <summary>The console / command surface.</summary>
    Console,
    /// <summary>Meta / session actions — menu, pause, quit, mode toggles.</summary>
    Meta,
    /// <summary>Benchmarking and measurement controls.</summary>
    Bench,
    /// <summary>Debug and inspection controls (view modes, hashes, captures).</summary>
    Debug,
    /// <summary>System-level actions that don't fit another bucket.</summary>
    System,
}
