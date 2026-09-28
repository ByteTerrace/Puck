using System.Globalization;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The debug name a device object is created with, taken from its creator's own identity: the owner (an SDF engine, a
/// graph instance, a package), the part of it the object is (a table, a pipeline, a pass), optionally a detail within
/// that part (a pass's draw pool beside its barrier pool), and, for one of several alike, its index (a frame slot).
/// Every creating member of <see cref="GpuDeviceServices"/> takes one, and <see cref="GpuObjectNaming"/> applies it
/// where the device reports object names, so a validation or debug-layer message names the object rather than printing
/// a bare handle.
/// <para>
/// A name holds references to strings its creator already owns and an integer, so building one costs nothing; the text
/// is formatted only by <see cref="ToString"/>, the one place a name becomes a string, and only when naming is on. A
/// name has no clock, counter or handle in it, so the same creator names the same object alike on every run. The
/// default value names nothing and is never applied.
/// </para>
/// </summary>
public readonly struct GpuObjectName : IEquatable<GpuObjectName> {
    /// <summary>The <see cref="Index"/> of a name that has none.</summary>
    public const int NoIndex = -1;

    /// <summary>Initializes a name.</summary>
    /// <param name="owner">The creator's identity, such as <c>sdf.world</c> or a graph instance's name.</param>
    /// <param name="part">The part of the creator the object is, such as a table's, pipeline's or pass's name.</param>
    /// <param name="detail">The object's role within <paramref name="part"/> when the part owns several objects of one
    /// kind, or <see langword="null"/>.</param>
    /// <param name="index">The object's index among several alike, such as its frame slot, or <see cref="NoIndex"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="owner"/> or <paramref name="part"/> is empty, or
    /// <paramref name="detail"/> is empty rather than <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> or <paramref name="part"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is less than <see cref="NoIndex"/>.</exception>
    public GpuObjectName(string owner, string part, string? detail = null, int index = NoIndex) {
        ArgumentException.ThrowIfNullOrEmpty(argument: owner);
        ArgumentException.ThrowIfNullOrEmpty(argument: part);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: NoIndex,
            value: index
        );

        if (detail is { Length: 0 }) {
            throw new ArgumentException(
                message: "A name's detail is absent or non-empty.",
                paramName: nameof(detail)
            );
        }

        Detail = detail;
        Index = index;
        Owner = owner;
        Part = part;
    }

    /// <summary>Gets the object's role within <see cref="Part"/>, or <see langword="null"/>.</summary>
    public string? Detail { get; }
    /// <summary>Gets the object's index among several alike, or <see cref="NoIndex"/>.</summary>
    public int Index { get; }
    /// <summary>Gets whether this is the default value, which names nothing.</summary>
    public bool IsEmpty => (Owner is null);
    /// <summary>Gets the creator's identity, or <see langword="null"/> for the default value.</summary>
    public string? Owner { get; }
    /// <summary>Gets the part of the creator the object is, or <see langword="null"/> for the default value.</summary>
    public string? Part { get; }

    /// <summary>Returns this name with an index, for one of several alike objects.</summary>
    /// <param name="index">The index, zero or more.</param>
    /// <returns>The indexed name, or the default value when this is the default value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public GpuObjectName At(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);

        if (IsEmpty) {
            return this;
        }

        return new GpuObjectName(
            detail: Detail,
            index: index,
            owner: Owner!,
            part: Part!
        );
    }
    /// <inheritdoc/>
    public bool Equals(GpuObjectName other) =>
        (
            (Index == other.Index) &&
            string.Equals(
                a: Owner,
                b: other.Owner,
                comparisonType: StringComparison.Ordinal
            ) &&
            string.Equals(
                a: Part,
                b: other.Part,
                comparisonType: StringComparison.Ordinal
            ) &&
            string.Equals(
                a: Detail,
                b: other.Detail,
                comparisonType: StringComparison.Ordinal
            )
        );
    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        ((obj is GpuObjectName other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            value1: Owner,
            value2: Part,
            value3: Detail,
            value4: Index
        );
    /// <summary>Formats the name as the device reports it: <c>owner/part</c>, then <c>/detail</c> with a detail and
    /// <c>[index]</c> with an index, and the empty string for the default value. This is the one place a name becomes
    /// text.</summary>
    /// <returns>The formatted name.</returns>
    public override string ToString() {
        if (IsEmpty) {
            return string.Empty;
        }

        if (Index == NoIndex) {
            return ((Detail is null)
                ? string.Concat(
                    str0: Owner,
                    str1: "/",
                    str2: Part
                )
                : string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{Owner}/{Part}/{Detail}"
                )
            );
        }

        return ((Detail is null)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{Owner}/{Part}[{Index}]"
            )
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{Owner}/{Part}/{Detail}[{Index}]"
            )
        );
    }

    /// <summary>Compares two names ordinally.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(GpuObjectName left, GpuObjectName right) =>
        left.Equals(other: right);
    /// <summary>Compares two names ordinally.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(GpuObjectName left, GpuObjectName right) =>
        !left.Equals(other: right);
}
