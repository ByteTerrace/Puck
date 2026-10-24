namespace Puck.Scripting;

/// <summary>The addon ABI's <c>Ask</c> subject kind wire values, defined independently of any consumer enum. The
/// admitted kinds are Body (Drive/Observe), Section (Mutate), Screen (Observe/Control), and State (Observe).
/// <see cref="Profile"/> is reserved. No wildcard ordinal exists on
/// purpose: the wire has no spelling for asking for one.</summary>
public enum AddonSubjectKind : byte {
    /// <summary>A simulated body. Paired with the Drive/Observe capability bits.</summary>
    Body = 1,

    /// <summary>A screen index. Paired with Observe or Control; C is zero.</summary>
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

    /// <summary>A state row name: A/C carry UTF-8 pointer/length. Paired with Observe.</summary>
    State = 5,
}
