using System.Text;
using Puck.Attestation;
using Puck.World.Protocol;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Document-authored grant rows (WorldDefinition.Grants). Console and Seat principals are already canonical per
    // WorldGrantCommandModule.TryParsePrincipal's grammar. An Addon principal's name is resolved against addonNames;
    // a Peer's index is checked against the reserved peer slice (defense in depth against a programmatically
    // constructed definition, since the JSON converter's shared parser already enforces it). An exclusive 'all'
    // reservation is refused, and no two rows may name the identical (principal, capability, subject) triple —
    // unlike the ordinary idempotent re-grant a live world.grant tolerates. Whether a legitimate, non-conflicting row
    // is actually held — including Budget legitimacy — is WorldGrants.TryGrant's decision alone, made once at boot;
    // this pass does not re-derive it.
    // A group member must be a real actor: Seat/Console/Addon/Peer. Group is refused (members are flat, never
    // nested); World/Document are refused (neither is a real actor).
    private static bool IsLegitimateGroupMember(WorldPrincipal member) => (member.Kind is
        PrincipalKind.Seat or PrincipalKind.Console or PrincipalKind.Addon or PrincipalKind.Peer);
    // Whether two kinds are identical in every BEHAVIOR-BEARING field — the guard against a "size-only kind": a pair
    // differing ONLY in Capacity is a capacity VALUE, not a kind, and is refused by name below. Roles compares as an
    // ORDERED sequence of (name, capability-set) pairs — two kinds that merely declare their roles in a different
    // order are legitimately different authored data, never coalesced here.
    private static bool SameBehavior(WorldGroupKind a, WorldGroupKind b) {
        if (
            (a.Roles.Count != b.Roles.Count) ||
            (a.OwnershipPolicy != b.OwnershipPolicy) ||
            (a.Lifetime != b.Lifetime) ||
            (a.EvictionPolicy != b.EvictionPolicy) ||
            !string.Equals(
            a: a.SharedStateScope,
            b: b.SharedStateScope,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return false;
        }

        for (var index = 0; (index < a.Roles.Count); index++) {
            var left = a.Roles[index];
            var right = b.Roles[index];

            if (
                !string.Equals(
                a: left.Name,
                b: right.Name,
                comparisonType: StringComparison.Ordinal
            ) ||
                (left.Capabilities.Count != right.Capabilities.Count)
            ) {
                return false;
            }

            var rightCapabilities = new HashSet<WorldCapability>(collection: right.Capabilities);

            foreach (var capability in left.Capabilities) {
                if (!rightCapabilities.Contains(item: capability)) {
                    return false;
                }
            }
        }

        return true;
    }
    // The data-side addon descriptors: non-empty unique names, a required module pin, and no two addons declaring the
    // SAME slot (null slots are not dedup-checked, since PlayerRoster.TryClaimSlot seats an unset one at the first
    // free slot not claimed by a seat). Returns the name set — threaded forward to ValidateGrants so a
    // document-authored grant row naming addon:<name> can be resolved against what the document actually declares.
    private static HashSet<string> ValidateAddons(IReadOnlyList<WorldAddonRow> addons, int populationCapacity, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var addon in addons) {
            if (
                (addon is null) ||
                string.IsNullOrWhiteSpace(value: addon.Name)
            ) {
                errors.Add(item: "an addon requires a name.");

                continue;
            }

            if (!names.Add(item: addon.Name)) {
                errors.Add(item: $"addon name '{addon.Name}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(value: addon.ModulePath)) {
                errors.Add(item: $"addon '{addon.Name}' requires a modulePath.");
            }

            // The hash is REQUIRED — there is no no-pin sentinel any more. An unpinned module makes the state the
            // guest touches depend on a file on disk: a determinism hole first, a security one second.
            if (string.IsNullOrEmpty(value: addon.Hash)) {
                errors.Add(item: $"addon '{addon.Name}' requires a hash — an unpinned module makes authoritative state depend on a file on disk.");
            } else if (!IsValidAddonHash(hash: addon.Hash)) {
                errors.Add(item: $"addon '{addon.Name}' hash '{addon.Hash}' must match sha256-64/{{16 hex}}.");
            }

            // WorldAddonRuntime narrows this ulong to a long FuelPerTick (0 means "use the host default", mirrored
            // as null there); an unbounded value silently wraps negative on that cast, handing the guest an effectively
            // infinite budget that never traps OutOfFuel and hangs the sim-tick thread. Reject it here instead.
            if (addon.Fuel > long.MaxValue) {
                errors.Add(item: $"addon '{addon.Name}' fuel {addon.Fuel} exceeds the maximum of {long.MaxValue}.");
            }

            // The manifest — what this addon ASKS for (WorldCapabilityRequest). A request is a designation only (see
            // its own remarks): bounds-checked exactly like a document-authored grant subject, never checked for
            // whether it will actually be honored — that is Requests' whole point, decided later by the settled
            // grant table (WorldAddonRuntime's mount-time report), never here.
            if (addon.Requests is { } requests) {
                for (var index = 0; (index < requests.Count); index++) {
                    ValidateGrantSubjectBounds(
                        subject: requests[index].Subject,
                        populationCapacity: populationCapacity,
                        path: $"addon '{addon.Name}' requests[{index}]",
                        errors: errors
                    );
                }
            }

            // The machine-memory-watch rows (the fifth event family): screen/address are non-negative, length is a
            // bounded byte range that fits the wire's single i64 value lane (see WorldAddonMemoryWatch's own doc).
            if (addon.MemoryWatches is { } watches) {
                for (var index = 0; (index < watches.Count); index++) {
                    var watch = watches[index];
                    var path = $"addon '{addon.Name}' memoryWatches[{index}]";

                    if (watch.Screen < 0) {
                        errors.Add(item: $"{path}.screen {watch.Screen} must be non-negative.");
                    }

                    if (watch.Address < 0) {
                        errors.Add(item: $"{path}.address {watch.Address} must be non-negative.");
                    }

                    if (
                        (watch.Length < 1) ||
                        (watch.Length > 8)
                    ) {
                        errors.Add(item: $"{path}.length {watch.Length} must be 1..8.");
                    }
                }
            }
        }

        return names;
    }
    // The admission section: which identities/issuers the TCP door admits (WorldAdmissionDoor, Puck.World.Server's
    // WorldTcpHost), and what each is minted. Crypto-shape rules reuse Puck.Attestation's TrustListEntry.Validate()
    // directly rather than re-deriving them. Grant TEMPLATE rows are checked against the same subject-bounds/
    // exclusive-over-all rules ValidateGrants applies; Budget/exclusivity legitimacy is WorldServer.Grant's decision
    // at admission time, not this pass's.
    private static void ValidateAdmission(IReadOnlyList<WorldAdmissionEntry>? entries, int populationCapacity, List<string> errors) {
        if (entries is not { Count: > 0 } rows) {
            return;
        }

        var seen = new HashSet<(string Domain, string? Subject, WorldAdmissionTrustMode Mode)>();

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"admission[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!seen.Add(item: (row.Domain, row.Subject, row.Mode))) {
                errors.Add(item: $"{path} duplicates an earlier row naming the same domain, subject, and mode.");
            }

            if (
                (row.Subject is not null) &&
                (Encoding.UTF8.GetByteCount(s: row.Subject) > AttestationResourceLimits.TextStringUtf8Bytes)
            ) {
                errors.Add(item: $"{path}.subject exceeds the attestation-v1-base limit of {AttestationResourceLimits.TextStringUtf8Bytes} UTF-8 bytes.");
            }

            // The attestation-profile crypto shape governs rows that verify attestation claims. A 'federatedAuthority'
            // row is keyless by rule (below) and its domain is an authority namespace or the any-authority
            // wildcard, never a key fingerprint — the profile's algorithm/domain constraints cannot apply to it.
            if (row.Mode != WorldAdmissionTrustMode.FederatedAuthority) {
                if (!AttestationProfile.Base.AllowsAlgorithm(algorithm: row.Algorithm)) {
                    errors.Add(item: $"{path}.algorithm must be '{AttestationAlgorithms.EcdsaP256Sha256}' because the world admission door uses the mandatory attestation-v1-base profile.");
                }

                try {
                    var domainBytes = Convert.FromHexString(s: (row.Domain ?? string.Empty));

                    if (
                        (domainBytes.Length != 32) ||
                        !string.Equals(
                        a: row.Domain,
                        b: Convert.ToHexStringLower(bytes: domainBytes),
                        comparisonType: StringComparison.Ordinal
                    )
                    ) {
                        errors.Add(item: $"{path}.domain must be exactly 32 bytes of lowercase hexadecimal.");
                    }
                } catch (FormatException) {
                    errors.Add(item: $"{path}.domain must be exactly 32 bytes of lowercase hexadecimal.");
                }
            }

            byte[]? spki = null;

            if (row.Mode == WorldAdmissionTrustMode.FederatedAuthority) {
                if (string.IsNullOrWhiteSpace(value: row.Domain)) {
                    errors.Add(item: $"{path}.domain is required for mode 'federatedAuthority' — it names the authenticated source-authority namespace, or '{WorldAdmissionEntry.AnyAuthority}' for any of them.");
                }

                if (
                    !string.IsNullOrEmpty(value: row.Algorithm) ||
                    !string.IsNullOrEmpty(value: row.PublicKey)
                ) {
                    errors.Add(item: $"{path} carries a key for mode 'federatedAuthority' — an arrival row authorizes a namespace the federation handshake already authenticated and can never verify a claim; leave algorithm and publicKey empty.");
                }
            } else {
                try {
                    spki = Convert.FromBase64String(s: (row.PublicKey ?? string.Empty));
                } catch (FormatException) {
                    errors.Add(item: $"{path}.publicKey is not valid base64.");
                }
            }

            if (spki is { Length: > 0 }) {
                if (spki.Length > AttestationResourceLimits.SubjectPublicKeyInfoBytes) {
                    errors.Add(item: $"{path}.publicKey is {spki.Length} bytes; attestation-v1-base permits at most {AttestationResourceLimits.SubjectPublicKeyInfoBytes} DER SPKI bytes.");
                }

                try {
                    var pinnedId = new KeyId {
                        Algorithm = (row.Algorithm ?? string.Empty),
                        Domain = (row.Domain ?? string.Empty),
                        KeyHash = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki),
                        Subject = ((row.Mode == WorldAdmissionTrustMode.Vouches)
                        ? null
                        : row.Subject),
                    };
                    var entry = new TrustListEntry(
                        PinnedId: pinnedId,
                        PublicKeySubjectPublicKeyInfo: spki,
                        Mode: ((row.Mode == WorldAdmissionTrustMode.Vouches)
                        ? AttestationTrustMode.Vouches
                        : AttestationTrustMode.SignsDirectly),
                        Reach: WorldAdmissionEmptyReach,
                        MaximumAge: null
                    );

                    entry.Validate();
                } catch (ArgumentException exception) {
                    errors.Add(item: $"{path}: {exception.Message}");
                }
            } else if (spki is not null) {
                errors.Add(item: $"{path}.publicKey decodes to zero bytes.");
            }

            if (
                (row.Mode == WorldAdmissionTrustMode.SignsDirectly) &&
                string.IsNullOrWhiteSpace(value: row.Subject)
            ) {
                errors.Add(item: $"{path}.subject is required for mode 'signsDirectly'.");
            }

            if (
                (row.Mode == WorldAdmissionTrustMode.Vouches) &&
                (row.Subject is not null)
            ) {
                errors.Add(item: $"{path}.subject must be absent for mode 'vouches' — a vouching root's chain resolves its own subject; it does not pin one here.");
            }

            if (
                (row.Mode == WorldAdmissionTrustMode.FederatedAuthority) &&
                (row.Subject is not null)
            ) {
                errors.Add(item: $"{path}.subject must be absent for mode 'federatedAuthority' — the row trusts an authority namespace, never one traveler it hands over.");
            }

            if (
                (row.Disclosure is { } disclosure) &&
                !Enum.IsDefined(value: disclosure)
            ) {
                errors.Add(item: $"{path}.disclosure '{disclosure}' is not defined.");
            }

            // Frames is a legitimate authored tier for an observer that only ever receives pixels, but a peer that
            // is minted anything it must act through needs at least the presentation document to resolve what it is
            // acting on — a grant with nothing to address is a grant that can only fail at use.
            if (
                (row.Disclosure == WorldDisclosureTier.Frames) &&
                ((row.Grants ?? []).Count > 0)
            ) {
                errors.Add(item: $"{path}.disclosure 'frames' mints {(row.Grants ?? []).Count} grant(s) — a frames-tier peer receives no document to address them against.");
            }

            var grants = (row.Grants ?? []);

            for (var grantIndex = 0; (grantIndex < grants.Count); grantIndex++) {
                var grant = grants[grantIndex];
                var grantPath = $"{path}.grants[{grantIndex}]";

                // An absent subject means the body this admission assigns, whose index the door cannot know; it is
                // concrete by construction, so it needs neither a bounds check nor the exclusive-over-all rule.
                if (grant.Subject is { } grantSubject) {
                    ValidateGrantSubjectBounds(
                        errors: errors,
                        path: grantPath,
                        populationCapacity: populationCapacity,
                        subject: grantSubject
                    );

                    if (
                        grant.Exclusive &&
                        (grantSubject.Kind == GrantSubjectKind.All)
                    ) {
                        errors.Add(item: $"{grantPath} is exclusive over 'all' — an exclusive reservation must name a concrete subject.");
                    }
                }
            }
        }
    }
    // GrantSubjectJsonConverter already validates a subject's grammar/shape via WorldGrantCommandModule.TryParseSubject
    // at parse time; it has no population figure to check a body:<n> token against (WorldGrants.IsLegitimateSubject
    // bounds it later, at grant time, and only for an actual grant), so that bound is checked here instead, for both
    // a request and an authored grant.
    private static void ValidateGrantSubjectBounds(GrantSubject subject, int populationCapacity, string path, List<string> errors) {
        if (
            (subject.Kind == GrantSubjectKind.Body) &&
            ((subject.Value < 0) || (subject.Value >= populationCapacity))
        ) {
            errors.Add(item: $"{path}.subject body:{subject.Value} is outside 0..{(populationCapacity - 1)} for the authored population capacity.");
        }
        // A row-scoped Mutate subject is deliberately NOT bound-checked against the live creations/placements rows:
        // authoring a row that does not exist yet is the act a contribution slot grants. Its shape is still checked,
        // by the same rule the live grant door applies (Server.WorldGrants.Conflicts) — WorldPrototype.Id is a
        // DocumentIdentifier, so a `state.` token there names a reference whose resolved value is some other string;
        // WorldPlacement.Id is a plain literal, which is why the reference rule is creation-only.
        if (subject.Kind is GrantSubjectKind.Creation or GrantSubjectKind.Placement) {
            var id = (subject.Id ?? string.Empty);

            if (string.IsNullOrWhiteSpace(value: id)) {
                errors.Add(item: $"{path}.subject {subject.Describe()} names a blank row id.");
            } else if (
                (subject.Kind == GrantSubjectKind.Creation) &&
                id.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: Puck.Assets.Documents.DocumentIdentifier.ReferencePrefix
            )
            ) {
                errors.Add(item: $"{path}.subject {subject.Describe()} names a state reference rather than a row id — a '{Puck.Assets.Documents.DocumentIdentifier.ReferencePrefix}' token resolves to some other string at load, so the row it addresses can never equal the granted subject; name the resolved id.");
            }
        }
    }
    private static void ValidateGrants(IReadOnlyList<WorldGrant> grants, HashSet<string> addonNames, HashSet<string> groupIds, int populationCapacity, int localSeats, List<string> errors) {
        var seen = new HashSet<(WorldPrincipal, WorldCapability, GrantSubject)>();

        for (var index = 0; (index < grants.Count); index++) {
            var grant = grants[index];
            var path = $"grants[{index}]";

            // A `world` row is refused HERE, at the document's own door, rather than only at the boot replay: the
            // grant table refuses it on EVERY boot (WorldGrants.Conflicts rule (-1) — the world's authority is
            // STRUCTURAL, so a row for it would be accepted-and-inert), which made this a document that validates
            // against itself and then loses a row it declared, every single time it loads. A document may not carry
            // a row nothing will ever hold.
            if (grant.Principal.Kind == PrincipalKind.World) {
                errors.Add(item: $"{path}.principal is 'world' — the world's own authored program (a rules effect, a kit's generate effect) holds no grant rows: its authority is STRUCTURAL, admitted before the table is consulted at all, so the grant table refuses this row on every boot and the document would validate against itself.");
            } else if (
                (grant.Principal.Kind == PrincipalKind.Addon) &&
                !addonNames.Contains(item: (grant.Principal.Name ?? string.Empty))
            ) {
                errors.Add(item: $"{path}.principal addon:{grant.Principal.Name} names no declared addon row.");
            } else if (
                (grant.Principal.Kind == PrincipalKind.Peer) &&
                (((uint)(grant.Principal.Index - localSeats)) >= ((uint)(populationCapacity - localSeats)))
            ) {
                errors.Add(item: $"{path}.principal peer:{grant.Principal.Index} is outside {localSeats}..{(populationCapacity - 1)} for the authored population capacity.");
            } else if (
                (grant.Principal.Kind == PrincipalKind.Group) &&
                !groupIds.Contains(item: (grant.Principal.Name ?? string.Empty))
            ) {
                // The SAME "validates then loses the row" trap the world/addon/peer checks above already close: the
                // live table refuses an unknown-group grant row too (Server.WorldGrants.Conflicts' reachability
                // check), so a document that validates against itself here would lose the row on every boot.
                errors.Add(item: $"{path}.principal group:{grant.Principal.Name} names no declared group row.");
            }

            ValidateGrantSubjectBounds(
                subject: grant.Subject,
                populationCapacity: populationCapacity,
                path: path,
                errors: errors
            );

            if (
                grant.Exclusive &&
                (grant.Subject.Kind == GrantSubjectKind.All)
            ) {
                errors.Add(item: $"{path} is exclusive over 'all' — an exclusive reservation must name a concrete subject.");
            }

            if (!seen.Add(item: (grant.Principal, grant.Capability, grant.Subject))) {
                errors.Add(item: $"{path} duplicates an earlier row naming the same principal, capability, and subject.");
            }
        }
    }
    // A scope=group destination's selector: a `named` arm must resolve to a declared groups.groups[].id (the
    // named/tagged split docs/vision.md "Durability, scope and generation" describes); a `tagged` arm names no
    // particular group up front — resolution walks the ACTING traveler's own memberships at transfer time (a later
    // lane's job), so this pass only holds the tag itself to the same non-empty discipline WorldGroup.Tags entries
    // already carry.
    private static void ValidateGroupSelector(WorldGroupSelector selector, HashSet<string> groupIds, string path, List<string> errors) {
        switch (selector) {
            case WorldGroupSelector.Named named:
                RequireDeclaredListing(
                    declaredSet: groupIds,
                    errors: errors,
                    rowNoun: "groups.groups row",
                    subject: $"{path} names group '{named.Group}', which",
                    value: named.Group
                );
                break;

            case WorldGroupSelector.Tagged tagged:
                if (string.IsNullOrWhiteSpace(value: tagged.Tag)) {
                    errors.Add(item: $"{path}.tag must be non-empty.");
                }
                break;

            default:
                errors.Add(item: $"{path} is an unrecognized selector kind.");
                break;
        }
    }
    // Validates the GROUP + MEMBERSHIP binding substrate — the group-kind policy catalog and the group roster rows.
    // Returns the declared group-id set so ValidateGrants can check a document-authored group: principal row against
    // it (the SAME forward-threading addonNames already rides). A null section (the document declared no `groups`
    // section at all — OPTIONAL, like `rules`) validates as empty.
    private static HashSet<string> ValidateGroups(WorldGroupsSection? groups, Dictionary<string, WorldStateRow> stateRows, List<string> errors) {
        var groupIds = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (groups is null) {
            return groupIds;
        }

        if (groups.Kinds.Count > WorldGroupCapacity.MaxKinds) {
            errors.Add(item: $"groups.kinds count {groups.Kinds.Count} exceeds the maximum of {WorldGroupCapacity.MaxKinds}.");
        }

        var kindsByName = new Dictionary<string, WorldGroupKind>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < groups.Kinds.Count); index++) {
            var kind = groups.Kinds[index];
            var path = $"groups.kinds[{index}]";

            if (kind is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: kind.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!kindsByName.TryAdd(
                key: kind.Name,
                value: kind
            )) {
                errors.Add(item: $"{path}.name '{kind.Name}' is duplicated.");
            }

            if (
                (kind.Capacity < 1) ||
                (kind.Capacity > WorldGroupCapacity.MaxMembersPerGroup)
            ) {
                errors.Add(item: $"{path}.capacity {kind.Capacity} is outside 1..{WorldGroupCapacity.MaxMembersPerGroup}.");
            }

            var roleNames = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var roleIndex = 0; (roleIndex < kind.Roles.Count); roleIndex++) {
                var role = kind.Roles[roleIndex];
                var rolePath = $"{path}.roles[{roleIndex}]";

                if (role is null) {
                    errors.Add(item: $"{rolePath} is required.");

                    continue;
                }

                RequireUniqueName(
                    value: role.Name,
                    seen: roleNames,
                    path: rolePath,
                    field: "name",
                    errors: errors
                );

                if (role.Capabilities.Count == 0) {
                    errors.Add(item: $"{rolePath}.capabilities is empty — a role reaching no capability could not exist without lying about what it is for; omit the role instead.");
                }
            }

            if (
                (kind.SharedStateScope is { } scope) &&
                !stateRows.ContainsKey(key: scope)
            ) {
                errors.Add(item: $"{path}.sharedStateScope '{scope}' names no declared state row.");
            }
        }

        // The size-only-kind guard: every PAIR of declared kinds must differ in at least one behavior-bearing field.
        var declared = kindsByName.Values.ToArray();

        for (var left = 0; (left < declared.Length); left++) {
            for (var right = (left + 1); (right < declared.Length); right++) {
                if (
                    SameBehavior(
                    a: declared[left],
                    b: declared[right]
                ) &&
                    (declared[left].Capacity != declared[right].Capacity)
                ) {
                    errors.Add(item: $"groups.kinds '{declared[left].Name}' and '{declared[right].Name}' differ ONLY in capacity — a kind that differs from another only in capacity is a capacity VALUE, not a kind (rename one usage to author the same kind with a different member cap instead).");
                }
            }
        }

        if (groups.Groups.Count > WorldGroupCapacity.MaxGroups) {
            errors.Add(item: $"groups.groups count {groups.Groups.Count} exceeds the maximum of {WorldGroupCapacity.MaxGroups}.");
        }

        for (var index = 0; (index < groups.Groups.Count); index++) {
            var row = groups.Groups[index];
            var path = $"groups.groups[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            // row.Id already crossed the WorldSafeName door at JSON parse — it CANNOT hold a value the
            // id-to-instance-name composition (WorldSessionResolver.MintInstanceName) would refuse (the identical
            // reasoning WorldIdentitySeed.Id's own validator comment states: the mapping is injective over
            // WorldSafeName, so no separate "does it survive the mapping" check can ever fire here). The only thing
            // left to check is ORDINAL UNIQUENESS within this document.
            if (!groupIds.Add(item: row.Id)) {
                errors.Add(item: $"{path}.id '{row.Id}' is duplicated.");
            }

            // A row's own tags — what a scope=group `tagged` destination selector matches against (see
            // ValidateGroupSelector/WorldGroup.Tags). Absent means none; present-but-empty is refused rather than
            // silently treated as absent, the same "author it or omit it" discipline the section's other optional
            // lists (kind.SharedStateScope, this section's own Ownership) follow.
            if (row.Tags is { Count: 0 }) {
                errors.Add(item: $"{path}.tags is present but empty — omit the member instead of authoring an empty list.");
            } else if (row.Tags is { Count: > 0 } tags) {
                var seenTags = new HashSet<string>(comparer: StringComparer.Ordinal);

                for (var tagIndex = 0; (tagIndex < tags.Count); tagIndex++) {
                    var tag = tags[tagIndex];
                    var tagPath = $"{path}.tags[{tagIndex}]";

                    RequireUniqueName(
                        value: tag,
                        seen: seenTags,
                        path: tagPath,
                        field: "",
                        errors: errors
                    );
                }
            }

            if (!kindsByName.TryGetValue(
                key: (row.KindName ?? string.Empty),
                value: out var kind
            )) {
                errors.Add(item: $"{path}.kindName '{row.KindName}' names no declared group kind.");

                continue;
            }

            if (row.Members.Count > kind.Capacity) {
                errors.Add(item: $"{path} has {row.Members.Count} member(s), exceeding kind '{kind.Name}''s capacity of {kind.Capacity}.");
            }

            var seenMembers = new HashSet<WorldPrincipal>();

            for (var memberIndex = 0; (memberIndex < row.Members.Count); memberIndex++) {
                var member = row.Members[memberIndex];
                var memberPath = $"{path}.members[{memberIndex}]";

                if (!IsLegitimateGroupMember(member: member)) {
                    errors.Add(item: ((member.Kind == PrincipalKind.Group)
                        ? $"{memberPath} is '{member.Describe()}' — FLAT ONLY: a group member is a principal, never a group."
                        : $"{memberPath} is '{member.Describe()}' — {member.Kind} is not a real actor and cannot hold membership."));
                } else if (!seenMembers.Add(item: member)) {
                    errors.Add(item: $"{memberPath} '{member.Describe()}' is duplicated within the group.");
                }
            }
        }

        // One row per subject — the structural half of the escrow/transfer lane's refusal obligation (see
        // WorldMutation.OfferOwnership/SettleOwnership's own remarks): a subject with TWO ownership rows would have
        // two answers to "who owns it", which is exactly the "owned by two principals" shape the invariant forbids,
        // reachable here only through hand-authored duplication (no live mutation kind can produce it — every arm
        // REPLACES the one row naming a subject, never appends a second).
        var seenSubjects = new HashSet<OwnershipSubject>();

        for (var index = 0; (index < groups.Ownership.Count); index++) {
            var row = groups.Ownership[index];
            var path = $"groups.ownership[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!seenSubjects.Add(item: row.Subject)) {
                errors.Add(item: $"{path}.subject '{row.Subject.Describe()}' is duplicated — a subject may carry exactly one ownership row.");
            }

            switch (row.Subject.Kind) {
                case OwnershipSubjectKind.Group:
                    if (!groupIds.Contains(item: (row.Subject.Id ?? string.Empty))) {
                        errors.Add(item: $"{path}.subject names group '{row.Subject.Id}', which no declared group row carries.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.subject.kind {row.Subject.Kind} is not a declared subject kind.");

                    break;
            }

            switch (row.Owner.Kind) {
                case OwnershipOwnerKind.Principal:
                    if (row.Owner.Principal is not { } owner) {
                        errors.Add(item: $"{path}.owner.kind is Principal but carries no principal.");
                    } else if (!IsLegitimateGroupMember(member: owner)) {
                        errors.Add(item: $"{path}.owner '{owner.Describe()}' is not a legitimate owner principal — group ownership rides owner.kind Group instead.");
                    }

                    if (row.Owner.GroupId is not null) {
                        errors.Add(item: $"{path}.owner.kind is Principal but also carries a groupId.");
                    }

                    if (row.Owner.Escrow is not null) {
                        errors.Add(item: $"{path}.owner.kind is Principal but also carries an escrow.");
                    }

                    break;
                case OwnershipOwnerKind.Group:
                    if (row.Owner.GroupId is not { } ownerGroupId) {
                        errors.Add(item: $"{path}.owner.kind is Group but carries no groupId.");
                    } else if (!groupIds.Contains(item: ownerGroupId)) {
                        errors.Add(item: $"{path}.owner names group '{ownerGroupId}', which no declared group row carries.");
                    }

                    if (row.Owner.Principal is not null) {
                        errors.Add(item: $"{path}.owner.kind is Group but also carries a principal.");
                    }

                    if (row.Owner.Escrow is not null) {
                        errors.Add(item: $"{path}.owner.kind is Group but also carries an escrow.");
                    }

                    break;
                case OwnershipOwnerKind.Escrow:
                    if (row.Owner.Escrow is not { } escrow) {
                        errors.Add(item: $"{path}.owner.kind is Escrow but carries no escrow.");
                    } else {
                        if (!IsLegitimateGroupMember(member: escrow.Offerer)) {
                            errors.Add(item: $"{path}.owner.escrow.offerer '{escrow.Offerer.Describe()}' is not a legitimate actor principal.");
                        }

                        if (!IsLegitimateGroupMember(member: escrow.Recipient)) {
                            errors.Add(item: $"{path}.owner.escrow.recipient '{escrow.Recipient.Describe()}' is not a legitimate actor principal.");
                        }

                        if (escrow.Offerer == escrow.Recipient) {
                            errors.Add(item: $"{path}.owner.escrow offers to its own offerer {escrow.Offerer.Describe()} — an offer to oneself is not a trade.");
                        }

                        if (escrow.DeadlineTick < 0) {
                            errors.Add(item: $"{path}.owner.escrow.deadlineTick {escrow.DeadlineTick} is negative.");
                        }
                    }

                    if (row.Owner.Principal is not null) {
                        errors.Add(item: $"{path}.owner.kind is Escrow but also carries a principal.");
                    }

                    if (row.Owner.GroupId is not null) {
                        errors.Add(item: $"{path}.owner.kind is Escrow but also carries a groupId.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.owner.kind {row.Owner.Kind} is not a declared owner kind.");

                    break;
            }
        }

        return groupIds;
    }

    // Mirrors WorldAdmissionDoor's own s_noReach: this section's authorization vocabulary is
    // WorldAdmissionEntry.Grants, never Puck.Attestation's slot-reach mechanism, so every entry validates against an
    // empty reach set here too.
    private static readonly IReadOnlySet<string> WorldAdmissionEmptyReach = new HashSet<string>(comparer: StringComparer.Ordinal);
}
