using Puck.Maths;

namespace Puck.SdfVm.Queries;

/// <summary>
/// What an <see cref="IFieldEvaluator"/> can answer about the program it wraps — the assertion surface a consumer
/// checks once when binding the evaluator.
/// </summary>
/// <param name="WarpFree">Whether the wrapped program contains no op or shape this evaluator lacks a fixed-point
/// implementation. <see cref="SdfFieldEvaluator"/> reports <see langword="true"/> because its constructor rejects
/// unsupported programs instead of constructing a partial evaluator.</param>
public readonly record struct FieldEvaluatorCapabilities(bool WarpFree);

/// <summary>
/// Deterministic, fixed-point access to a live <see cref="SdfProgram"/>'s signed distance FIELD — the one primitive
/// gravity, magnetism, wind, or any other "which way is down/toward/away" gameplay mechanic derives from, without
/// the field itself ever encoding what a consumer intends to do with the answer. <see cref="SdfFieldEvaluator"/> is
/// the built-in provider for live programs (see its type remarks for the interpreted subset).
/// <para>
/// THE GRAVITY DERIVATION — the whole reason this interface exists as its own seam rather than living inside a
/// gravity-specific type: a consumer wanting "down" at a point computes <c>-gradient.Normalize()</c> from
/// <see cref="TryFieldGradient"/>. That is ONE line, entirely the CONSUMER's, never the engine's — the field carries
/// no notion of "planet," "gravity," or "up." A consumer wanting the opposite sense (a rocket's escape thrust, a
/// balloon's lift, a repulsor) just drops the sign; a consumer wanting the field's raw steepest-ASCENT direction
/// uses the gradient unmodified. The engine names the PRIMITIVE (a scalar field and its gradient); nothing on this
/// interface, or in <see cref="SdfFieldEvaluator"/>, is aware that a caller might call the result "gravity."
/// </para>
/// </summary>
public interface IFieldEvaluator {
    /// <summary>What this evaluator can answer — check once, not per query.</summary>
    FieldEvaluatorCapabilities Capabilities { get; }

    /// <summary>Evaluates the wrapped program's signed distance field at <paramref name="position"/>.</summary>
    /// <param name="position">The world-space point to evaluate.</param>
    /// <param name="distance">The signed nearest-surface distance (negative inside geometry, positive outside),
    /// when the method returns <see langword="true"/>.</param>
    /// <param name="material">The material id of the nearest surface (the accumulator's winning material at
    /// <paramref name="position"/>), when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the wrapped program declares at least one shape to answer against (a
    /// shape-less program returns <see langword="false"/> rather than a meaningless sentinel distance).</returns>
    bool TryDistance(WorldCoord3 position, out FixedQ4816 distance, out int material);

    /// <summary>Evaluates the field's GRADIENT at <paramref name="position"/> — the unit-length direction of
    /// steepest distance INCREASE, i.e. the direction pointing directly away from the nearest surface.</summary>
    /// <param name="position">The world-space point to evaluate.</param>
    /// <param name="gradient">The unit-length gradient, when the method returns <see langword="true"/>. See
    /// <see cref="SdfFieldEvaluator"/>'s remarks for the four-tap tetrahedron central-difference probe.</param>
    /// <returns><see langword="true"/> when every probe <see cref="TryDistance"/> call succeeded and the raw
    /// (pre-normalize) gradient was non-zero (a point exactly on a flat/degenerate field answers
    /// <see langword="false"/>, matching <see cref="FixedVector3.Normalize"/>'s "no direction" convention for a
    /// zero vector).</returns>
    bool TryFieldGradient(WorldCoord3 position, out FixedVector3 gradient);
}
