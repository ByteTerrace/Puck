namespace Puck.Maths;

/// <summary>
/// What an <see cref="IFieldEvaluator"/> can answer about whatever it wraps — the assertion surface a consumer checks
/// once when binding the evaluator, never per query.
/// </summary>
/// <param name="WarpFree">Whether the wrapped representation is one the evaluator answers exactly everywhere. A
/// provider that refuses an unsupported input at construction, rather than answering approximately, reports
/// <see langword="true"/>.</param>
public readonly record struct FieldEvaluatorCapabilities(bool WarpFree);
/// <summary>
/// Deterministic, fixed-point access to a scalar field and its gradient.
/// </summary>
/// <remarks>
/// <para>Distances are signed: negative inside geometry, positive outside. The gradient points away from the nearest
/// surface, so a consumer wanting "down" computes <c>-gradient.Normalize()</c>. The field encodes no notion of planet,
/// gravity, or up.</para>
/// <para>The interface names no representation — a signed distance program, a sampled volume, a closed-form potential,
/// and an analytic half-space all satisfy it — so a producer and a consumer of a field may sit in sibling libraries
/// that never reference each other.</para>
/// </remarks>
public interface IFieldEvaluator {
    /// <summary>Gets what this evaluator can answer.</summary>
    FieldEvaluatorCapabilities Capabilities { get; }

    /// <summary>Evaluates the field at <paramref name="position"/>.</summary>
    /// <param name="position">The world-space point to evaluate. An implementation reads the WHOLE hierarchical
    /// position; one that can only answer within a single <see cref="FixedPosition"/> cell must say so on its own type
    /// and refuse or rebase a cross-cell query rather than silently answering for the wrong cell.</param>
    /// <param name="distance">The signed nearest-surface distance, when the method returns <see langword="true"/>.</param>
    /// <param name="material">The material id of the nearest surface, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the wrapped representation declares anything to answer against AND
    /// <paramref name="position"/> is expressible in its frame; otherwise <see langword="false"/> rather than a
    /// sentinel distance.</returns>
    bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material);
    /// <summary>Evaluates the field's gradient at <paramref name="position"/> — the unit-length direction of steepest
    /// distance increase.</summary>
    /// <param name="position">The world-space point to evaluate.</param>
    /// <param name="gradient">The unit-length gradient, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when every probe succeeded and the raw gradient was non-zero; a point on a flat
    /// or degenerate field answers <see langword="false"/>, matching <see cref="FixedVector3.Normalize"/>'s convention
    /// for a zero vector.</returns>
    bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient);
    /// <summary>Evaluates the field's gradient at <paramref name="position"/> with a caller-chosen probe step — the
    /// per-call peer of <see cref="TryFieldGradient(FixedPosition, out FixedVector3)"/> for a consumer measuring
    /// geometry at a scale the provider's default probe does not suit.</summary>
    /// <param name="position">The world-space point to evaluate.</param>
    /// <param name="epsilon">The finite-difference probe span in world units; a non-positive value takes the
    /// provider's own default, making this exactly the two-argument overload.</param>
    /// <param name="gradient">The unit-length gradient, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when every probe succeeded and the raw gradient was non-zero.</returns>
    bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient);
}
