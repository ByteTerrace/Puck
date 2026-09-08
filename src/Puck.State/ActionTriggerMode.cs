using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>One edge/latch vocabulary, shared by every gated trigger the engine evaluates — a per-body fact
/// trigger and a state rule alike. It is deliberately not two concepts with two spellings: "fires while the condition
/// holds" and "fires once when the condition becomes true" is the same distinction at both scopes, so it is the same
/// enum.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionTriggerMode>))]
public enum ActionTriggerMode : byte {
    /// <summary>Fires every evaluation the condition holds — the default, and the right shape for a continuous effect
    /// (a per-tick drain, a standing impulse).</summary>
    Level,

    /// <summary>Fires once on the condition crossing from not-holding to holding, and re-arms only when it crosses
    /// back — the right shape for anything that writes a document row, since a level-triggered write fires once per
    /// tick the condition holds rather than once per crossing.</summary>
    Edge,
}
