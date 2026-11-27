using System.Collections.Frozen;

namespace Puck.Transpiler.Lowering;

/// <summary>The folds over a list known at compile time (<c>sum</c>, <c>count</c>, <c>any</c>, <c>all</c>,
/// <c>minimum</c>, <c>maximum</c> and <c>first</c>), and the one description of the expression each stands for. A rule
/// operand writes that expression out over its elements' bodies, so a fold costs exactly what the expression written
/// out by hand costs; a document value evaluates it step by step over its elements' values, in the order the
/// written-out expression evaluates, so no evaluation nests deeper as the list grows.</summary>
/// <remarks>Each fold is the left-to-right chain an author writes: <c>sum</c> is <c>t0 + t1 + …</c>, <c>count</c> the
/// same over truth values, <c>any</c> <c>t0 or t1 or …</c>, <c>all</c> <c>t0 and t1 and …</c>, <c>minimum</c> and
/// <c>maximum</c> the nested two-argument calls, and <c>first</c> the choice chain
/// <c>c0 ? v0 : (c1 ? v1 : … : otherwise)</c>. <see cref="Fold{T}"/> hands each step of that chain to a
/// <see cref="ListFoldSpelling{T}"/> innermost first, so a spelling that builds nodes gets the chain and one that
/// evaluates gets its value.</remarks>
public static class ListFolds {
    /// <summary>Gets the names a list fold is called by.</summary>
    public static FrozenSet<string> Names { get; } = new[] { "all", "any", "count", "first", "maximum", "minimum", "sum" }.ToFrozenSet(comparer: StringComparer.Ordinal);

    /// <summary>Returns whether a call has a list fold's shape: <c>first(list, condition, otherwise)</c>,
    /// <c>first(list, condition, value, otherwise)</c>, or any other fold's <c>name(list, item =&gt; term)</c>. The lambda
    /// is what tells a fold from a call of the same name that is a document construct (a gate's <c>all</c>) or a
    /// scalar (<c>minimum(a, b)</c>); whether the first argument is a list known at compile time is the caller's to
    /// judge.</summary>
    /// <param name="name">The call's name.</param>
    /// <param name="arguments">The number of arguments the call passes.</param>
    /// <param name="secondIsLambda">Whether the second argument is a lambda.</param>
    /// <returns><see langword="true"/> when the call is shaped as a list fold.</returns>
    public static bool Shaped(string name, int arguments, bool secondIsLambda) => (secondIsLambda && (name switch {
        "first" => (arguments is 3 or 4),
        "all" or "any" or "count" or "maximum" or "minimum" or "sum" => (arguments == 2),
        _ => false,
    }));
    /// <summary>Returns whether a fold reads its terms as truth values (<c>any</c>, <c>all</c> and <c>count</c>).</summary>
    /// <param name="name">The fold's name.</param>
    /// <returns><see langword="true"/> for a fold over truth values.</returns>
    public static bool ReadsTruth(string name) => (name is "all" or "any" or "count");

    /// <summary>Folds the terms through a spelling. Each step of the fold's chain is one call of
    /// <paramref name="spelling"/>, made innermost first (left to right for the infix and call chains, the last element
    /// first for <c>first</c>'s choice chain), so a spelling that builds nodes returns the chain written out and one
    /// that evaluates returns the chain's value without nesting.</summary>
    /// <typeparam name="T">What the spelling folds: the language's expression node, or an evaluated value.</typeparam>
    /// <param name="name">The fold's name, one of <see cref="Names"/>.</param>
    /// <param name="terms">One term per element in list order: the body's value, or for <c>first</c> its condition.</param>
    /// <param name="values">For <c>first</c>, one chosen value per element; <see langword="null"/> for every other fold.</param>
    /// <param name="otherwise">For <c>first</c>, the value when no condition holds; <see langword="null"/> otherwise.</param>
    /// <param name="spelling">The language's steps: node constructors, or evaluations.</param>
    /// <param name="refusal">Why the fold has no value, or <see langword="null"/> when it has one.</param>
    /// <returns>The folded chain, or <see langword="null"/> with <paramref name="refusal"/> set when <c>minimum</c> or
    /// <c>maximum</c> folds an empty list, which has no value.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a fold, or <c>first</c> lacks its values or
    /// its otherwise.</exception>
    public static T? Fold<T>(string name, IReadOnlyList<T> terms, IReadOnlyList<T>? values, T? otherwise, ListFoldSpelling<T> spelling, out string? refusal) where T : class {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(spelling);
        refusal = null;

        switch (name) {
            case "sum":
                return Chain(terms: terms, empty: 0m, combine: (left, right) => spelling.Binary(left: left, symbol: "+", right: right));
            case "count":
                return Chain(terms: [.. terms.Select(selector: term => Truth(term: term))], empty: 0m, combine: (left, right) => spelling.Binary(left: left, symbol: "+", right: right));
            case "any":
                return ((terms.Count == 1) ? Truth(term: terms[0]) : Chain(terms: terms, empty: 0m, combine: (left, right) => spelling.Binary(left: left, symbol: "or", right: right)));
            case "all":
                return ((terms.Count == 1) ? Truth(term: terms[0]) : Chain(terms: terms, empty: 1m, combine: (left, right) => spelling.Binary(left: left, symbol: "and", right: right)));
            case "minimum" or "maximum":
                if (terms.Count == 0) {
                    refusal = $"{name} over an empty list has no value";
                    return null;
                }
                return Chain(terms: terms, empty: 0m, combine: (left, right) => spelling.Call(name: name, left: left, right: right));
            case "first": {
                    if ((values is null) || (values.Count != terms.Count) || (otherwise is null)) {
                        throw new ArgumentException(message: "first takes one value per condition and an otherwise.", paramName: nameof(values));
                    }
                    var chosen = otherwise;

                    for (var index = (terms.Count - 1); (index >= 0); index--) {
                        chosen = spelling.Choice(condition: terms[index], whenTrue: values[index], whenFalse: chosen);
                    }
                    return chosen;
                }
            default:
                throw new ArgumentException(message: $"'{name}' is not a list fold.", paramName: nameof(name));
        }

        T Truth(T term) => (spelling.IsTruth(term: term) ? term : spelling.Binary(left: term, symbol: "!=", right: spelling.Number(value: 0m)));
        T Chain(IReadOnlyList<T> terms, decimal empty, Func<T, T, T> combine) {
            if (terms.Count == 0) {
                return spelling.Number(value: empty);
            }
            var chained = terms[0];

            for (var index = 1; (index < terms.Count); index++) {
                chained = combine(arg1: chained, arg2: terms[index]);
            }
            return chained;
        }
    }
}

/// <summary>The steps a language hands <see cref="ListFolds.Fold{T}"/>: node constructors that build a fold's
/// expression in its own tree, or evaluations that compute each step's value as it is made.</summary>
/// <typeparam name="T">The language's expression node type, or its evaluated value.</typeparam>
public abstract class ListFoldSpelling<T> where T : class {
    /// <summary>Returns a number literal.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The literal.</returns>
    public abstract T Number(decimal value);
    /// <summary>Returns an infix operation.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="symbol">The operator's symbol (<c>+</c>, <c>or</c>, <c>and</c>, <c>!=</c>).</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The operation.</returns>
    public abstract T Binary(T left, string symbol, T right);
    /// <summary>Returns a two-argument call.</summary>
    /// <param name="name">The function's name (<c>minimum</c> or <c>maximum</c>).</param>
    /// <param name="left">The first argument.</param>
    /// <param name="right">The second argument.</param>
    /// <returns>The call.</returns>
    public abstract T Call(string name, T left, T right);
    /// <summary>Returns a conditional choice.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="whenTrue">The value a true condition chooses.</param>
    /// <param name="whenFalse">The value a false condition chooses.</param>
    /// <returns>The choice.</returns>
    public abstract T Choice(T condition, T whenTrue, T whenFalse);
    /// <summary>Returns whether a term already reads 1 or 0 (a comparison, <c>and</c>, <c>or</c> or <c>not</c>), so a
    /// fold over truth values takes it as written rather than comparing it with 0.</summary>
    /// <param name="term">The term.</param>
    /// <returns><see langword="true"/> when the term is a truth value.</returns>
    public abstract bool IsTruth(T term);
}
