using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>How a group of a KIND distributes ownership of something it collectively acquires — the loot/ownership-
/// distribution policy slot every <see cref="WorldGroupKind"/> declares. Consulted by a LATER lane once an ownable
/// subject exists (<see cref="WorldOwnership"/> is the type that lane consumes); this head establishes the vocabulary
/// and validates it, and does not yet distribute anything.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldGroupOwnershipPolicy>))]
public enum WorldGroupOwnershipPolicy : byte {
    /// <summary>No collective acquisition — a group of this kind never becomes an owner.</summary>
    None,

    /// <summary>A designated leader role decides distribution.</summary>
    LeaderDecides,

    /// <summary>Distribution rotates across the current membership in join order.</summary>
    RoundRobin,

    /// <summary>Any member may claim, first come first served.</summary>
    FreeForAll,
}

/// <summary>Whether a RUNTIME group of a kind survives losing its last member — the lifetime/persistence policy slot
/// every <see cref="WorldGroupKind"/> declares. Never consulted for the AUTHORED roster: an authored row is re-seeded
/// from the document on every boot/<c>world.reset</c> regardless of this field, so it can never dissolve out from
/// under the document that declares it.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldGroupLifetime>))]
public enum WorldGroupLifetime : byte {
    /// <summary>A runtime group of this kind auto-dissolves the moment <see cref="WorldMutation.LeaveGroup"/> or
    /// <see cref="WorldMutation.KickMember"/> empties it (only ever checked once it HAD at least one member — forming
    /// an empty group never dissolves it).</summary>
    Ephemeral,

    /// <summary>A runtime group of this kind survives at zero members until explicitly removed.</summary>
    Persistent,
}

/// <summary>What happens to a seated member's row on <see cref="WorldMutation.KickMember"/> — the KIND decides, never
/// an engine default. Never consulted for <see cref="WorldMutation.LeaveGroup"/> (voluntary self-departure always
/// just removes the one row).</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldGroupEvictionPolicy>))]
public enum WorldGroupEvictionPolicy : byte {
    /// <summary>Only the kicked member's row is removed; the group persists (subject to
    /// <see cref="WorldGroupLifetime"/> if that empties it).</summary>
    Remove,

    /// <summary>Kicking ANY member removes the WHOLE group row immediately — every other membership goes with it.</summary>
    Disband,
}

/// <summary>One named role a <see cref="WorldGroupKind"/> declares — the role→capability half of the kind's policy
/// bundle. A role names nothing about MEMBERSHIP (this head keeps a membership row role-less; see
/// <see cref="WorldGroup.Members"/>); it exists so <c>world.grant</c> can refuse, at the door, a hold over a group
/// principal that no role of its kind could ever exercise (the addon-reachability-honesty analog: an admitted-but-
/// unreachable grant is a grant that lies).</summary>
/// <param name="Name">The role's stable name, unique within its kind.</param>
/// <param name="Capabilities">The capabilities a member acting under this role may be granted through the group
/// principal. Never empty — a role reaching nothing is a role that could not exist without lying about what it is
/// for.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGroupRole(
    string Name,
    IReadOnlyList<WorldCapability> Capabilities
);

/// <summary>A group KIND — a POLICY BUNDLE, never a size label. Every field here must be BEHAVIOR-bearing: a kind
/// that differs from another ONLY in <see cref="Capacity"/> is a capacity VALUE, not a kind, and
/// <c>WorldDefinitionValidator</c> refuses that pair by name.</summary>
/// <param name="Name">The kind's stable name, unique within <see cref="WorldGroupsSection.Kinds"/> — the vocabulary a
/// <see cref="WorldGroup.KindName"/> reference is validated against (unknown-by-name, the same shape as the state-row
/// cell-existence refusal).</param>
/// <param name="Roles">The role→capability map (see <see cref="WorldGroupRole"/>). May be empty — a kind with no
/// roles reaches no capability at all through its group principal, so any grant naming it is refused as unreachable.</param>
/// <param name="OwnershipPolicy">The loot/ownership-distribution policy slot (see <see cref="WorldGroupOwnershipPolicy"/>).</param>
/// <param name="Lifetime">The lifetime/persistence policy (see <see cref="WorldGroupLifetime"/>) — RUNTIME groups only.</param>
/// <param name="EvictionPolicy">What a kick does to the kicked member's row — and, under
/// <see cref="WorldGroupEvictionPolicy.Disband"/>, to the whole group (see <see cref="WorldGroupEvictionPolicy"/>).</param>
/// <param name="Capacity">The maximum concurrent members a group of this kind admits — one minor authored field
/// within <see cref="WorldGroupCapacity.MaxMembersPerGroup"/>, the population ceiling this substrate is bounded
/// against.</param>
/// <param name="SharedStateScope">The name of a declared <c>state</c> row this kind's groups share, or
/// <see langword="null"/> for none. Validated to reference an EXISTING row (refused by name otherwise, the same
/// cell-existence discipline the world-rules operand walk enforces) — the deeper "every member reads/writes this row
/// together" semantic belongs to a later lane; this head only pins the reference honest.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGroupKind(
    string Name,
    IReadOnlyList<WorldGroupRole> Roles,
    WorldGroupOwnershipPolicy OwnershipPolicy,
    WorldGroupLifetime Lifetime,
    WorldGroupEvictionPolicy EvictionPolicy,
    int Capacity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SharedStateScope = null
);

/// <summary>One group ROW — a roster of principals under a kind. ONE shape whether the row was boot-authored (present
/// in the server's own base document — re-seeded on every <c>world.reset</c>/<c>.load</c>/<c>.reload</c>) or formed
/// live by <see cref="WorldMutation.FormGroup"/> (never written back to the base, so a whole-document rebuild simply
/// does not carry it forward — the party-vs-roster split falls out of the ordinary document-swap machinery, not a
/// bespoke flag on this type).</summary>
/// <param name="Id">The group's stable id, unique within <see cref="WorldGroupsSection.Groups"/> — the token
/// <see cref="WorldPrincipal.Group"/> carries as a grant principal.</param>
/// <param name="KindName">The owning kind's name — validated to reference a declared <see cref="WorldGroupKind"/>
/// (unknown-by-name).</param>
/// <param name="Members">The current membership — FLAT ONLY: every entry is a PRINCIPAL, never a group (a
/// <see cref="PrincipalKind.Group"/> entry is refused by name — a member holding another group's memberships by
/// proxy is exactly what flat membership forbids), and never <see cref="PrincipalKind.World"/>/
/// <see cref="PrincipalKind.Document"/> (neither is a real actor a hold could ever reach). Bounded by the kind's own
/// <see cref="WorldGroupKind.Capacity"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGroup(
    string Id,
    string KindName,
    IReadOnlyList<WorldPrincipal> Members
);

/// <summary>Which flavor of subject an <see cref="OwnershipSubject"/> addresses. Only <see cref="Group"/> is admitted
/// today — item/instance subjects are consumed by later lanes, which add their own case here rather than reusing or
/// widening this one.</summary>
[JsonConverter(typeof(StrictEnumConverter<OwnershipSubjectKind>))]
public enum OwnershipSubjectKind : byte {
    /// <summary>A group, by its stable id — <c>who owns/founded this group</c>.</summary>
    Group,
}

/// <summary>Which flavor of owner an <see cref="OwnershipOwner"/> is — the "principal-or-group" duality the ownership
/// binding names explicitly, distinct from and never spelled as a <see cref="WorldGrant"/> row.</summary>
[JsonConverter(typeof(StrictEnumConverter<OwnershipOwnerKind>))]
public enum OwnershipOwnerKind : byte {
    /// <summary>A single principal owns the subject.</summary>
    Principal,

    /// <summary>A group owns the subject collectively (its <see cref="WorldGroupKind.OwnershipPolicy"/> is how a
    /// later lane would distribute an acquisition among members — never consulted by this head).</summary>
    Group,

    /// <summary>The subject is held in ESCROW — the durable INTERMEDIATE OWNER a trade parks a subject in between an
    /// offer and its settlement. Escrow counts as exactly ONE owner (see <see cref="OwnershipEscrow"/>): the subject
    /// is owned by neither the offerer nor the recipient while this kind holds, never by both and never by neither.
    /// Entered only by <see cref="WorldMutation.OfferOwnership"/> and left only by
    /// <see cref="WorldMutation.SettleOwnership"/> (an accept by the named recipient, or a reclaim to the named
    /// offerer once the deadline passes — see that mutation's remarks).</summary>
    Escrow,
}

/// <summary>The ESCROW payload for <see cref="OwnershipOwnerKind.Escrow"/> — who offered, who may accept, and the
/// tick past which the offerer alone may reclaim. This is the "durable intermediate owner": while a subject's
/// <see cref="OwnershipOwner"/> carries this record, the subject is owned by the escrow row itself, not by either
/// named party — the recovery-by-timeout half of the escrow/transfer lane (see <see cref="WorldMutation.SettleOwnership"/>).</summary>
/// <param name="Offerer">The principal that placed the subject into escrow — the sole reclaim beneficiary.</param>
/// <param name="Recipient">The principal named to accept the subject — the sole accept beneficiary. Never equal to
/// <paramref name="Offerer"/> (refused by name — an offer to oneself is not a trade).</param>
/// <param name="DeadlineTick">The server tick at or after which <see cref="WorldMutation.SettleOwnership"/>'s reclaim
/// admits — the SAME tick unit <see cref="WorldStateAdvance.EpochTick"/> already rides. Before this tick, only an
/// accept by <see cref="Recipient"/> can resolve the escrow.</param>
public readonly record struct OwnershipEscrow(
    WorldPrincipal Offerer,
    WorldPrincipal Recipient,
    long DeadlineTick
);

/// <summary>The OWNED thing — today, exclusively a group (see <see cref="OwnershipSubjectKind"/>'s remarks). A later
/// lane widens <see cref="Kind"/> to item/instance subjects without reshaping this type.</summary>
/// <param name="Kind">The subject flavor.</param>
/// <param name="Id">The subject's stable id — a group id for <see cref="OwnershipSubjectKind.Group"/>.</param>
public readonly record struct OwnershipSubject(OwnershipSubjectKind Kind, string Id) {
    /// <summary>Describes a short stable label for console echoes — <c>group:&lt;id&gt;</c> today, the only declared
    /// <see cref="OwnershipSubjectKind"/>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => Kind switch {
        OwnershipSubjectKind.Group => $"group:{Id}",
        _ => "?",
    };

    /// <summary>Parses a subject token (<c>group:&lt;id&gt;</c>) — shared by the world.ownership.* console
    /// verbs, the same typed-token discipline <see cref="WorldPrincipal.TryParse"/> and <c>GrantSubject</c>'s own
    /// converter already follow. A future item/instance subject kind adds its own prefix here rather than reusing
    /// this one.</summary>
    /// <param name="token">The token to parse.</param>
    /// <param name="subject">The parsed subject, on success.</param>
    /// <returns><see langword="true"/> when the token parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> token, out OwnershipSubject subject) {
        subject = default;

        if (token.StartsWith(value: "group:", comparisonType: StringComparison.OrdinalIgnoreCase) && (token.Length > 6)) {
            subject = new OwnershipSubject(Kind: OwnershipSubjectKind.Group, Id: token[6..].ToString());

            return true;
        }

        return false;
    }
}

/// <summary>The OWNER — a principal, a group, or an escrow row, never a bare grant row (ownership SEEDS/IMPLIES a
/// grant; the grant door is meant to CONSULT this type, never to spell it). Exactly one of <see cref="Principal"/>/
/// <see cref="GroupId"/>/<see cref="Escrow"/> is populated, matching <see cref="Kind"/> — the structural half of the
/// refusal obligation this type and <see cref="WorldMutation.OfferOwnership"/>/<see cref="WorldMutation.SettleOwnership"/>
/// jointly uphold: no sequence of accepted/refused submissions may leave the same item owned by two principals or by
/// none (escrow counts as one).</summary>
/// <param name="Kind">Whether the owner is a bare principal, a group, or an escrow row.</param>
/// <param name="Principal">The owning principal for <see cref="OwnershipOwnerKind.Principal"/>; <see langword="null"/>
/// otherwise. Never <see cref="PrincipalKind.Group"/> — a group owner is spelled through <see cref="GroupId"/>, not
/// this field, so the two branches never overlap.</param>
/// <param name="GroupId">The owning group's id for <see cref="OwnershipOwnerKind.Group"/>; <see langword="null"/>
/// otherwise.</param>
/// <param name="Escrow">The escrow payload for <see cref="OwnershipOwnerKind.Escrow"/>; <see langword="null"/>
/// otherwise.</param>
public readonly record struct OwnershipOwner(
    OwnershipOwnerKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPrincipal? Principal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GroupId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OwnershipEscrow? Escrow = null
);

/// <summary>One ownership BINDING — <c>subject → principal-or-group-or-escrow</c>, the second kind of the
/// group+binding substrate. A row is document-authored (boot/reset/load-seeded, like every other row in this
/// section), but its <see cref="Owner"/> moves LIVE through <see cref="WorldMutation.OfferOwnership"/> (owner ->
/// escrow) and <see cref="WorldMutation.SettleOwnership"/> (escrow -> recipient, or escrow -> offerer on timeout) —
/// the escrow/transfer lane, riding this exact row shape. There is still no mutation kind that CREATES a row from
/// nothing (only a document may declare a subject's first owner) or that widens <see cref="OwnershipSubject.Kind"/>
/// past <see cref="OwnershipSubjectKind.Group"/> — a later lane adding item/instance subjects is expected to add
/// whatever mints their first row, riding this same shape.</summary>
/// <param name="Subject">The owned thing.</param>
/// <param name="Owner">Who owns it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldOwnership(
    OwnershipSubject Subject,
    OwnershipOwner Owner
);

/// <summary>Capacity constants for the group+membership substrate — made-up, sensible fixture ceilings (this is a
/// generic engine primitive; a genre world authors its own kind names and members, never a size drawn from a specific
/// game's vocabulary).</summary>
public static class WorldGroupCapacity {
    /// <summary>The maximum declared group kinds a document may carry.</summary>
    public const int MaxKinds = 32;

    /// <summary>The maximum live group rows (authored + runtime combined) at once.</summary>
    public const int MaxGroups = 128;

    /// <summary>The maximum members a single group may hold — the ceiling a kind's own
    /// <see cref="WorldGroupKind.Capacity"/> is bounded within.</summary>
    public const int MaxMembersPerGroup = 64;
}

/// <summary>The <c>groups</c> document section — the group+membership binding substrate's document shape. OPTIONAL
/// (like <c>rules</c>): a document declaring none carries a <see langword="null"/> section here rather than an empty
/// one, so adding this section never refuses an existing world at boot. <see cref="Kinds"/> and the AUTHORED rows
/// inside <see cref="Groups"/> are STANDING data (re-seeded on every boot/<c>world.reset</c>); RUNTIME rows
/// <see cref="WorldMutation.FormGroup"/> adds are wiped the same way every other live-only edit is — see
/// <see cref="WorldGroup"/>'s own remarks.</summary>
/// <param name="Kinds">The declared kind catalog.</param>
/// <param name="Groups">The group roster — authored and runtime rows in ONE list (see <see cref="WorldGroup"/>).</param>
/// <param name="Ownership">The ownership bindings (see <see cref="WorldOwnership"/>). May be empty.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGroupsSection(
    IReadOnlyList<WorldGroupKind> Kinds,
    IReadOnlyList<WorldGroup> Groups,
    IReadOnlyList<WorldOwnership> Ownership
) {
    /// <summary>Gets the empty section — every mutation composer's fallback for a document that declared no
    /// <c>groups</c> section at all (<c>current.Groups ?? Empty</c>, the identical pattern <c>rules</c> uses).</summary>
    public static WorldGroupsSection Empty { get; } = new(Kinds: [], Groups: [], Ownership: []);
}
