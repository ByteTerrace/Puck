using System.Runtime.CompilerServices;

namespace Puck.State;

/// <summary>
/// Allocation-free Boolean stack program operations shared across rule gates and physics body action gates.
/// </summary>
public static class GateProgramEvaluator {
    /// <summary>
    /// Folds the preceding <paramref name="arity"/> Boolean results on <paramref name="stack"/> into one conjoined
    /// (<paramref name="isAll"/> is <see langword="true"/>) or disjoined (<paramref name="isAll"/> is <see langword="false"/>) result.
    /// </summary>
    /// <param name="stack">The Boolean evaluation stack.</param>
    /// <param name="top">The stack pointer, updated to index one past the folded result.</param>
    /// <param name="arity">The number of operands to consume.</param>
    /// <param name="isAll"><see langword="true"/> for <c>all</c> (conjunction); <see langword="false"/> for <c>any</c> (disjunction).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FoldGroup(Span<bool> stack, ref int top, int arity, bool isAll) {
        var start = (top - arity);
        var result = isAll;

        for (var index = start; (index < top); index++) {
            result = (isAll
                ? (result && stack[index])
                : (result || stack[index])
            );
        }

        top = start;
        stack[top++] = result;
    }
    /// <summary>
    /// Inverts the top value on <paramref name="stack"/>.
    /// </summary>
    /// <param name="stack">The Boolean evaluation stack.</param>
    /// <param name="top">The current stack pointer (inverts <c>stack[top - 1]</c>).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Invert(Span<bool> stack, int top) {
        stack[(top - 1)] = !stack[(top - 1)];
    }
}
