using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>The participant an action effect addresses. A document-scope rule has no participant to select, so
/// only <see cref="Self"/> is admitted there; the other members belong to a host's per-participant action programs,
/// which share the <see cref="ActionEffect.SetState"/>/<see cref="ActionEffect.AddState"/> shapes with the rule
/// compiler and therefore carry the member on the wire.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionTarget>))]
public enum ActionTarget : byte {
    /// <summary>The participant whose trigger fired.</summary>
    Self,

    /// <summary>The target selected by the participant's active producer.</summary>
    ProducerTarget,

    /// <summary>The participant that applied the recipient's most recent targeted effect.</summary>
    AffectingSubject,
}
