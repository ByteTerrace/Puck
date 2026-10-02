using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>The time shaping from one key to the next, including the segment across the clock's wrap.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldKeyEase>))]
public enum WorldKeyEase {
    /// <summary>Move at a constant rate between the two values.</summary>
    Linear,
    /// <summary>Use the cubic smoothstep, with zero speed at both ends.</summary>
    Smooth,
    /// <summary>Hold the earlier value until the next key.</summary>
    Step,
}
/// <summary>One value at a point in a clock's authored span.</summary>
/// <typeparam name="T">The field's bindable type, which determines how values blend.</typeparam>
/// <param name="At">The point in the named clock's span, in that clock's authored units.</param>
/// <param name="Value">The value at this key.</param>
/// <param name="Ease">How time moves from this key to the next.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldKey<T>(double At, T Value, WorldKeyEase Ease = WorldKeyEase.Linear);
/// <summary>A periodic curve over a named presentation clock. The final key joins the first across the wrap.</summary>
/// <typeparam name="T">The field's bindable type.</typeparam>
/// <param name="Clock">The clock declared in the world's timeline.</param>
/// <param name="Keys">At least two keys, strictly ascending within the clock's span.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldKeys<T>(string Clock, IReadOnlyList<WorldKey<T>> Keys);
