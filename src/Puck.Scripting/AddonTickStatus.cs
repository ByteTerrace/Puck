namespace Puck.Scripting;

/// <summary>Specifies the outcome of a single addon tick.</summary>
public enum AddonTickStatus {
    /// <summary>The tick ran and its command records decoded cleanly.</summary>
    Ok = 0,

    /// <summary>The tick was skipped or faulted; no fresh commands are available.</summary>
    Faulted,
}
