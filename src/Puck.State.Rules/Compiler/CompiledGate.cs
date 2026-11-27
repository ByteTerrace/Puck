using System.Runtime.CompilerServices;

namespace Puck.State.Rules;

/// <summary>A compiled postfix Boolean gate, decided by <see cref="RuleEvaluation.Decide"/> as the closure tree its
/// tokens lower to, built at its first decision and kept. A gate's comparisons and the programs on their sides lower
/// to the same nodes as an expression program (<see cref="CompiledProgram"/>), so one compiled path decides every
/// gate and evaluates every program. An empty gate is always open.</summary>
/// <remarks>The tree is published with an interlocked exchange; two threads building it at once build the same tree
/// and one is kept.</remarks>
[CollectionBuilder(builderType: typeof(CompiledGate), methodName: nameof(Create))]
public sealed class CompiledGate : CompiledTokens<GateToken> {
    private ProgramNode? m_root;

    /// <summary>Initializes a new instance of the <see cref="CompiledGate"/> class.</summary>
    /// <param name="tokens">The postfix tokens; empty means "always".</param>
    /// <exception cref="ArgumentNullException"><paramref name="tokens"/> is <see langword="null"/>.</exception>
    public CompiledGate(GateToken[] tokens) : base(tokens: tokens) { }

    /// <summary>Gets the gate with no tokens, which is always open.</summary>
    public static CompiledGate Always { get; } = new(tokens: []);

    /// <summary>Builds a gate from a token span (the collection-expression builder).</summary>
    /// <param name="tokens">The tokens.</param>
    /// <returns>The gate.</returns>
    public static CompiledGate Create(ReadOnlySpan<GateToken> tokens) => (tokens.IsEmpty
        ? Always
        : new(tokens: tokens.ToArray()));

    /// <summary>Reads a gate's tokens.</summary>
    /// <param name="gate">The gate, or <see langword="null"/>.</param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(parameterName: nameof(gate))]
    public static implicit operator GateToken[]?(CompiledGate? gate) => gate?.Tokens;
    /// <summary>Reads a gate's tokens as a span.</summary>
    /// <param name="gate">The gate, or <see langword="null"/> for the empty span.</param>
    public static implicit operator ReadOnlySpan<GateToken>(CompiledGate? gate) => (gate?.Tokens ?? []);

    // The tree this gate decides as, built at its first decision and kept.
    internal ProgramNode Root() {
        if (Volatile.Read(location: ref m_root) is { } root) {
            return root;
        }

        var built = ProgramClosures.BuildGate(gate: Tokens);

        return (Interlocked.CompareExchange(comparand: null, location1: ref m_root, value: built) ?? built);
    }
}
