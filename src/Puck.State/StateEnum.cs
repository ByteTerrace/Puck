using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>
/// A named closed set of symbolic values a row's integer cells may hold. Member <c>i</c> is the value <c>i</c>, so
/// the stored representation stays an ordinary integer; what the enum adds is a range a write is admitted against
/// and a name a console listing or a decompiled source prints in place of the number.
/// </summary>
/// <param name="Name">The enum's stable name, unique within the section.</param>
/// <param name="Members">The member names in value order; member <c>i</c> is the value <c>i</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateEnum(CellName Name, IReadOnlyList<CellName> Members) {
    /// <summary>Gets how many members the enum declares — the exclusive upper bound of its value range.</summary>
    [JsonIgnore]
    public int Count => Members.Count;

    /// <summary>Determines whether <paramref name="value"/> names one of the enum's members.</summary>
    /// <param name="value">The candidate stored value.</param>
    /// <returns><see langword="true"/> when the value is in range.</returns>
    public bool Admits(long value) => ((value >= 0L) && (value < Members.Count));
    /// <summary>Validates the declaration itself: at least one member, no more than
    /// <see cref="StateCapacity.MaxEnumMembers"/>, and no duplicate member name.</summary>
    /// <param name="reason">Why the declaration was refused, or empty when it is well formed.</param>
    /// <returns><see langword="true"/> when the declaration is well formed.</returns>
    public bool TryValidate(out string reason) {
        if (Members.Count == 0) {
            reason = $"State enum '{Name.Value}' declares no members.";

            return false;
        }

        if (Members.Count > StateCapacity.MaxEnumMembers) {
            reason = $"State enum '{Name.Value}' declares {Members.Count} members, past the {StateCapacity.MaxEnumMembers}-member limit.";

            return false;
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < Members.Count); index++) {
            if (!seen.Add(item: Members[index].Value)) {
                reason = $"State enum '{Name.Value}' declares duplicate member '{Members[index].Value}'.";

                return false;
            }
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Admits a write against the enum's range, refusing by name outside it.</summary>
    /// <param name="value">The stored value a write would leave behind.</param>
    /// <param name="reason">Why the value was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the value names a member.</returns>
    public bool TryAdmit(long value, out string reason) {
        if (Admits(value: value)) {
            reason = string.Empty;

            return true;
        }

        reason = $"{value} is outside enum '{Name.Value}', whose members are 0..{(Members.Count - 1)}.";

        return false;
    }
    /// <summary>Attempts to read the member name a stored value stands for.</summary>
    /// <param name="value">The stored value.</param>
    /// <param name="member">The member name on success; otherwise the default.</param>
    /// <returns><see langword="true"/> when the value names a member.</returns>
    public bool TryGetMember(long value, out CellName member) {
        if (Admits(value: value)) {
            member = Members[((int)value)];

            return true;
        }

        member = default;

        return false;
    }
    /// <summary>Attempts to read the stored value a member name stands for.</summary>
    /// <param name="member">The member name.</param>
    /// <param name="value">The stored value on success; otherwise zero.</param>
    /// <returns><see langword="true"/> when the enum declares the member.</returns>
    public bool TryGetValue(CellName member, out long value) {
        for (var index = 0; (index < Members.Count); index++) {
            if (string.Equals(
                a: Members[index].Value,
                b: member.Value,
                comparisonType: StringComparison.Ordinal
            )) {
                value = index;

                return true;
            }
        }

        value = 0L;

        return false;
    }
}
