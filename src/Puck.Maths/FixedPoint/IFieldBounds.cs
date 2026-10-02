namespace Puck.Maths;

/// <summary>How a certified sweep ended.</summary>
public enum CertifiedSweepOutcome {
    /// <summary>The whole displacement is certified clear.</summary>
    Clear = 0,
    /// <summary>The sphere's next box cannot be proved clear: a surface may lie within one more step.</summary>
    Contact = 1,
    /// <summary>The sweep could not certify further: the caller's bounds-query budget ran out, or a box's bounds were
    /// unbounded (it left the field's frame, where answers are refused). The fraction reached is still certified
    /// clear.</summary>
    Exhausted = 2,
}
/// <summary>A certified sweep's answer: how far along the displacement the sphere is proved clear, and why it
/// stopped.</summary>
/// <param name="Fraction">The fraction of the displacement, in <c>[0, 1]</c> and floored to the Q16 grid, along which
/// every point of the moving sphere is proved to keep the field above zero. A zero fraction can also mean the initial
/// sphere cannot be proved clear.</param>
/// <param name="Reached">A representable centre at the end of the certified travel, itself proved clear after a
/// positive advance; at zero travel it is the unmodified origin. The sweep carries its fraction at 2⁻³² of the
/// displacement, finer than <paramref name="Fraction"/>, so this is where a fast body may be placed.</param>
/// <param name="Outcome">Why the sweep stopped.</param>
/// <param name="BoundsQueries">The bounds queries the sweep spent, each one walk of the field over one box: never
/// more than the caller's budget.</param>
public readonly record struct CertifiedSweep(FixedQ4816 Fraction, FixedPosition Reached, CertifiedSweepOutcome Outcome, int BoundsQueries);
/// <summary>
/// Certified bounds on a scalar field over a box of points — the region reading of an <see cref="IFieldEvaluator"/>.
/// </summary>
/// <remarks>
/// <para>An interval is certified, not estimated: its lower endpoint is at or below every distance the field answers
/// at a point of the box, corners included, and its upper endpoint at or above every one. A provider that cannot
/// enclose its own answers over a box refuses rather than approximating, so a consumer can build a proof on the
/// interval, such as a sweep that never crosses a surface however fast it moves.</para>
/// <para>Like <see cref="IFieldEvaluator"/>, the interface names no representation, so a producer of bounds and a
/// consumer that proves something with them may sit in sibling libraries that never reference each other.</para>
/// </remarks>
public interface IFieldBounds {
    /// <summary>Gets the factor a clearance is multiplied by to propose a step that stays clear: the reciprocal of the
    /// field's Lipschitz bound, one for an exact distance. A proposal only; a consumer proves each step with
    /// <see cref="TryDistanceBounds"/> before taking it.</summary>
    FixedQ4816 StepScale { get; }

    /// <summary>Encloses every distance the field answers at a point of the box
    /// <c>[<paramref name="lower"/>, <paramref name="upper"/>]</c>, corners included.</summary>
    /// <param name="lower">The box's least corner.</param>
    /// <param name="upper">The box's greatest corner, at or above <paramref name="lower"/> on every axis.</param>
    /// <param name="distance">The enclosing interval on success.</param>
    /// <returns><see langword="false"/> when the field has nothing to answer against or a corner leaves its frame,
    /// as its point query refuses there; <see langword="true"/> otherwise.</returns>
    bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance);
}
/// <summary>
/// Moves a sphere along a displacement only as far as it is proved to stay clear of a field's surfaces.
/// </summary>
public interface ICertifiedSweepQuery {
    /// <summary>Advances a sphere along a displacement, certifying every step: every point of the swept sphere up to
    /// the returned fraction is proved outside the field's solid.</summary>
    /// <param name="origin">The sphere's centre at the start.</param>
    /// <param name="displacement">The whole motion; the answer is a fraction of it.</param>
    /// <param name="radius">The sphere's radius, at least zero.</param>
    /// <param name="boundsQueryBudget">The most bounds queries the sweep may spend, at least one.</param>
    /// <param name="contactTolerance">The clearance, at least zero, at or below which the sweep stops in
    /// <see cref="CertifiedSweepOutcome.Contact"/> rather than creeping on toward a surface it approaches ever more
    /// slowly; zero stops only where no clearance is proved. Stopping sooner keeps only ground already proved.</param>
    /// <param name="sweep">The certified fraction, the outcome and the queries spent on success.</param>
    /// <returns><see langword="false"/> when the field has nothing to answer against or the origin leaves its
    /// frame.</returns>
    bool TryCertifiedSweep(FixedPosition origin, FixedVector3 displacement, FixedQ4816 radius, int boundsQueryBudget, FixedQ4816 contactTolerance, out CertifiedSweep sweep);
}
