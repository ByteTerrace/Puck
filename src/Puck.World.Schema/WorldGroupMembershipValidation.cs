using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Validates the value-level invariants shared by authored and live group membership rows. The world
/// definition validator delegates to this helper so a hand-authored document and a composed membership mutation
/// reject the same malformed local/verified union.</summary>
public static class WorldGroupMembershipValidation {
    /// <summary>Validates one membership reference and rejects a local reference that is not a real actor.</summary>
    /// <param name="member">The member to validate.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the reference is canonical and admissible.</returns>
    public static bool TryValidateMember(WorldGroupMember member, out string reason) {
        reason = string.Empty;

        if (member is null) {
            reason = "member is required";

            return false;
        }

        if (!member.Ref.IsCanonical()) {
            reason = "member ref is malformed or default";

            return false;
        }

        if (member.Ref.Kind == MemberRefKind.Local && member.Ref.Principal is { } principal && principal.Kind is not (
            PrincipalKind.Seat or PrincipalKind.Console or PrincipalKind.Addon or PrincipalKind.Peer
        )) {
            reason = $"{principal.Describe()} is not a real actor and cannot hold membership";

            return false;
        }

        if (member.JoinOrdinal < 0) {
            reason = "joinOrdinal must be non-negative";

            return false;
        }

        if ((member.Role is not null) && string.IsNullOrWhiteSpace(value: member.Role)) {
            reason = "role must be omitted or non-empty";

            return false;
        }

        if (member.Tags is { Count: 0 }) {
            reason = "tags must be omitted or non-empty";

            return false;
        }

        if (member.Tags is { Count: > 0 } tags) {
            var seenTags = new HashSet<string>(StringComparer.Ordinal);

            foreach (var tag in tags) {
                if (string.IsNullOrWhiteSpace(tag) || !seenTags.Add(tag)) {
                    reason = "tags must be non-empty and distinct";

                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Validates a group's roster capacity, reference uniqueness, and monotonic ordinal bounds.</summary>
    /// <param name="members">The roster to validate.</param>
    /// <param name="capacity">The kind's admitted capacity.</param>
    /// <param name="nextJoinOrdinal">The next ordinal to allocate; every present member must precede it.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the roster is valid.</returns>
    public static bool TryValidateRoster(
        IReadOnlyList<WorldGroupMember> members,
        int capacity,
        int nextJoinOrdinal,
        out string reason
    ) {
        reason = string.Empty;

        if (members is null) {
            reason = "members are required";

            return false;
        }

        if ((capacity < 1) || (capacity > WorldGroupCapacity.MaxMembersPerGroup)) {
            reason = $"capacity {capacity} is outside 1..{WorldGroupCapacity.MaxMembersPerGroup}";

            return false;
        }

        if ((nextJoinOrdinal < 0) || ((members.Count > 0) && (nextJoinOrdinal == 0))) {
            reason = "nextJoinOrdinal must be positive when the roster is non-empty";

            return false;
        }

        if (members.Count > capacity) {
            reason = $"roster has {members.Count} member(s), exceeding capacity {capacity}";

            return false;
        }

        var seenRefs = new HashSet<WorldMemberRef>();
        var seenOrdinals = new HashSet<int>();

        foreach (var member in members) {
            if (!TryValidateMember(member: member, reason: out reason) ||
                !seenRefs.Add(member.Ref) ||
                !seenOrdinals.Add(member.JoinOrdinal) ||
                (member.JoinOrdinal >= nextJoinOrdinal)) {
                reason = (!string.IsNullOrEmpty(reason)
                    ? reason
                    : "member references and join ordinals must be distinct and inside nextJoinOrdinal");

                return false;
            }
        }

        return true;
    }
}
