namespace Puck.Scripting;

/// <summary>The detail of an addon fault — a classification plus a human-readable diagnostic line.</summary>
/// <param name="Kind">The fault classification.</param>
/// <param name="Detail">The formatted diagnostic (e.g. <c>"addon ghost: OutOfFuel at tick 3140 — disabled; 'addon enable ghost' to retry"</c>).</param>
public readonly record struct AddonFault(AddonFaultKind Kind, string Detail) {
    /// <summary>Gets the healthy, no-fault value.</summary>
    public static AddonFault None => new(Detail: "", Kind: AddonFaultKind.None);
}
