using System.Collections;
using System.Runtime.CompilerServices;

namespace Puck.State.Rules;

/// <summary>A compiled postfix expression program: its tokens, which every analysis reads (dataflow, bounds, prices,
/// disclosure, read-back), and the closure tree that evaluation runs, built from those tokens once per kind the
/// program is evaluated in (<see cref="RuleExpressions.Evaluate"/>). The tokens are never interpreted.</summary>
/// <remarks>
/// <para>The tree is derived from the tokens and nothing else, so a program whose tokens are equal evaluates equally
/// however it was built. The tokens must not change once the program has been evaluated: the tree is not rebuilt.
/// A caller that substitutes into a token array between evaluations (the bounds enumerator, the constant folder)
/// evaluates the span instead (<see cref="RuleExpressions.EvaluateDetached(IStateReader?, ReadOnlySpan{CompiledExpressionToken}, CellKind)"/>),
/// which builds a tree for that one evaluation.</para>
/// <para>The tree is published once per kind with an interlocked exchange; two threads building it at once build
/// the same tree and one is kept, so the program needs no lock.</para>
/// </remarks>
[CollectionBuilder(builderType: typeof(CompiledProgram), methodName: nameof(Create))]
public sealed class CompiledProgram : IReadOnlyList<CompiledExpressionToken> {
    private static readonly int KindCount = (Enum.GetValues<CellKind>().Length);

    private readonly CompiledExpressionToken[] m_tokens;
    private ProgramNode?[]? m_roots;

    /// <summary>Initializes a new instance of the <see cref="CompiledProgram"/> class.</summary>
    /// <param name="tokens">The postfix tokens.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tokens"/> is <see langword="null"/>.</exception>
    public CompiledProgram(CompiledExpressionToken[] tokens) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        m_tokens = tokens;
    }

    /// <summary>Gets the program with no tokens.</summary>
    public static CompiledProgram Empty { get; } = new(tokens: []);

    /// <summary>Gets how many tokens the program holds.</summary>
    public int Count => m_tokens.Length;
    /// <summary>Gets how many tokens the program holds.</summary>
    public int Length => m_tokens.Length;
    /// <summary>Gets the postfix tokens. Reading them is free; writing them after the program was evaluated leaves
    /// its tree stale.</summary>
    public CompiledExpressionToken[] Tokens => m_tokens;

    /// <summary>Gets one token.</summary>
    /// <param name="index">The token's position.</param>
    public CompiledExpressionToken this[int index] => m_tokens[index];

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
    public static implicit operator CompiledExpressionToken[]?(CompiledProgram? program) => program?.m_tokens;
    /// <summary>Reads a program's tokens as a span.</summary>
    /// <param name="program">The program, or <see langword="null"/> for the empty span.</param>
    public static implicit operator ReadOnlySpan<CompiledExpressionToken>(CompiledProgram? program) => (program?.m_tokens ?? []);

    /// <summary>Returns the tokens as a span.</summary>
    /// <returns>The span.</returns>
    public ReadOnlySpan<CompiledExpressionToken> AsSpan() => m_tokens;
    /// <summary>Returns a span over part of the tokens.</summary>
    /// <param name="start">The first token.</param>
    /// <param name="length">How many tokens.</param>
    /// <returns>The span.</returns>
    public ReadOnlySpan<CompiledExpressionToken> AsSpan(int start, int length) => m_tokens.AsSpan(start: start, length: length);
    /// <summary>Returns the span enumerator over the tokens, so a <see langword="foreach"/> reads them by reference.</summary>
    /// <returns>The enumerator.</returns>
    public ReadOnlySpan<CompiledExpressionToken>.Enumerator GetEnumerator() => ((ReadOnlySpan<CompiledExpressionToken>)m_tokens).GetEnumerator();
    /// <summary>Copies the tokens.</summary>
    /// <returns>A new array.</returns>
    public CompiledExpressionToken[] ToArray() => [.. m_tokens];

    IEnumerator<CompiledExpressionToken> IEnumerable<CompiledExpressionToken>.GetEnumerator() => ((IEnumerable<CompiledExpressionToken>)m_tokens).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => m_tokens.GetEnumerator();

    // The tree this program evaluates as in one kind, built at its first evaluation in that kind and kept.
    internal ProgramNode Root(CellKind kind) {
        var roots = (Volatile.Read(location: ref m_roots) ?? InstallRoots());
        var index = ((int)kind);

        if (((uint)index) >= ((uint)roots.Length)) {
            return ProgramClosures.Build(kind: kind, persistent: true, tokens: m_tokens);
        }

        return (Volatile.Read(location: ref roots[index]) ?? Install(index: index, kind: kind, roots: roots));
    }

    private ProgramNode?[] InstallRoots() {
        var built = new ProgramNode?[KindCount];

        return (Interlocked.CompareExchange(comparand: null, location1: ref m_roots, value: built) ?? built);
    }
    private ProgramNode Install(ProgramNode?[] roots, int index, CellKind kind) {
        var built = ProgramClosures.Build(kind: kind, persistent: true, tokens: m_tokens);

        return (Interlocked.CompareExchange(comparand: null, location1: ref roots[index], value: built) ?? built);
    }
}
