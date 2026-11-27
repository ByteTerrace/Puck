using System.Runtime.CompilerServices;

namespace Puck.State.Rules;

/// <summary>A compiled postfix expression program, evaluated by <see cref="RuleExpressions.Evaluate"/> as the closure
/// tree its tokens lower to, built once per kind the program is evaluated in and kept. The tokens are never
/// interpreted.</summary>
/// <remarks>
/// <para>The tree is derived from the tokens and nothing else, so programs whose tokens are equal evaluate equally
/// however they were built. A caller that substitutes into a token array between evaluations (the bounds enumerator,
/// the constant folder) evaluates the span instead
/// (<see cref="RuleExpressions.EvaluateDetached(IStateReader?, ReadOnlySpan{CompiledExpressionToken}, CellKind)"/>),
/// which builds a tree for that one evaluation.</para>
/// <para>A token array becomes a program only through the constructor or an explicit cast, so no evaluation wraps an
/// array, and builds a tree, per call. Each tree is published with an interlocked exchange; two threads building one
/// at once build the same tree and one is kept, so the program needs no lock.</para>
/// </remarks>
[CollectionBuilder(builderType: typeof(CompiledProgram), methodName: nameof(Create))]
public sealed class CompiledProgram : CompiledTokens<CompiledExpressionToken> {
    private static readonly int KindCount = Enum.GetValues<CellKind>().Length;

    private ProgramNode?[]? m_roots;

    /// <summary>Initializes a new instance of the <see cref="CompiledProgram"/> class.</summary>
    /// <param name="tokens">The postfix tokens.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tokens"/> is <see langword="null"/>.</exception>
    public CompiledProgram(CompiledExpressionToken[] tokens) : base(tokens: tokens) { }

    /// <summary>Gets the program with no tokens.</summary>
    public static CompiledProgram Empty { get; } = new(tokens: []);

    /// <summary>Builds a program from a token span (the collection-expression builder).</summary>
    /// <param name="tokens">The tokens.</param>
    /// <returns>The program.</returns>
    public static CompiledProgram Create(ReadOnlySpan<CompiledExpressionToken> tokens) => (tokens.IsEmpty
        ? Empty
        : new(tokens: tokens.ToArray()));

    /// <summary>Wraps a token array as a program.</summary>
    /// <param name="tokens">The tokens, or <see langword="null"/>.</param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(parameterName: nameof(tokens))]
    public static explicit operator CompiledProgram?(CompiledExpressionToken[]? tokens) => ((tokens is null)
        ? null
        : new(tokens: tokens));
    /// <summary>Reads a program's tokens.</summary>
    /// <param name="program">The program, or <see langword="null"/>.</param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(parameterName: nameof(program))]
    public static implicit operator CompiledExpressionToken[]?(CompiledProgram? program) => program?.Tokens;
    /// <summary>Reads a program's tokens as a span.</summary>
    /// <param name="program">The program, or <see langword="null"/> for the empty span.</param>
    public static implicit operator ReadOnlySpan<CompiledExpressionToken>(CompiledProgram? program) => (program?.Tokens ?? []);

    // The tree this program evaluates as in one kind, built at its first evaluation in that kind and kept.
    internal ProgramNode Root(CellKind kind) {
        var roots = (Volatile.Read(location: ref m_roots) ?? InstallRoots());
        var index = ((int)kind);

        if (((uint)index) >= ((uint)roots.Length)) {
            return ProgramClosures.Build(kind: kind, persistent: true, tokens: Tokens);
        }

        return (Volatile.Read(location: ref roots[index]) ?? Install(index: index, kind: kind, roots: roots));
    }

    private ProgramNode?[] InstallRoots() {
        var built = new ProgramNode?[KindCount];

        return (Interlocked.CompareExchange(comparand: null, location1: ref m_roots, value: built) ?? built);
    }
    private ProgramNode Install(ProgramNode?[] roots, int index, CellKind kind) {
        var built = ProgramClosures.Build(kind: kind, persistent: true, tokens: Tokens);

        return (Interlocked.CompareExchange(comparand: null, location1: ref roots[index], value: built) ?? built);
    }
}
