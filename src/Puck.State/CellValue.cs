using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.State;

/// <summary>
/// One cell's value as a closed union over <see cref="CellKind"/> — the single carrier a read answers with and a
/// write carries, in place of the sibling nullables a cell record spells on the wire.
/// </summary>
/// <remarks>
/// Storage is inline: one <see cref="long"/> for the numeric cases, one <see cref="string"/> reference for
/// <see cref="CellKind.Text"/>, one <see cref="ReadOnlyMemory{T}"/> for <see cref="CellKind.Vector"/>, and one
/// discriminator byte. A value case is never boxed on store, because a cell value is read on every tick;
/// <see cref="IUnion.Value"/> boxes only when something actually calls it, which is reflection and generic tooling
/// alone. The carrier stays hand-written even after the language's own union feature arrives unless that feature
/// stores value cases unboxed too.
/// <para>The <see cref="CellKind.Vector"/> case is an opaque payload: the components ride here as raw signed bytes
/// and the typed view over them belongs to whoever owns the vector space, not to this carrier.</para>
/// <para>The default carrier holds no case at all — <see cref="HasValue"/> is <see langword="false"/> and
/// <see cref="Kind"/> throws. Every accessor throws on a kind mismatch rather than answering with a neutral value
/// that would read as a real one.</para>
/// </remarks>
[Union]
[JsonConverter(typeof(CellValueJsonConverter))]
public readonly struct CellValue : IEquatable<CellValue>, IUnion {
    // The discriminator is stored as (kind + 1) so that the default carrier — every field zero — is the one value
    // carrying no case, without spending a second field on the question.
    private readonly byte m_discriminator;
    private readonly long m_number;
    private readonly string? m_text;
    private readonly ReadOnlyMemory<sbyte> m_vector;

    private CellValue(CellKind kind, long number, string? text, ReadOnlyMemory<sbyte> vector) {
        m_discriminator = checked((byte)(((byte)kind) + 1));
        m_number = number;
        m_text = text;
        m_vector = vector;
    }

    /// <summary>Gets the case this carrier holds.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds no case (see <see cref="HasValue"/>).</exception>
    public CellKind Kind => (HasValue
        ? ((CellKind)(m_discriminator - 1))
        : throw new InvalidOperationException(message: "A default CellValue holds no case; check HasValue before reading Kind.")
    );
    /// <summary>Gets whether this carrier holds a case at all. <see langword="false"/> for the default value
    /// alone.</summary>
    public bool HasValue => (m_discriminator != 0);

    object? IUnion.Value => (HasValue
        ? (Kind switch {
            CellKind.Int or CellKind.Fixed => m_number,
            CellKind.Bool => (m_number != 0L),
            CellKind.Text => m_text,
            CellKind.Vector => m_vector,
            _ => throw new InvalidOperationException(message: $"Unknown cell kind '{Kind}'."),
        })
        : null
    );

    /// <summary>Gets the <see cref="CellKind.Bool"/> case's value.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds another case, or none.</exception>
    public bool AsBool => (Require(kind: CellKind.Bool).m_number != 0L);
    /// <summary>Gets the <see cref="CellKind.Fixed"/> case's raw <c>FixedQ4816</c> bits.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds another case, or none.</exception>
    public long AsFixed => Require(kind: CellKind.Fixed).m_number;
    /// <summary>Gets the <see cref="CellKind.Int"/> case's value.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds another case, or none.</exception>
    public long AsInt => Require(kind: CellKind.Int).m_number;
    /// <summary>Gets the <see cref="CellKind.Text"/> case's text.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds another case, or none.</exception>
    public string AsText => (Require(kind: CellKind.Text).m_text ?? string.Empty);
    /// <summary>Gets the <see cref="CellKind.Vector"/> case's opaque signed 8-bit components.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds another case, or none.</exception>
    public ReadOnlyMemory<sbyte> AsVector => Require(kind: CellKind.Vector).m_vector;
    /// <summary>Gets a numeric case's stored encoding: a <see cref="CellKind.Bool"/>'s 0 or 1, a
    /// <see cref="CellKind.Fixed"/>'s raw <c>FixedQ4816</c> bits, or an <see cref="CellKind.Int"/>'s value — the one
    /// number a row's column stores, whichever of the three kinds it declares.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds a text or vector case, or none.</exception>
    public long Raw => (Kind switch {
        CellKind.Bool or CellKind.Fixed or CellKind.Int => m_number,
        _ => throw new InvalidOperationException(message: $"A {Kind} cell value carries no stored number."),
    });

    /// <summary>Creates the <see cref="CellKind.Bool"/> case.</summary>
    /// <param name="value">The boolean value.</param>
    /// <returns>The carrier.</returns>
    public static CellValue Bool(bool value) => new(
        kind: CellKind.Bool,
        number: (value
            ? 1L
            : 0L
        ),
        text: null,
        vector: default
    );
    /// <summary>Creates the <see cref="CellKind.Fixed"/> case.</summary>
    /// <param name="rawBits">The raw <c>FixedQ4816</c> bit pattern, never a decimal spelling.</param>
    /// <returns>The carrier.</returns>
    public static CellValue Fixed(long rawBits) => new(
        kind: CellKind.Fixed,
        number: rawBits,
        text: null,
        vector: default
    );
    /// <summary>Creates the <see cref="CellKind.Int"/> case.</summary>
    /// <param name="value">The whole 64-bit signed value.</param>
    /// <returns>The carrier.</returns>
    public static CellValue Int(long value) => new(
        kind: CellKind.Int,
        number: value,
        text: null,
        vector: default
    );
    /// <summary>Creates the numeric case a row's kind declares from the one number its column stores — the inverse
    /// of <see cref="Raw"/>.</summary>
    /// <param name="kind">The row's declared kind: <see cref="CellKind.Int"/>, <see cref="CellKind.Fixed"/>, or
    /// <see cref="CellKind.Bool"/>.</param>
    /// <param name="raw">The stored encoding: an int's value, a fixed's raw <c>FixedQ4816</c> bits, or a bool's
    /// number, read as <see langword="true"/> when it is nonzero.</param>
    /// <returns>The carrier.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is <see cref="CellKind.Text"/>,
    /// <see cref="CellKind.Vector"/>, or no declared kind; neither payload is a number.</exception>
    public static CellValue FromNumber(CellKind kind, long raw) => kind switch {
        CellKind.Int => Int(value: raw),
        CellKind.Fixed => Fixed(rawBits: raw),
        CellKind.Bool => Bool(value: (raw != 0L)),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: kind,
            message: $"A {kind} cell value is not built from a stored number.",
            paramName: nameof(kind)
        ),
    };
    /// <summary>Creates the <see cref="CellKind.Text"/> case.</summary>
    /// <param name="value">The text; <see langword="null"/> is carried as the empty string.</param>
    /// <returns>The carrier.</returns>
    public static CellValue Text(string? value) => new(
        kind: CellKind.Text,
        number: 0L,
        text: (value ?? string.Empty),
        vector: default
    );
    /// <summary>Creates the <see cref="CellKind.Vector"/> case.</summary>
    /// <param name="components">The opaque signed 8-bit components. The carrier keeps the memory as handed to it
    /// and never copies it, so the caller owns the lifetime of what it points at.</param>
    /// <returns>The carrier.</returns>
    public static CellValue Vector(ReadOnlyMemory<sbyte> components) => new(
        kind: CellKind.Vector,
        number: 0L,
        text: null,
        vector: components
    );
    /// <summary>Parses a human-authored wire token into the case a row's kind declares.</summary>
    /// <param name="kind">The destination row's declared kind.</param>
    /// <param name="token">The wire token exactly as typed.</param>
    /// <param name="value">The parsed carrier on success; otherwise the carrier holding no case.</param>
    /// <param name="reason">Why the token was refused, or empty on success.</param>
    /// <param name="symbols">The enum the destination row draws its cells from, or <see langword="null"/> for a row
    /// that names none; an Int token may then name one of its members.</param>
    /// <returns><see langword="true"/> when the token parsed under <paramref name="kind"/>'s grammar.</returns>
    /// <remarks>The grammar is the authored one, never the stored encoding: a decimal spelling for
    /// <see cref="CellKind.Fixed"/> (never raw <c>FixedQ4816</c> bits), <c>true</c>/<c>false</c> for
    /// <see cref="CellKind.Bool"/>, and a plain integer literal, or a member name of
    /// <paramref name="symbols"/>, for <see cref="CellKind.Int"/>.
    /// <see cref="CellKind.Text"/> and <see cref="CellKind.Vector"/> carry their own payloads and are refused by
    /// name here rather than guessed at.
    /// <para>A row's kind is resolved against the candidate the write composes against, never at the verb that
    /// typed the token: the row this token targets may not exist yet when it is typed.</para></remarks>
    public static bool TryParse(CellKind kind, string token, out CellValue value, out string reason, StateEnum? symbols = null) {
        switch (kind) {
            case CellKind.Vector:
                value = default;
                reason = "vector-kind row takes a vector operand, never a numeric one";

                return false;
            case CellKind.Text:
                value = default;
                reason = "text-kind row takes a text operand, never a numeric one";

                return false;
            case CellKind.Fixed:
                if (FixedQ4816.TryParse(
                    s: token,
                    provider: CultureInfo.InvariantCulture,
                    result: out var fixedValue
                )) {
                    reason = string.Empty;
                    value = Fixed(rawBits: fixedValue.Value);

                    return true;
                }

                value = default;
                reason = $"'{token}' is not a decimal value (e.g. \"12.5\")";

                return false;
            case CellKind.Bool:
                if (bool.TryParse(
                    result: out var boolValue,
                    value: token
                )) {
                    reason = string.Empty;
                    value = Bool(value: boolValue);

                    return true;
                }

                value = default;
                reason = $"'{token}' is not 'true' or 'false'";

                return false;
            default:
                if (long.TryParse(
                    s: token,
                    style: NumberStyles.Integer,
                    provider: CultureInfo.InvariantCulture,
                    result: out var whole
                )) {
                    reason = string.Empty;
                    value = Int(value: whole);

                    return true;
                }
                if (
                    (symbols is not null) &&
                    CellName.TryParse(
                        candidate: token,
                        name: out var member,
                        reason: out _
                    ) &&
                    symbols.TryGetValue(
                        member: member,
                        value: out var ordinal
                    )
                ) {
                    reason = string.Empty;
                    value = Int(value: ordinal);

                    return true;
                }

                value = default;
                reason = ((symbols is null)
                    ? $"'{token}' is not an integer"
                    : $"'{token}' is not an integer or a member of enum '{symbols.Name.Value}'");

                return false;
        }
    }

    /// <summary>Determines whether two carriers hold the same case with the same payload.</summary>
    /// <param name="left">The left carrier.</param>
    /// <param name="right">The right carrier.</param>
    /// <returns><see langword="true"/> when the two are equal.</returns>
    public static bool operator ==(CellValue left, CellValue right) => left.Equals(other: right);
    /// <summary>Determines whether two carriers differ in case or payload.</summary>
    /// <param name="left">The left carrier.</param>
    /// <param name="right">The right carrier.</param>
    /// <returns><see langword="true"/> when the two differ.</returns>
    public static bool operator !=(CellValue left, CellValue right) => !left.Equals(other: right);

    /// <inheritdoc/>
    public bool Equals(CellValue other) {
        if (m_discriminator != other.m_discriminator) {
            return false;
        }

        if (!HasValue) {
            return true;
        }

        return (Kind switch {
            CellKind.Text => string.Equals(
                a: m_text,
                b: other.m_text,
                comparisonType: StringComparison.Ordinal
            ),
            CellKind.Vector => m_vector.Span.SequenceEqual(other: other.m_vector.Span),
            _ => (m_number == other.m_number),
        });
    }
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is CellValue other) && Equals(other: other));
    /// <inheritdoc/>
    /// <remarks>The text and vector cases fold per-process randomized hashes, so this value is for dictionaries
    /// only and never reaches a state hash; a hash that must agree across runs folds the stored columns
    /// themselves.</remarks>
    public override int GetHashCode() {
        if (!HasValue) {
            return 0;
        }

        var payload = (Kind switch {
            CellKind.Text => (m_text?.GetHashCode(comparisonType: StringComparison.Ordinal) ?? 0),
            CellKind.Vector => VectorHash(components: m_vector.Span),
            _ => m_number.GetHashCode(),
        });

        return HashCode.Combine(
            value1: m_discriminator,
            value2: payload
        );
    }
    /// <inheritdoc/>
    public override string ToString() => (HasValue
        ? (Kind switch {
            CellKind.Bool => $"Bool({(m_number != 0L)})",
            CellKind.Text => $"Text(\"{m_text}\")",
            CellKind.Vector => $"Vector[{m_vector.Length}]",
            _ => $"{Kind}({m_number})",
        })
        : "none"
    );
    /// <summary>Reads the carried payload as <typeparamref name="T"/> when the case stores exactly that type —
    /// <see cref="long"/> for <see cref="CellKind.Int"/> and <see cref="CellKind.Fixed"/>, <see cref="bool"/> for
    /// <see cref="CellKind.Bool"/>, <see cref="string"/> for <see cref="CellKind.Text"/>, and
    /// <c>ReadOnlyMemory&lt;sbyte&gt;</c> for <see cref="CellKind.Vector"/>.</summary>
    /// <typeparam name="T">The payload type to read.</typeparam>
    /// <param name="value">The payload on success; otherwise the default.</param>
    /// <returns><see langword="true"/> when this carrier's case stores <typeparamref name="T"/>.</returns>
    public bool TryGetValue<T>([MaybeNullWhen(false)] out T value) {
        // Each arm has already proved typeof(T) is the payload's own type, so the reinterpretation below is a
        // no-op reread of the same bytes — it exists so a value case is handed back without the box that
        // (T)(object)payload would allocate on every tick.
        if (HasValue) {
            switch (Kind) {
                case CellKind.Int:
                case CellKind.Fixed:
                    if (typeof(T) == typeof(long)) {
                        var number = m_number;

                        value = Unsafe.As<long, T>(source: ref number);

                        return true;
                    }

                    break;
                case CellKind.Bool:
                    if (typeof(T) == typeof(bool)) {
                        var flag = (m_number != 0L);

                        value = Unsafe.As<bool, T>(source: ref flag);

                        return true;
                    }

                    break;
                case CellKind.Text:
                    if (typeof(T) == typeof(string)) {
                        var text = (m_text ?? string.Empty);

                        value = Unsafe.As<string, T>(source: ref text);

                        return true;
                    }

                    break;
                case CellKind.Vector:
                    if (typeof(T) == typeof(ReadOnlyMemory<sbyte>)) {
                        var components = m_vector;

                        value = Unsafe.As<ReadOnlyMemory<sbyte>, T>(source: ref components);

                        return true;
                    }

                    break;
                default:
                    break;
            }
        }

        value = default;

        return false;
    }

    private static int VectorHash(ReadOnlySpan<sbyte> components) {
        var hash = new HashCode();

        hash.AddBytes(value: System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: components));

        return hash.ToHashCode();
    }
    private CellValue Require(CellKind kind) => ((HasValue && (Kind == kind))
        ? this
        : throw new InvalidOperationException(message: (HasValue
            ? $"A CellValue holding {Kind} was read as {kind}."
            : $"A default CellValue holds no case and cannot be read as {kind}."
        ))
    );
}
