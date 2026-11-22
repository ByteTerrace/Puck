using System.Globalization;
using System.Runtime.CompilerServices;

namespace Puck.Abstractions;

/// <summary>
/// An interpolated refusal message that is formatted only when the check it explains fails: a method that takes a
/// condition and a message marks the message parameter with
/// <c>[InterpolatedStringHandlerArgument("condition")]</c>, and a passing check formats nothing and allocates nothing.
/// Values are formatted with the invariant culture.
/// </summary>
[InterpolatedStringHandler]
public ref struct RefusalText {
    private DefaultInterpolatedStringHandler m_inner;
    private readonly bool m_refused;

    /// <summary>Initializes a new instance of the <see cref="RefusalText"/> struct for one check.</summary>
    /// <param name="literalLength">The literal characters the message holds.</param>
    /// <param name="formattedCount">The values the message formats.</param>
    /// <param name="condition">The check: <see langword="true"/> when it passes, which formats nothing.</param>
    /// <param name="shouldAppend">Receives whether the message's parts are formatted.</param>
    public RefusalText(int literalLength, int formattedCount, bool condition, out bool shouldAppend) {
        m_refused = !condition;
        shouldAppend = m_refused;
        m_inner = (m_refused ? new DefaultInterpolatedStringHandler(literalLength: literalLength, formattedCount: formattedCount, provider: CultureInfo.InvariantCulture) : default);
    }

    /// <summary>Appends a literal part of a refused check's message.</summary>
    /// <param name="value">The literal.</param>
    public void AppendLiteral(string value) => m_inner.AppendLiteral(value: value);
    /// <summary>Appends a formatted value of a refused check's message.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    public void AppendFormatted<T>(T value) => m_inner.AppendFormatted(value: value);
    /// <summary>Returns the message, or the empty string when the check passed.</summary>
    /// <returns>The message.</returns>
    public string ToStringAndClear() => (m_refused ? m_inner.ToStringAndClear() : string.Empty);
}
