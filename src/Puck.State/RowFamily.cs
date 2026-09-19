using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>
/// A declared row family: one authored name standing for <paramref name="Size"/> sibling rows. By default the
/// members are named <c>&lt;name&gt;0</c> through <c>&lt;name&gt;&lt;size-1&gt;</c> and are declared consecutively
/// in the section. A family may instead declare the index set its members carry — <see cref="Indices"/>, which may
/// have gaps, so <c>Pile0</c> and <c>Pile2</c>..<c>Pile12</c> are one family — or name its member rows outright
/// through <see cref="Members"/>, which frees the members from both the naming convention and contiguity.
/// </summary>
/// <param name="Name">The family's stable name; never a row name of its own.</param>
/// <param name="Size">How many member rows the family holds, 1..<see cref="StateCapacity.MaxRows"/>. When
/// <see cref="Indices"/> or <see cref="Members"/> is declared, this is that list's length.</param>
/// <param name="Indices">The family index each member carries, in member order, or <see langword="null"/> when the
/// members are indexed <c>0</c>..<c>Size-1</c> with no gaps. A family index is what <c>&lt;family&gt;[i]</c>
/// selects on, so an index the list omits is a gap that selects no row.</param>
/// <param name="Members">The member rows' own names, in member order, or <see langword="null"/> when the members
/// are named by the <c>&lt;name&gt;&lt;index&gt;</c> convention.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateFamily(CellName Name, int Size, IReadOnlyList<int>? Indices = null, IReadOnlyList<CellName>? Members = null) {
    /// <summary>Returns the row name of the family's member at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based member position, never the family index the member carries.</param>
    /// <returns>The member row name.</returns>
    public string MemberName(int index) => (((Members is { } members) && (((uint)index) < ((uint)members.Count)))
        ? members[index].Value
        : $"{Name.Value}{FamilyIndex(index: index)}");
    /// <summary>Returns the family index the member at <paramref name="index"/> carries — the number
    /// <c>&lt;family&gt;[i]</c> selects it by.</summary>
    /// <param name="index">The zero-based member position.</param>
    /// <returns>The declared family index, or the member position when the family declares none.</returns>
    public int FamilyIndex(int index) => (((Indices is { } indices) && (((uint)index) < ((uint)indices.Count)))
        ? indices[index]
        : index);
}
/// <summary>The compiled form of a <see cref="StateFamily"/>: the catalog ordinals its member rows occupy, so a
/// live family index is a bounds check and an add rather than a name lookup.</summary>
/// <param name="Name">The family's stable name.</param>
/// <param name="FirstOrdinal">The catalog ordinal of the member at position zero.</param>
/// <param name="Count">How many member rows the family holds.</param>
/// <param name="Slots">The catalog ordinal each family index resolves to, indexed by family index and
/// <c>-1</c> at a gap, or <see langword="null"/> when the family is the contiguous <c>0</c>..<c>Count-1</c> range
/// starting at <paramref name="FirstOrdinal"/>. Never mutated after construction; equality compares it
/// element-wise.</param>
public readonly record struct RowFamily(CellName Name, int FirstOrdinal, int Count, int[]? Slots = null) {
    /// <summary>Gets the catalog ordinal one past the family's last member; meaningful only for a contiguous
    /// family.</summary>
    public int EndOrdinal => (FirstOrdinal + Count);

    /// <summary>Determines whether a catalog ordinal is one of the family's members.</summary>
    /// <param name="ordinal">The catalog ordinal.</param>
    /// <returns><see langword="true"/> when the ordinal is a member of this family.</returns>
    public bool Contains(int ordinal) {
        if (Slots is not { } slots) {
            return ((ordinal >= FirstOrdinal) && (ordinal < EndOrdinal));
        }

        for (var index = 0; (index < slots.Length); index++) {
            if (slots[index] == ordinal) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Returns the catalog ordinals of the family's members, in member order.</summary>
    /// <returns>One ordinal per member.</returns>
    public IEnumerable<int> Ordinals() {
        if (Slots is not { } slots) {
            for (var ordinal = FirstOrdinal; (ordinal < EndOrdinal); ordinal++) {
                yield return ordinal;
            }

            yield break;
        }

        for (var index = 0; (index < slots.Length); index++) {
            if (slots[index] >= 0) {
                yield return slots[index];
            }
        }
    }
    /// <summary>Attempts to resolve a family index to its catalog ordinal.</summary>
    /// <param name="index">The family index, which a gapped family may leave unfilled.</param>
    /// <param name="ordinal">The catalog ordinal on success; otherwise <c>-1</c>.</param>
    /// <returns><see langword="true"/> when the index names a member.</returns>
    public bool TryGetOrdinal(int index, out int ordinal) {
        if (Slots is { } slots) {
            if (
                (((uint)index) < ((uint)slots.Length)) &&
                (slots[index] >= 0)
            ) {
                ordinal = slots[index];

                return true;
            }
        } else if (((uint)index) < ((uint)Count)) {
            ordinal = (FirstOrdinal + index);

            return true;
        }

        ordinal = -1;

        return false;
    }
    /// <inheritdoc/>
    public bool Equals(RowFamily other) => (
        (Name == other.Name) &&
        (FirstOrdinal == other.FirstOrdinal) &&
        (Count == other.Count) &&
        ((Slots is null)
            ? (other.Slots is null)
            : ((other.Slots is { } theirs) && Slots.AsSpan().SequenceEqual(other: theirs.AsSpan())))
    );
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        value1: Name,
        value2: FirstOrdinal,
        value3: Count,
        value4: (Slots?.Length ?? -1)
    );
}
/// <summary>The compiled extent of one ownership lane: where the lane's descriptors start in the catalog and how
/// many it holds, so a store can size the lane without walking every descriptor.</summary>
/// <param name="Lane">The lane described.</param>
/// <param name="FirstOrdinal">The catalog ordinal of the lane's first descriptor, or <c>-1</c> when the lane is
/// empty.</param>
/// <param name="Count">How many descriptors the lane declares.</param>
public readonly record struct StateLaneDescriptor(StateLane Lane, int FirstOrdinal, int Count) {
    /// <summary>Gets a value indicating whether the lane declares nothing.</summary>
    public bool IsEmpty => (Count == 0);
}
