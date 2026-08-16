namespace Puck.Scripting;

/// <summary>The addon ABI's <c>Ask</c> subject kind wire values. Pinned independently of any consumer enum. The
/// decode-valid set is <c>{1, 3}</c> — <see cref="Body"/> (paired with the Drive/Observe capability bits) and
/// <see cref="Section"/> (paired with the Mutate capability bit alone, the addon mutation seam's own handle shape);
/// <see cref="Screen"/> and <see cref="Profile"/> remain number-pinned reservations, not yet admitted, so growth
/// is a range change, never a break. No wildcard ordinal exists on purpose: the wire has no spelling for asking
/// for one.</summary>
public enum AddonSubjectKind : byte {
    /// <summary>A simulated body. Paired with the Drive/Observe capability bits.</summary>
    Body = 1,

    /// <summary>A screen. Reserved; not admitted.</summary>
    Screen = 2,

    /// <summary>A world-document section. Paired with the Mutate capability bit alone — the addon mutation seam's
    /// handle shape (<c>AddonSimulationPump.TryValidateAsk</c>). NAME-KEYED, not ordinal-keyed: the <c>A</c> lane
    /// carries a guest-memory pointer and the <c>C</c> lane the UTF-8 byte length of the section's declared NAME
    /// (a <c>Puck.World.Protocol.WorldSection</c> member, matched case-insensitively), the same ptr/len convention
    /// <see cref="AddonAbi.RequestVerbs.SubmitMutation"/> already uses for a mutation payload — never a baked
    /// enum ordinal a prior renumbering can silently strand (see <c>Addons.WorldAddonRuntime.ResolveAsks</c>'s
    /// remarks on the drift class this closes). A <see cref="Body"/> ask still carries a plain population index
    /// in <c>A</c> with <c>C</c> required zero — bodies are a live table position, not a renumberable enum, so
    /// they keep the ordinal shape.</summary>
    Section = 3,

    /// <summary>A profile. Reserved; not admitted.</summary>
    Profile = 4,
}
