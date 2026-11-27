using System.Collections;
using System.Runtime.CompilerServices;

namespace Puck.State.Rules;

/// <summary>A compiled postfix Boolean gate: its tokens, which every analysis reads (dataflow, deadlines, prices,
/// read-back), and the closure tree <see cref="RuleEvaluation.Decide"/> runs, built from those tokens at the gate's first
/// decision and kept. A gate's comparisons and the programs on their sides lower to the same nodes as an expression
/// program (<see cref="CompiledProgram"/>), so one compiled path decides every gate and evaluates every program.</summary>
/// <remarks>The tokens must not change once the gate has decided: the tree is not rebuilt. It is published with an
/// interlocked exchange; two threads building it at once build the same tree and one is kept.</remarks>
[CollectionBuilder(builderType: typeof(CompiledGate), methodName: nameof(Create))]
public sealed class CompiledGate : IReadOnlyList<GateToken> {
    private readonly GateToken[] m_tokens;
    private ProgramNode? m_root;

    /// <summary>Initializes a new instance of the <see cref="CompiledGate"/> class.</summary>
    /// <param name="tokens">The postfix tokens; empty means "always".</param>
    /// <exception cref="ArgumentNullException"><paramref name="tokens"/> is <see langword="null"/>.</exception>
    public CompiledGate(GateToken[] tokens) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        m_tokens = tokens;
    }

    /// <summary>Gets the gate with no tokens, which is always open.</summary>
    public static CompiledGate Always { get; } = new(tokens: []);

    /// <summary>Gets how many tokens the gate holds.</summary>
    public int Count => m_tokens.Length;
    /// <summary>Gets how many tokens the gate holds.</summary>
    public int Length => m_tokens.Length;
    /// <summary>Gets the postfix tokens. Writing them after the gate decided leaves its tree stale.</summary>
    public GateToken[] Tokens => m_tokens;

    /// <summary>Gets one token.</summary>
    /// <param name="index">The token's position.</param>
    public GateToken this[int index] => m_tokens[index];

    /// <summary>Builds a gate from a token span (the collection-expression builder).</summary>
    /// <param name="tokens">The tokens.</param>
    /// <returns>The gate.</returns>
    public static CompiledGate Create(ReadOnlySpan<GateToken> tokens) => (tokens.IsEmpty
        ? Always
        : new(tokens: tokens.ToArray()));

    /// <summary>Reads a gate's tokens.</summary>
    /// <param name="gate">The gate, or <see langword="null"/>.</param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(parameterName: nameof(gate))]
    public static implicit operator GateToken[]?(CompiledGate? gate) => gate?.m_tokens;
    /// <summary>Reads a gate's tokens as a span.</summary>
    /// <param name="gate">The gate, or <see langword="null"/> for the empty span.</param>
    public static implicit operator ReadOnlySpan<GateToken>(CompiledGate? gate) => (gate?.m_tokens ?? []);

    /// <summary>Returns the span enumerator over the tokens, so a <see langword="foreach"/> reads them by reference.</summary>
    /// <returns>The enumerator.</returns>
    public ReadOnlySpan<GateToken>.Enumerator GetEnumerator() => ((ReadOnlySpan<GateToken>)m_tokens).GetEnumerator();

    IEnumerator<GateToken> IEnumerable<GateToken>.GetEnumerator() => ((IEnumerable<GateToken>)m_tokens).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => m_tokens.GetEnumerator();

    // The tree this gate decides as, built at its first decision and kept.
    internal ProgramNode Root() {
        if (Volatile.Read(location: ref m_root) is { } root) {
            return root;
        }

        var built = ProgramClosures.BuildGate(gate: m_tokens);

        return (Interlocked.CompareExchange(comparand: null, location1: ref m_root, value: built) ?? built);
    }
}
