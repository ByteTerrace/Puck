namespace Puck.Text;

/// <summary>
/// A late-binding content-time channel a <see cref="TextEffectParameter"/> can read by name hash when it resolves an
/// effect. This is the deterministic replacement for the prior art's terminal environment variable: a caller supplies
/// a small list of named float channels (an FNV-1a hash of the variable name plus its current value) sourced from
/// content-time state — a content tick, a gameplay meter, an intensity — never the wall clock or RNG.
/// </summary>
/// <param name="NameHash">The FNV-1a hash of the variable's name (see <see cref="TextEnrichmentTags"/>'s sigil parse).</param>
/// <param name="Value">The variable's current value.</param>
public readonly record struct TextEnrichmentVariable(uint NameHash, float Value);
