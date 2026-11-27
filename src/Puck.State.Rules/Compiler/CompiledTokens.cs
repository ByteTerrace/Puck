using System.Collections;

namespace Puck.State.Rules;

/// <summary>A compiled postfix token sequence that evaluates as the closure tree it lowers to: the tokens are what
/// every analysis reads (dataflow, bounds, prices, deadlines, disclosure, read-back), and the tree, built from them
/// alone, is what evaluation runs. <see cref="CompiledProgram"/> and <see cref="CompiledGate"/> are its two kinds.</summary>
/// <typeparam name="TToken">The token type.</typeparam>
/// <remarks>The tree is built at the first evaluation and kept, so the tokens must not change after it: reading them is
/// free, and a caller that rewrites tokens between evaluations evaluates a span instead.</remarks>
public abstract class CompiledTokens<TToken> : IReadOnlyList<TToken> where TToken : struct {
    private readonly TToken[] m_tokens;

    /// <summary>Initializes a new instance of the <see cref="CompiledTokens{TToken}"/> class.</summary>
    /// <param name="tokens">The postfix tokens.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tokens"/> is <see langword="null"/>.</exception>
    protected CompiledTokens(TToken[] tokens) {
        ArgumentNullException.ThrowIfNull(argument: tokens);

        m_tokens = tokens;
    }

    /// <summary>Gets how many tokens the sequence holds.</summary>
    public int Count => m_tokens.Length;
    /// <summary>Gets how many tokens the sequence holds.</summary>
    public int Length => m_tokens.Length;
    /// <summary>Gets the postfix tokens. Writing them after the first evaluation leaves the tree stale.</summary>
    public TToken[] Tokens => m_tokens;

    /// <summary>Gets one token.</summary>
    /// <param name="index">The token's position.</param>
    public TToken this[int index] => m_tokens[index];

    /// <summary>Returns the tokens as a span.</summary>
    /// <returns>The span.</returns>
    public ReadOnlySpan<TToken> AsSpan() => m_tokens;
    /// <summary>Returns a span over part of the tokens.</summary>
    /// <param name="start">The first token.</param>
    /// <param name="length">How many tokens.</param>
    /// <returns>The span.</returns>
    public ReadOnlySpan<TToken> AsSpan(int start, int length) => m_tokens.AsSpan(start: start, length: length);
    /// <summary>Returns the span enumerator over the tokens, so a <see langword="foreach"/> reads them by reference.</summary>
    /// <returns>The enumerator.</returns>
    public ReadOnlySpan<TToken>.Enumerator GetEnumerator() => ((ReadOnlySpan<TToken>)m_tokens).GetEnumerator();
    /// <summary>Copies the tokens.</summary>
    /// <returns>A new array.</returns>
    public TToken[] ToArray() => [.. m_tokens];

    IEnumerator<TToken> IEnumerable<TToken>.GetEnumerator() => ((IEnumerable<TToken>)m_tokens).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => m_tokens.GetEnumerator();
}
