namespace Puck.State;

/// <summary>What a named expression function means to the two languages that spell it: the number of arguments it
/// takes, and the value domain it is defined over.</summary>
public enum ExpressionDomain : byte {
    /// <summary>Defined over both integers and fixed-point numbers.</summary>
    Number,
    /// <summary>Integers only — the bit, lattice and combinatorial family.</summary>
    Integer,
    /// <summary>Fixed-point only.</summary>
    Fixed,
    /// <summary>Reads two numbers, yields 1 or 0.</summary>
    Comparison,
    /// <summary>Reads one number, yields -1, 0 or 1.</summary>
    Sign,
    /// <summary>Reads a condition and two arms, yields one of the arms.</summary>
    Select,
}

/// <summary>One named function of the expression language.</summary>
/// <param name="Name">The spelling, identical in the rule language and the document language.</param>
/// <param name="Arity">How many arguments it takes.</param>
/// <param name="Domain">The value domain it is defined over.</param>
/// <param name="Operation">The operation it denotes, for a caller reaching the rule evaluator directly.</param>
public sealed record ExpressionFunction(string Name, int Arity, ExpressionDomain Domain, ExpressionOp Operation);

/// <summary>
/// The named functions of the expression language, and the ONE place either language may learn their spellings.
/// <para>Puck has two evaluators over one vocabulary: the rule language, which runs in the simulation over
/// <c>FixedQ4816</c>/<c>Int</c> cells, and the document language, which folds at compile time over the numbers a
/// document carries. They are different evaluators on purpose — one is deterministic simulation state, the other is
/// authoring arithmetic that never survives lowering — but a NAME that means two things, or means something in one
/// and nothing in the other, is a defect rather than a design. So the table lives here, beside the rule language's
/// own operator descriptors it is projected from, and the document language reads it rather than restating it.
/// </para>
/// <para>A function this names that the document language cannot fold is refused BY NAME at that spelling, never
/// reported as unknown: "the rule language evaluates <c>hexRotate</c>; the document language does not" is the true
/// statement, and it is the one an author needs.</para>
/// </summary>
public static class ExpressionVocabulary {
    /// <summary>Every named function, keyed by spelling.</summary>
    public static IReadOnlyDictionary<string, ExpressionFunction> Functions { get; } =
        ExpressionOperators.Calls.ToDictionary(
            keySelector: static entry => entry.Key,
            elementSelector: static entry => new ExpressionFunction(
                Name: entry.Key,
                Arity: entry.Value.Arity,
                Operation: entry.Value.Operation,
                Domain: entry.Value.Signature switch {
                    ExpressionSignature.Int => ExpressionDomain.Integer,
                    ExpressionSignature.Fixed => ExpressionDomain.Fixed,
                    ExpressionSignature.Comparison => ExpressionDomain.Comparison,
                    ExpressionSignature.Sign => ExpressionDomain.Sign,
                    ExpressionSignature.Select => ExpressionDomain.Select,
                    _ => ExpressionDomain.Number,
                }
            ),
            comparer: StringComparer.Ordinal);

    /// <summary>Looks up a spelling.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="function">The function on success.</param>
    /// <returns><see langword="true"/> when the vocabulary names <paramref name="name"/>.</returns>
    public static bool TryFind(string name, out ExpressionFunction? function) =>
        Functions.TryGetValue(key: name, value: out function);
}
