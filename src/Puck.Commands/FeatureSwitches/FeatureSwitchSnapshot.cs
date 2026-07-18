namespace Puck.Commands;

/// <summary>
/// An immutable capture of every registered switch's value at one moment — the unit a benchmark harness snapshots at
/// suite start and restores verbatim at teardown (and a sweep restores between legs), so a run leaves the session's
/// switch state exactly as it found it.
/// </summary>
/// <remarks>
/// The snapshot records the value each switch's <c>Get</c> reported when <see cref="FeatureSwitchRegistry.Snapshot"/>
/// ran; <see cref="FeatureSwitchRegistry.Restore"/> re-applies those values through the live descriptors. A switch that
/// vanished between snapshot and restore is simply skipped, and a value a switch now rejects is a no-op — restore is
/// best-effort by construction.
/// </remarks>
/// <param name="Values">The captured <c>name → value</c> map, keyed ordinally.</param>
public sealed record FeatureSwitchSnapshot(IReadOnlyDictionary<string, string> Values) {
    /// <summary>The number of switch values the snapshot captured.</summary>
    public int Count => Values.Count;
}
