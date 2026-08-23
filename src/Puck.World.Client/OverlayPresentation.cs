using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Client;

/// <summary>Evaluates <see cref="OverlayPredicate"/>s for a local seat — the seam every ranked-candidate reader
/// (a HUD frame element's sources, a camera's anchor list) selects through, so the evaluator and the thing it gates
/// can live in different assemblies.</summary>
public interface IOverlayPredicateEvaluator {
    /// <summary>Evaluates a predicate for one local seat; a <see langword="null"/> predicate is true.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    /// <param name="predicate">The predicate, or <see langword="null"/>.</param>
    bool Evaluate(int slot, OverlayPredicate? predicate);
    /// <summary>Evaluates a predicate for the world scope: true when it holds for any joined local seat.</summary>
    /// <param name="predicate">The predicate, or <see langword="null"/>.</param>
    bool EvaluateAnySeat(OverlayPredicate? predicate);
}
/// <summary>The recency presence curve a windowed fact reads (<see cref="OverlayPredicate.Recently"/>,
/// <see cref="OverlayPredicate.Speaking"/>): 1 through the window after the fact last held, easing to 0 across the
/// fade, 0 once the fade has elapsed. Ticks are the time base; seconds enter only through the rate.</summary>
public static class OverlayRecency {
    /// <summary>Returns the presence of a fact that last held on <paramref name="lastHeldTick"/> as of
    /// <paramref name="completedTick"/>.</summary>
    /// <param name="completedTick">The current completed simulation tick.</param>
    /// <param name="lastHeldTick">The tick the fact last held on; 0 means never.</param>
    /// <param name="rateHz">The simulation rate; a non-positive rate reads 0 presence.</param>
    /// <param name="windowSeconds">How long after <paramref name="lastHeldTick"/> presence stays 1.</param>
    /// <param name="fadeSeconds">How long after the window presence eases to 0; 0 cuts.</param>
    public static float Presence(ulong completedTick, ulong lastHeldTick, int rateHz, float windowSeconds, float fadeSeconds) {
        if (
            (rateHz <= 0) ||
            (lastHeldTick == 0UL) ||
            (completedTick < lastHeldTick)
        ) {
            return 0f;
        }

        // The tick delta stays exact before the conversion: a float loses single-tick resolution after roughly
        // 19 hours at 240 Hz.
        var elapsedSeconds = (((double)(completedTick - lastHeldTick)) / rateHz);

        if (elapsedSeconds < windowSeconds) {
            return 1f;
        }

        if (fadeSeconds <= 0f) {
            return 0f;
        }

        return (float)Math.Clamp(
            max: 1.0,
            min: 0.0,
            value: (1.0 - ((elapsedSeconds - windowSeconds) / fadeSeconds))
        );
    }
}
/// <summary>Evaluates an <see cref="OverlayPredicate.State"/> against a live document: text rows compare ordinally,
/// every other row compares as <see cref="FixedQ4816"/> through <see cref="ActionStateComparisons.Holds(ActionStateComparison, FixedQ4816, FixedQ4816)"/>.</summary>
public static class OverlayStateComparison {
    /// <summary>Returns whether the predicate holds as of <paramref name="tick"/>. An unparseable binding, an
    /// undeclared row, or an absent cell reads false.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="state">The predicate.</param>
    /// <param name="tick">The tick an advancing row's value is computed at.</param>
    public static bool Holds(WorldDefinition definition, OverlayPredicate.State state, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: state);

        if (
            !BindableState.TryParseBinding(
            key: out var key,
            row: out var rowName,
            value: state.Binding
        ) ||
            !WorldStateReader.TryRead(
            definition: definition,
            key: key,
            rawValue: out var rawValue,
            row: out var row,
            rowName: rowName,
            text: out var text,
            tick: tick
        )
        ) {
            return false;
        }

        if (row.Kind == CellKind.Text) {
            if (state.Text is not { } expectedText) {
                return false;
            }

            var equal = string.Equals(
                a: text,
                b: expectedText,
                comparisonType: StringComparison.Ordinal
            );

            return (state.Comparison == ActionStateComparison.NotEqual) ? !equal : equal;
        }

        if ((rawValue is not { } raw) || (state.Value is not { } expected)) {
            return false;
        }

        var value = ((row.Kind == CellKind.Fixed)
            ? FixedQ4816.FromRawBits(value: raw)
            : FixedQ4816.FromDouble(value: raw)
        );

        return state.Comparison.Holds(
            expected: FixedQ4816.FromDouble(value: expected),
            value: value
        );
    }
}
/// <summary>Selects the first holding candidate of a ranked list — the one rule a HUD frame element's
/// <c>sources</c> and a camera's <c>anchors</c> both follow.</summary>
public static class OverlayRanking {
    /// <summary>Returns the index of the first candidate whose predicate holds for <paramref name="slot"/>, or -1
    /// when none does.</summary>
    /// <typeparam name="T">The candidate type.</typeparam>
    /// <param name="candidates">The candidates in rank order.</param>
    /// <param name="when">Reads a candidate's predicate (<see langword="null"/> always holds).</param>
    /// <param name="evaluator">The evaluator; <see langword="null"/> treats every predicate as holding.</param>
    /// <param name="slot">The 0-based local seat, or -1 for the world scope (any joined seat).</param>
    public static int FirstHolding<T>(IReadOnlyList<T> candidates, Func<T, OverlayPredicate?> when, IOverlayPredicateEvaluator? evaluator, int slot) {
        ArgumentNullException.ThrowIfNull(argument: candidates);
        ArgumentNullException.ThrowIfNull(argument: when);

        for (var index = 0; (index < candidates.Count); index++) {
            var predicate = when(arg: candidates[index]);

            if (
                (predicate is null) ||
                (evaluator is null) ||
                ((slot < 0)
                    ? evaluator.EvaluateAnySeat(predicate: predicate)
                    : evaluator.Evaluate(
                        predicate: predicate,
                        slot: slot
                    ))
            ) {
                return index;
            }
        }

        return -1;
    }
}
/// <summary>One frame element's cross-fade state: the winning key, the outgoing key while a fade runs, and the
/// mix. Advanced once per produced frame; holds no references, so a preallocated array of these costs nothing per
/// frame.</summary>
public struct OverlayFrameCrossfade {
    private double m_startSeconds;
    private bool m_seeded;

    /// <summary>Gets the winning (incoming) key, or -1.</summary>
    public int Current { get; private set; }
    /// <summary>Gets the outgoing key while a fade runs, or -1.</summary>
    public int Outgoing { get; private set; }
    /// <summary>Gets the weight of <see cref="Current"/>, 0 to 1; 1 when no fade runs.</summary>
    public float Mix { get; private set; }

    /// <summary>Advances the fade to <paramref name="nowSeconds"/> with <paramref name="winner"/> as this frame's
    /// winning key. A change of winner starts a fade from the previous winner when <paramref name="fadeSeconds"/>
    /// is positive and a previous winner existed; a change mid-fade restarts from the current winner and drops the
    /// older outgoing key. The first call seeds without fading.</summary>
    /// <param name="winner">The winning key this frame, or -1.</param>
    /// <param name="nowSeconds">The presentation clock, seconds.</param>
    /// <param name="fadeSeconds">The authored fade length; 0 cuts.</param>
    public void Advance(int winner, double nowSeconds, float fadeSeconds) {
        if (!m_seeded) {
            m_seeded = true;
            Current = winner;
            Outgoing = -1;
            Mix = 1f;

            return;
        }

        if (winner != Current) {
            if ((fadeSeconds > 0f) && (Current >= 0)) {
                Outgoing = Current;
                m_startSeconds = nowSeconds;
            } else {
                Outgoing = -1;
            }

            Current = winner;
        }

        if (Outgoing < 0) {
            Mix = 1f;

            return;
        }

        var progress = ((nowSeconds - m_startSeconds) / fadeSeconds);

        if (progress >= 1.0) {
            Outgoing = -1;
            Mix = 1f;
        } else {
            Mix = (float)Math.Max(val1: 0.0, val2: progress);
        }
    }
}
