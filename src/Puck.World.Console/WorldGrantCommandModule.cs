using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The capability-grant console surface — the dev reflection of the principal/grant model: <c>world.grant</c> and
/// <c>world.revoke</c> mutate the server's one grant table over the wire, <c>world.grants</c> echoes it, and
/// <c>world.why</c> echoes the <see cref="GrantVerdict"/> a single check produces (which rule decides, not merely
/// whether). Grant
/// changes route <see cref="CommandRouting.Simulation"/> (they gate sim behavior) and apply synchronously at submit
/// (like a command), so a following <c>world.grants</c> read behind the stdin barrier sees the settled table; the
/// server prints the loud accept/reject line. This is a separate module from the mutation surface to keep both under
/// their analyzer ceilings.
/// </summary>
/// <remarks>Principal tokens: <c>seat1</c>..<c>seat4</c> | <c>console</c> | <c>addon:&lt;name&gt;</c> |
/// <c>peer:&lt;n&gt;:&lt;generation&gt;</c> (a population entity index and its current admission generation). Capability
/// tokens: <c>drive</c> | <c>observe</c> | <c>control</c> |
/// <c>mutate</c> | <c>edit</c>. Subject tokens: <c>body:&lt;n&gt;</c> (0..127, the population ceiling) |
/// <c>screen:&lt;n&gt;</c> | <c>section:&lt;name&gt;</c> | <c>state:&lt;name&gt;</c> | <c>region:&lt;name&gt;</c>
/// (a placement's volume facet) | <c>seat:&lt;n&gt;</c> (0..3, local seats) | <c>creation:&lt;id&gt;</c> |
/// <c>placement:&lt;id&gt;</c> (one creations/placements row apiece) | <c>all</c>. Trailing
/// tokens, any order, each at most once: <c>exclusive</c> on <c>world.grant</c> requests an exclusive hold
/// (rejected if a live holder owns it, in either order — the seeded permissive wildcard is exempt in both
/// directions, so it can always be narrowed and re-widened regardless of what exclusive holds exist elsewhere),
/// <c>budget:&lt;n&gt;</c> (1..65535) sets the row's per-tick dispatch allowance — required on an <c>observe</c>,
/// <c>drive</c>, or <c>mutate</c> grant to an untrusted <c>addon:</c>/<c>peer:</c> principal (refused by name otherwise),
/// refused on every other row (trusted principals read/drive/mutate unmetered; no capability but observe/drive/mutate
/// has a dispatch door to meter yet), and <c>budget:0</c> is refused at parse time (0 is not a spelling for "no budget" — omit the token instead);
/// and <c>events:&lt;n&gt;</c> (1..65535), the world-events sibling budget on an <c>observe</c> row — required on
/// <c>screen:</c>/<c>region:</c>/<c>seat:</c> subjects (they carry no other meaning), optional on <c>body:</c>. Two
/// mask tokens ride here too, deliberately spelled apart because they are two vocabularies over one bit-lane shape:
/// <c>verbs:&lt;name,...&gt;</c> names <see cref="Puck.World.Protocol.WorldMutation"/> kinds (on
/// <c>mutate section:&lt;name&gt;</c>/<c>mutate creation:&lt;id&gt;</c>/<c>mutate placement:&lt;id&gt;</c>, or
/// <c>edit state:&lt;name&gt;</c>), and <c>writes:&lt;name,...&gt;</c> names
/// <see cref="Puck.World.Protocol.WorldDocumentWriteKind"/> operations (on <c>mutate state:&lt;name&gt;</c>, the
/// cross-document durable-state write-back channel). Every
/// capability rejects any subject shape it does not
/// legitimately admit (see <see cref="Puck.World.Server.WorldGrants"/>'s own remarks for the full table and why):
/// <c>drive</c> accepts <c>body:&lt;n&gt;</c> naming a body that actually exists for any principal (an addon/peer
/// must carry <c>budget:&lt;n&gt;</c>) or <c>all</c> (console/seat only — an <c>addon:</c> principal is restricted to
/// <c>body:&lt;n&gt;</c> alone); <c>control</c>
/// accepts <c>screen:&lt;n&gt;</c> (any principal) or <c>all</c> (console/seat/peer); <c>mutate</c> accepts
/// <c>section:&lt;name&gt;</c> (any principal; an addon/peer must carry <c>budget:&lt;n&gt;</c>),
/// <c>creation:&lt;id&gt;</c>/<c>placement:&lt;id&gt;</c> (the row-scoped slot — any principal but <c>addon:</c>,
/// whose mutation seam designates a section handle and could never dispatch one), or <c>all</c>
/// (console/seat); <c>edit</c> accepts
/// <c>state:&lt;name&gt;</c> (any principal, whether the row is a scalar slot or a keyed table — a slot is a table
/// with one key; a concrete row may additionally carry <c>verbs:&lt;name,...&gt;</c>, the mutation-kind mask that
/// separates the per-cell writes from the whole-row re-authoring) or <c>all</c> (console/seat); <c>observe</c>
/// additionally accepts <c>body:&lt;n&gt;</c> naming a body that exists for any principal (an addon/peer must carry
/// <c>budget:&lt;n&gt;</c>) or <c>all</c> (console/seat).</remarks>
public sealed class WorldGrantCommandModule(IWorldConsoleAuthority authority, IServerLink link) : ICommandModule {
    // public (not internal): Puck.World's WorldAddonCommandModule.Mount reuses this exact token grammar for its
    // trailing <capability> <subject> manifest pairs, so "drive body:0" means the identical thing on both verbs —
    // widened to the minimum needed rather than duplicating the parse.
    public static bool TryParseCapability(ReadOnlySpan<char> token, out WorldCapability capability) {
        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "drive"
        )) {
            capability = WorldCapability.Drive;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "observe"
        )) {
            capability = WorldCapability.Observe;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "control"
        )) {
            capability = WorldCapability.Control;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "mutate"
        )) {
            capability = WorldCapability.Mutate;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "edit"
        )) {
            capability = WorldCapability.Edit;

            return true;
        }

        capability = WorldCapability.Drive;

        return false;
    }
    /// <summary>Parses the same <c>&lt;principal&gt; &lt;capability&gt; &lt;subject&gt; [exclusive] [budget:&lt;n&gt;]</c>
    /// grammar <c>world.grant</c>/<c>world.revoke</c> use, shared with the mutation surface's
    /// <c>world.grant.set</c>/<c>world.grant.remove</c> — one grammar for a grant token sequence regardless of
    /// whether it ends up live (this module) or document-authored (that one). The two trailing tokens are both gated
    /// by <paramref name="exclusiveAllowed"/> (revoke/remove accept neither — a revoke matches by
    /// (principal, capability, subject) alone and ignores both), and either may come first: <c>exclusive budget:8</c>
    /// and <c>budget:8 exclusive</c> parse identically.</summary>
    /// <param name="args">The verb's wire arguments (the tokens after the verb name).</param>
    /// <param name="exclusiveAllowed">Whether trailing <c>exclusive</c>/<c>budget:&lt;n&gt;</c>/<c>channels:&lt;...&gt;</c>/
    /// <c>ceiling:&lt;f&gt;</c> tokens are accepted.</param>
    /// <param name="verb">The calling verb's name, for the error echo.</param>
    /// <param name="grant">The parsed grant, on success.</param>
    /// <param name="error">The inline error echo, on failure.</param>
    /// <param name="channels">The world's compiled channel table, needed to resolve a <c>channels:&lt;name,...&gt;</c>
    /// token to ordinals — <see langword="null"/> only for a caller with no live table to resolve against (a
    /// <c>channels:</c> token then refuses by name rather than silently accepting an unresolved name).</param>
    /// <param name="targets">The world's compiled target-register table, needed to resolve a
    /// <c>registers:&lt;name,...&gt;</c> token into the shared reach bitspace.</param>
    /// <param name="verbsAllowed">Whether a trailing <c>verbs:&lt;name,...&gt;</c> token is accepted — independent of
    /// <paramref name="exclusiveAllowed"/>, since <c>world.why</c> accepts only this one trailing token (a read-only
    /// diagnostic) while accepting none of the four mutating ones.</param>
    /// <returns><see langword="true"/> when the tokens parsed to a well-formed grant.</returns>
    public static bool TryParseGrant(in WireArgs args, bool exclusiveAllowed, string verb, out WorldGrant grant, out CommandResult error, WorldChannelTable? channels = null, WorldTargetRegisterTable? targets = null, bool verbsAllowed = false) {
        grant = default;
        error = default;

        // Up to seven trailing tokens (exclusive, budget:<n>, events:<n>, channels:<...>, registers:<...>,
        // ceiling:<f>, hold:<seconds>) when the caller allows them, plus the TWO mask tokens (verbs:<name,...> and
        // writes:<name,...>); world.why allows ONLY the two mask tokens (read-only diagnostics, never the seven that
        // mutate a live row); world.revoke allows none — a revoke matches by (principal, capability, subject) alone
        // and clears every payload the row carried regardless.
        var maximum = ((3 + (exclusiveAllowed
            ? 7
            : 0)) + (verbsAllowed
            ? 2
            : 0));

        if (
            (args.Count < 3) ||
            (args.Count > maximum)
        ) {
            var form = (exclusiveAllowed
                ? "<principal> <capability> <subject> [exclusive] [budget:<n>] [events:<n>] [channels:<name,...>] [registers:<name,...>] [ceiling:<f>] [hold:<seconds>] [verbs:<name,...>] [writes:<name,...>]"
                : (verbsAllowed
                    ? "<principal> <capability> <subject> [verbs:<name,...>] [writes:<name,...>]"
                    : "<principal> <capability> <subject>"
            ));

            error = Usage(
                form: form,
                verb: verb
            );

            return false;
        }

        if (!TryParsePrincipal(
            token: args[0],
            principal: out var principal
        )) {
            error = CommandResult.Error(output: $"[{verb}: unknown principal '{args[0].ToString()}' — seat1..seat4|console|addon:<name>|peer:<n>:<generation>|document:<id>|group:<id>]");

            return false;
        }

        if (!TryParseCapability(
            token: args[1],
            capability: out var capability
        )) {
            error = CommandResult.Error(output: $"[{verb}: unknown capability '{args[1].ToString()}' — drive|observe|control|mutate|edit]");

            return false;
        }

        if (!TryParseSubject(
            token: args[2],
            subject: out var subject
        )) {
            error = CommandResult.Error(output: $"[{verb}: unknown subject '{args[2].ToString()}' — body:<n>|screen:<n>|section:<name>|state:<name>|region:<name>|seat:<n>|all]");

            return false;
        }

        var exclusive = false;
        ushort? budget = null;
        ushort? eventBudget = null;
        ChannelDeclaredMask? namedChannels = null;
        ChannelDeclaredMask? namedRegisters = null;
        long? ceiling = null;
        long? holdCeiling = null;
        MutationKindMask? kindMask = null;
        DocumentWriteMask? writeMask = null;

        for (var index = 3; (index < args.Count); index++) {
            var token = args[index];

            if (
                !exclusive &&
                exclusiveAllowed &&
                args.Is(
                index: index,
                value: "exclusive"
            )
            ) {
                exclusive = true;

                continue;
            }

            if (
                verbsAllowed &&
                token.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "verbs:"
            ) &&
                (token.Length > 6)
            ) {
                if (kindMask is not null) {
                    error = CommandResult.Error(output: $"[{verb}: 'verbs:' appears more than once]");

                    return false;
                }

                var mask = MutationKindMask.Empty;

                foreach (var name in token[6..].ToString().Split(
                    options: StringSplitOptions.None,
                    separator: ','
                )) {
                    if (!TryParseMutationKindName(
                        name: name,
                        ordinal: out var ordinal
                    )) {
                        error = CommandResult.Error(output: $"[{verb}: verbs:<> names '{name}', which names no declared mutation kind]");

                        return false;
                    }

                    mask = mask.With(ordinal: ordinal);
                }

                kindMask = mask;

                continue;
            }

            // writes: is verbs:' SIBLING, never its synonym — a DIFFERENT vocabulary (WorldDocumentWriteKind's
            // Set/Add) on a different door, spelled with its own token so a typed line can never be ambiguous about
            // which lane it meant (see MutationKindMask's remarks on the duality this split closed).
            if (
                verbsAllowed &&
                token.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "writes:"
            ) &&
                (token.Length > 7)
            ) {
                if (writeMask is not null) {
                    error = CommandResult.Error(output: $"[{verb}: 'writes:' appears more than once]");

                    return false;
                }

                var mask = DocumentWriteMask.Empty;

                foreach (var name in token[7..].ToString().Split(
                    options: StringSplitOptions.None,
                    separator: ','
                )) {
                    if (
                        !Enum.TryParse<WorldDocumentWriteKind>(
                        ignoreCase: true,
                        result: out var writeKind,
                        value: name
                    ) ||
                        !Enum.IsDefined(value: writeKind)
                    ) {
                        error = CommandResult.Error(output: $"[{verb}: writes:<> names '{name}', which is not one of {DocumentWriteMask.All.Describe()}]");

                        return false;
                    }

                    mask = mask.With(kind: writeKind);
                }

                writeMask = mask;

                continue;
            }

            if (
                exclusiveAllowed &&
                token.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "budget:"
            )
            ) {
                // 0 is refused HERE, at parse time, not merely at the door: 0 is not a spelling for "no budget" —
                // granting nothing is (omit the token). Folded into the same invalid-token message so a typed
                // budget:0 never reaches the wire/tape as a WorldGrant that a later stage has to refuse instead.
                if (
                    (budget is not null) ||
                    !ushort.TryParse(
                    s: token[7..],
                    style: NumberStyles.Integer,
                    provider: CultureInfo.InvariantCulture,
                    result: out var parsedBudget
                ) ||
                    (parsedBudget == 0)
                ) {
                    error = CommandResult.Error(output: $"[{verb}: invalid trailing token '{token.ToString()}' — expected budget:<n> with n in 1..65535, at most once (0 is not a spelling for 'no budget' — granting nothing is)]");

                    return false;
                }

                budget = parsedBudget;

                continue;
            }

            if (
                exclusiveAllowed &&
                token.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "events:"
            )
            ) {
                // The identical budget:0 discipline: 0 is not a spelling for "no event budget" — omit the token.
                if (
                    (eventBudget is not null) ||
                    !ushort.TryParse(
                    s: token[7..],
                    style: NumberStyles.Integer,
                    provider: CultureInfo.InvariantCulture,
                    result: out var parsedEventBudget
                ) ||
                    (parsedEventBudget == 0)
                ) {
                    error = CommandResult.Error(output: $"[{verb}: invalid trailing token '{token.ToString()}' — expected events:<n> with n in 1..65535, at most once (0 is not a spelling for 'no events' — granting nothing is)]");

                    return false;
                }

                eventBudget = parsedEventBudget;

                continue;
            }

            if (
                exclusiveAllowed &&
                token.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "channels:"
            ) &&
                (token.Length > 9)
            ) {
                if (namedChannels is not null) {
                    error = CommandResult.Error(output: $"[{verb}: 'channels:' appears more than once]");

                    return false;
                }

                if (channels is null) {
                    error = CommandResult.Error(output: $"[{verb}: channels:<> is not resolvable here — no live channel table to check names against]");

                    return false;
                }

                var mask = default(ChannelDeclaredMask);

                foreach (var name in token[9..].ToString().Split(
                    options: StringSplitOptions.None,
                    separator: ','
                )) {
                    if (!channels.TryGetOrdinal(
                        name: name,
                        ordinal: out var ordinal
                    )) {
                        error = CommandResult.Error(output: $"[{verb}: channels:<> names '{name}', which names no declared channel]");

                        return false;
                    }

                    mask = mask.With(ordinal: ordinal);
                }

                namedChannels = mask;

                continue;
            }

            if (
                exclusiveAllowed &&
                token.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "ceiling:"
            ) &&
                (token.Length > 8)
            ) {
                if (ceiling is not null) {
                    error = CommandResult.Error(output: $"[{verb}: 'ceiling:' appears more than once]");

                    return false;
                }

                // 0 is refused HERE too (parse time), the identical budget:0 discipline: pool-but-never-reach is
                // accepted-and-inert, so grant nothing instead of a ceiling that can never fire. NaN/Infinity are
                // rejected explicitly — NaN compares false against every bound below, so the range check alone would
                // let it through (WorldDefinitionValidator's channel.threshold check hits the identical trap).
                if (
                    !double.TryParse(
                    s: token[8..],
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out var parsedCeiling
                ) ||
                    !double.IsFinite(d: parsedCeiling) ||
                    (parsedCeiling < 0.0) ||
                    (parsedCeiling > 1.0)
                ) {
                    error = CommandResult.Error(output: $"[{verb}: invalid trailing token '{token.ToString()}' — expected ceiling:<f> with f in 0..1, at most once]");

                    return false;
                }

                // The fold compares the QUANTIZED raw ceiling (WorldGrants.PoolCeilings hands it this exact
                // FixedQ4816), never the authored double — an authored value like 1e-100 passes the range check above
                // but quantizes to raw 0, the identical "granted-but-never-reachable pool" ceiling:0 already refuses,
                // just reached through a different door. One check now covers both (the same "check the
                // representation the consumer actually compares" rule WorldDefinitionValidator's channel.threshold
                // check already enforces).
                var quantizedCeiling = FixedQ4816.FromDouble(value: parsedCeiling);

                if (quantizedCeiling.Value == 0L) {
                    error = CommandResult.Error(output: $"[{verb}: ceiling:{parsedCeiling} quantizes to raw 0 — a granted-but-never-reachable pool is accepted-and-inert; grant nothing instead]");

                    return false;
                }

                ceiling = quantizedCeiling.Value;

                continue;
            }

            if (
                exclusiveAllowed &&
                token.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "registers:"
            ) &&
                (token.Length > 10)
            ) {
                if (namedRegisters is not null) {
                    error = CommandResult.Error(output: $"[{verb}: 'registers:' appears more than once]");
                    return false;
                }
                if (targets is null) {
                    error = CommandResult.Error(output: $"[{verb}: registers:<> is not resolvable here — no live target-register table to check names against]");
                    return false;
                }

                var mask = default(ChannelDeclaredMask);

                foreach (var name in token[10..].ToString().Split(
                    options: StringSplitOptions.None,
                    separator: ','
                )) {
                    if (!targets.TryGetIndex(
                        index: out var targetIndex,
                        name: name
                    )) {
                        error = CommandResult.Error(output: $"[{verb}: registers:<> names '{name}', which names no declared target register]");
                        return false;
                    }
                    mask = mask.With(ordinal: targets.ReachOrdinal(index: targetIndex));
                }
                namedRegisters = mask;
                continue;
            }

            if (
                exclusiveAllowed &&
                token.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "hold:"
            ) &&
                (token.Length > 5)
            ) {
                if (
                    (holdCeiling is not null) ||
                    !double.TryParse(
                    s: token[5..],
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out var parsedHold
                ) ||
                    !double.IsFinite(d: parsedHold) ||
                    (parsedHold < 0.0) ||
                    (parsedHold > WorldBody.MaxActionHoldSeconds)
                ) {
                    error = CommandResult.Error(output: $"[{verb}: invalid trailing token '{token.ToString()}' — expected hold:<seconds> within the 0..{WorldBody.MaxActionHoldSeconds:0.###} engine backstop, at most once]");

                    return false;
                }

                holdCeiling = FixedQ4816.FromDouble(value: parsedHold).Value;

                continue;
            }

            var expected = (exclusiveAllowed
                ? "'exclusive', 'budget:<n>', 'events:<n>', 'channels:<name,...>', 'registers:<name,...>', 'ceiling:<f>', 'hold:<seconds>', 'verbs:<name,...>', and/or 'writes:<name,...>', each at most once"
                : (verbsAllowed
                    ? "'verbs:<name,...>' and/or 'writes:<name,...>', each at most once"
                    : "no trailing tokens"
            ));

            error = CommandResult.Error(output: $"[{verb}: unexpected trailing token '{token.ToString()}' — expected {expected}]");

            return false;
        }

        // A ceiling must name the channels it applies to — it is ONE number per (seat, channel), so a bare ceiling has
        // no ordinals to land on. The reverse is NOT an error: `channels:` alone is a contributor's REACH row (which
        // channels it may touch), which the grant door admits on an untrusted principal's drive grant. Which row may
        // carry which is WorldGrants.Conflicts' call, not this parser's.
        if (
            (ceiling is not null) &&
            (namedChannels is null)
        ) {
            error = CommandResult.Error(output: $"[{verb}: ceiling:<f> must name the channels it applies to (channels:<name,...>) — the pooled ceiling is one number per (seat, channel), never a single scalar over the whole vector]");

            return false;
        }

        var reachedBits = ((ceiling is null)
            ? (namedChannels?.Bits ?? 0UL)
            : 0UL) | (namedRegisters?.Bits ?? 0UL);
        var reach = (((ceiling is null) && (reachedBits != 0UL))
            ? new ChannelReachMask(Bits: reachedBits)
            : (ChannelReachMask?)null
        );
        var consent = (((ceiling is not null) && (namedChannels is { } consented))
            ? new ChannelConsentMask(Bits: consented.Bits)
            : (ChannelConsentMask?)null
        );

        grant = new WorldGrant(
            Budget: budget,
            Capability: capability,
            Ceiling: ceiling,
            Consent: consent,
            EventBudget: eventBudget,
            Exclusive: exclusive,
            HoldCeiling: holdCeiling,
            KindMask: kindMask,
            Principal: principal,
            Reach: reach,
            Subject: subject,
            WriteMask: writeMask
        );

        return true;
    }
    /// <summary>Parses a principal token (<c>seat1</c>..<c>seat4</c> | <c>console</c> | <c>addon:&lt;name&gt;</c> |
    /// <c>peer:&lt;n&gt;</c>) — shared with <see cref="Puck.World.WorldPrincipalJsonConverter"/>, so a document-sourced
    /// principal (a <see cref="WorldGrant.Principal"/> row, an addon manifest's implicit self-reference) always
    /// canonicalizes through the identical grammar a console token does. There is no other way to construct a
    /// non-canonical <see cref="WorldPrincipal"/> from either surface.</summary>
    /// <param name="token">The token to parse.</param>
    /// <param name="principal">The parsed principal, on success.</param>
    /// <returns><see langword="true"/> when the token parsed.</returns>
    public static bool TryParsePrincipal(ReadOnlySpan<char> token, out WorldPrincipal principal) {
        return WorldPrincipal.TryParse(
            principal: out principal,
            token: token
        );
    }
    /// <summary>Parses a subject token (<c>all</c> | <c>body:&lt;n&gt;</c> | <c>screen:&lt;n&gt;</c> |
    /// <c>section:&lt;name&gt;</c> | <c>state:&lt;name&gt;</c>) — shared with
    /// <see cref="Puck.World.GrantSubjectJsonConverter"/>, so a document-sourced subject (a
    /// <see cref="WorldCapabilityRequest.Subject"/>, a <see cref="WorldGrant.Subject"/> row) always canonicalizes
    /// through the identical grammar a console token does; there is no other way to construct a denormalized
    /// <see cref="GrantSubject"/> (a stray non-zero <c>Value</c>/<c>Id</c> the wildcard or section kinds do not use)
    /// from either surface, which is what keeps a document subject and a live grant table entry comparable by value.</summary>
    /// <param name="token">The token to parse.</param>
    /// <param name="subject">The parsed subject, on success.</param>
    /// <returns><see langword="true"/> when the token parsed.</returns>
    public static bool TryParseSubject(ReadOnlySpan<char> token, out GrantSubject subject) => GrantSubject.TryParse(
        subject: out subject,
        token: token
    );

    // The DOCUMENT-AUTHORED half of the read-back — the `document:<id>` rows in WorldDefinition.Grants, which are
    // deliberately NOT replayed into the live table (Server.WorldServer.IsDocumentChannelRow, and the grant door
    // refuses one by name): the cross-document durable-state write-back channel resolves them by reading the OWNER'S
    // document, so a live row would be a row nothing enforces. Echoing them HERE is what keeps that skip honest —
    // without it, dropping them from the table would drop the only surface that ever showed them. Omitted entirely
    // when the document carries none, so the ordinary read-back gains no noise.
    private static string DescribeDocumentRows(WorldDefinition definition, WorldPrincipal? filter) {
        var builder = new StringBuilder();

        foreach (var grant in definition.Grants) {
            if (
                (grant.Principal.Kind != PrincipalKind.Document) ||
                ((filter is { } only) && (grant.Principal != only))
            ) {
                continue;
            }

            _ = builder
                .Append(value: ((builder.Length == 0)
                ? " [world.grants.document: "
                : " | "))
                .Append(value: grant.Principal.Describe()).Append(value: ' ')
                .Append(value: grant.Capability.ToString().ToLowerInvariant()).Append(value: '/')
                .Append(value: grant.Subject.Describe());

            if (grant.WriteMask is { } writes) {
                _ = builder.Append(value: " writes:").Append(value: writes.Describe());
            }

            if (grant.KindMask is { } kinds) {
                _ = builder.Append(value: " verbs:").Append(value: kinds.Describe());
            }
        }

        return ((builder.Length == 0)
            ? string.Empty
            : builder.Append(value: ']').ToString()
        );
    }
    // Parse and submit a grant/revoke. Both share the principal/capability/subject grammar; grant additionally takes an
    // optional trailing 'exclusive'.
    private CommandResult Handle(WorldServer server, WorldPrincipal actor, in WireArgs args, bool exclusiveAllowed, bool revoke) {
        var verb = (revoke
            ? "world.revoke"
            : "world.grant"
        );

        // verbsAllowed rides WITH exclusiveAllowed here: world.grant (true/true) accepts both the mutating trailing
        // tokens and verbs:; world.revoke (false/false) accepts neither — a revoke matches by
        // (principal, capability, subject) alone and clears every payload the row carried regardless, verb mask
        // included (see WorldGrants.Revoke).
        if (!TryParseGrant(
            args: args,
            exclusiveAllowed: exclusiveAllowed,
            verb: verb,
            grant: out var grant,
            error: out var error,
            channels: server.Population.Channels,
            targets: server.Population.TargetRegisters,
            verbsAllowed: exclusiveAllowed
        )) {
            return error;
        }

        // The actor is whatever this dispatch's ingress door stamped — Console for a typed line. Console passes
        // WorldGrants.HoldsForAdministration unconditionally; a Seat actor passes only when the grant's OWN subject is
        // its own body (see that method's own doc for the narrowing). Note the GRANT's own principal (parsed
        // from the tokens) is the grant's TARGET and is a different thing entirely from the acting Seat/Console here.
        if (revoke) {
            link.SubmitRevoke(
                actor: actor,
                grant: grant
            );
        } else {
            link.SubmitGrant(
                actor: actor,
                grant: grant
            );
        }

        // The server prints the loud [world.grant: …] / [world.revoke: …] line at submit; the verb stays quiet.
        return CommandResult.None;
    }
    // Resolves a verbs:<...> token's comma-separated name against the declared mutation-kind catalog — the nested
    // WorldMutation record's own CLR name (e.g. "UpsertHudPanel"), case-insensitive, the same "the type's own name is
    // the stable id, never a second string kept in sync by hand" discipline the world.refusals catalog uses.
    private static bool TryParseMutationKindName(string name, out int ordinal) {
        foreach (var entry in WorldMutationKindCatalog.All()) {
            if (string.Equals(
                a: entry.Type.Name,
                b: name,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                ordinal = entry.Ordinal;

                return true;
            }
        }

        ordinal = -1;

        return false;
    }
    private static CommandResult Usage(string verb, string form) {
        return CommandResult.Error(output: $"[{verb}: expected {form}]");
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.grant",
            description: "Grants a capability to a principal: world.grant <principal> <capability> <subject> [exclusive] [budget:<n>] [events:<n>] [channels:<name,...>] [ceiling:<f>] [hold:<seconds>] [verbs:<name,...>] [writes:<name,...>]. principal = seat1..seat4|console|addon:<name>|peer:<n>:<generation>; capability = drive|observe|control|mutate|edit; subject = body:<n>|screen:<n>|section:<name>|state:<name>|region:<name>|seat:<n>|creation:<id>|placement:<id>|all (state:<name> narrows edit over ONE named state row, slot-shaped or keyed alike — a slot is a table with one key — reaching BOTH its whole-row world.row.set state/world.row.remove state and its per-cell world.state.cell.set/.remove writes). Applies at submit; an exclusive grant a live holder owns is rejected loudly, in either order (the seeded permissive wildcard never blocks one, and an exclusive hold never permanently blocks the wildcard's later re-grant either). budget:<n> (1..65535) sets the row's per-tick dispatch allowance: REQUIRED on an observe, drive, or mutate section:<name> grant to an untrusted addon:/peer: principal (a defaulted budget would silently decide a denial-of-service ceiling), REFUSED on every other row (trusted reads/drives/mutations are unmetered, and a mutate state:<name> row is the cross-document write-back channel, which has no dispatch door to meter — it is gated by writes:<name,...> instead), and budget:0 is refused at parse time (0 is not a spelling for 'no budget' — omit the token instead). events:<n> (1..65535) is the WORLD-EVENTS sibling budget: an observe grant may carry it independently of budget:<n> (dispatch and events meter different costs — they are two SEPARATE meters, not one renamed); it is REQUIRED on observe screen:<n>/region:<name>/seat:<n> (those subjects carry no other meaning under observe) and OPTIONAL on observe body:<n> (a bare observe body:<n> keeps its existing pose-query meaning; adding events:<n> additionally admits that body into collision/route event delivery). The PRE-EXISTING budget:<n> requirement on every untrusted observe row is UNCHANGED and stacks with this — an observe screen:<n>/region:<name>/seat:<n> row therefore needs BOTH budget:<n> AND events:<n> (the untrusted-Observe dispatch meter does not know a subject carries no query verb; only events:<n> is genuinely new vocabulary). events:0 is refused at parse time the same way budget:0 is. hold:<seconds> is the Drive row's timed-press ceiling, defaults to 2 seconds when omitted, may narrow or widen within the 60-second engine backstop, and never limits a live key/button hold. channels:<name,...> and ceiling:<f> are the CO-DRIVING pair, legal only on a drive grant and naming declared channels by their world/kit vocabulary. channels:<...> ALONE on an untrusted addon:/peer: row is that contributor's REACH — which channels it may touch. channels:<...> WITH ceiling:<f> (0..1) is only legal on the occupying seat's OWN row (seatN drive body:N) and authors the pool bound for exactly the channels it names, leaving other channels' ceilings as they were; issue it twice to give two channels different ceilings, and revoke the seat's own drive row to clear them. A reach with no seat-authored ceiling folds nothing. ceiling:0 is refused (pool-but-never-reach is accepted-and-inert; grant nothing instead), a bare ceiling with no channels is refused (it is one number per (seat, channel), not a scalar), and a ceiling on a contributor's row is refused (the ceiling is never derived from contributor rows). verbs:<name,...> is the MUTATION-KIND mask — legal on a mutate grant naming a CONCRETE section:<name>, creation:<id>, or placement:<id> subject (the dispatch door) and on an edit grant naming a CONCRETE state:<name> subject (never 'all' on either): it names WorldMutation kind types by their own record name (e.g. UpsertKit), and is refused if any names a kind outside that target's own declared kind set (an inert bit is a grant that lies) or if the resulting mask admits nothing at all (grant nothing instead). It is REQUIRED on an UNTRUSTED addon:/peer: mutate section:<name> row and refused without it: an absent mask means FULL REACH at the admission door (a trusted principal's maskless row is the seeded default), so a maskless untrusted row would silently admit every kind the section declares. On an EDIT row it is what separates bumping a state row from redefining it — 'verbs:UpsertStateCell,RemoveStateCell' admits the per-cell writes while denying the whole-row UpsertStateRow/RemoveStateRow that would re-author the row's envelope; an UNMASKED edit row keeps full reach, so a mask is opt-in narrowing beneath an already deny-by-default capability, never a new gate. writes:<name,...> is its SIBLING over a DIFFERENT vocabulary — WorldDocumentWriteKind's Set|Add, the cross-document durable-state write-back channel — legal ONLY on a mutate grant naming a CONCRETE state:<name> subject. The two are separate tokens because they are separate bit vocabularies: verbs: bit 0 is UpsertKit, writes: bit 0 is Set, and one field carrying both was a lane whose meaning depended on the row's subject kind. A RE-GRANT of the same row that OMITS either token CLEARS a previously-recorded mask of that kind — unlike budget/channels, which only ever write when carried. world.grants echoes a live mask by NAME (verbs:UpsertStateCell,RemoveStateCell / writes:Set,Add), never as a hex lane. Every capability rejects a subject shape it does not legitimately admit: drive wants body:<n> naming a body that exists (any principal; addon/peer must carry budget:<n>) or all (console/seat; addon must name body:<n>); control wants screen:<n> (any principal) or all (console/seat/peer); mutate wants section:<name> (any principal; an untrusted addon:/peer: row must carry BOTH budget:<n> and verbs:<name,...>) or creation:<id>/placement:<id> (the ROW-SCOPED slot, admitting that one creations/placements row and no other; same budget:/verbs: requirements for an untrusted principal, and refused outright for an addon: principal, whose mutation seam designates a section handle and could never dispatch it) or state:<name> (any principal, the cross-document write-back channel; no budget) or all (console/seat); edit wants state:<name> (any principal) or all (console/seat); observe wants body:<n> naming a body that exists (any principal; addon/peer must carry budget:<n>) or all (console/seat), and ADDITIONALLY (untrusted addon:/peer: principals only) screen:<n>, region:<name>, or seat:<n> — the world-events subjects, each requiring events:<n>.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.grant"
                )) {
                    return error;
                }

                return Handle(
                    server: server,
                    actor: context.ActingPrincipal(),
                    args: args,
                    exclusiveAllowed: true,
                    revoke: false
                );
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.revoke",
            description: "Revokes a capability from a principal: world.revoke <principal> <capability> <subject>. Same token grammar as world.grant, minus the trailing tokens (exclusive/budget/channels/ceiling do not apply — a revoke matches by (principal, capability, subject) alone, which also clears any budget, channel reach, or authored pool ceilings the row carried; revoking a seat's own drive row is the only way to clear its ceilings). Applies at submit; the body/section then denies that principal's writes loudly.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.revoke"
                )) {
                    return error;
                }

                return Handle(
                    server: server,
                    actor: context.ActingPrincipal(),
                    args: args,
                    exclusiveAllowed: false,
                    revoke: true
                );
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.grants",
            description: "Echoes the grant table (Immediate; the stdin barrier makes it read the settled table after any pending grant): world.grants [principal]. With a principal token it lists only that principal's rows. An exclusive grant is tagged (x); a row carrying a dispatch budget is suffixed 'budget:<n>', and a row carrying a mask is suffixed 'verbs:<Name,...>' (mutation kinds) or 'writes:<Name,...>' (cross-document Set/Add) BY NAME — the same spelling world.grant's own tokens take, so a read-back and the line that authored it never disagree. A second [world.grants.document: ...] group follows when the world document's own grants section carries document:<id> rows: those are NEVER live-table rows (the cross-document durable-state write-back channel reads them off the OWNER'S DOCUMENT, so the table would hold them budget-less, mask-less, and enforced by nothing), so they are echoed where they actually live rather than seated where nothing reads them. It is omitted entirely when there are none.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var resolveError,
                    server: out var server,
                    verb: "world.grants"
                )) {
                    return resolveError;
                }

                if (args.Count > 1) {
                    return Usage(
                        form: "[principal]",
                        verb: "world.grants"
                    );
                }

                WorldPrincipal? filter = null;

                if (args.Count == 1) {
                    if (TryParsePrincipal(
                        token: args[0],
                        principal: out var principal
                    )) {
                        filter = principal;
                    } else {
                        return CommandResult.Error(output: $"[world.grants: unknown principal '{args[0].ToString()}' — seat1..seat4|console|addon:<name>|peer:<n>:<generation>|document:<id>|group:<id>]");
                    }
                }

                return new CommandResult(Output: (server.Grants.Describe(filter: filter) + DescribeDocumentRows(
                    definition: server.Definition,
                    filter: filter
                )));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.why",
            description: "Echoes WHICH RULE decides an authority check (Immediate; reads the settled table behind the stdin barrier): world.why <principal> <capability> <subject> [verbs:<name,...>] [writes:<name,...>]. Same token grammar as world.grant (minus the mutating trailing tokens). The answer is the check's own verdict — reserver-match | beaten-by-reserver (naming the reserver) | concrete-hold | wildcard-hold | no-hold — so 'denied' stops being one indistinguishable state, and a surface with NO denial line at all can be positively cleared ('authority was fine, look elsewhere') instead of investigated. The pipe-assertable attribution read (the capability-channels campaign's 'A decision is data, never a boolean'). With a trailing verbs:<name,...> token on a mutate or edit check, additionally names the DECIDING row's kind mask (ConcreteHold beats WildcardHold, same as the bare check) and reports each queried kind as admitted or denied-by-mask — a row carrying NO mask admits every kind, since the mask is opt-in narrowing. writes:<name,...> does the same over the cross-document Set/Add vocabulary, where an ABSENT mask instead admits nothing.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var resolveError,
                    server: out var server,
                    verb: "world.why"
                )) {
                    return resolveError;
                }

                if (!TryParseGrant(
                    args: args,
                    exclusiveAllowed: false,
                    verb: "world.why",
                    grant: out var query,
                    error: out var error,
                    channels: server.Population.Channels,
                    targets: server.Population.TargetRegisters,
                    verbsAllowed: true
                )) {
                    return error;
                }

                // The WORLD's own authored program is answered for HONESTLY rather than through the table it never
                // consults: WorldServer.TryAdmitMutation admits it before any lookup, so reporting a NoHold verdict
                // here would be a true statement about the table and a false one about what happens. This is the
                // read-back side of the structural exemption — a decision nothing can echo can only be inferred.
                if (query.Principal.Kind == PrincipalKind.World) {
                    return new CommandResult(Output: $"[world.why: world {query.Capability.ToString().ToLowerInvariant()} {query.Subject.Describe()} = allowed (structural) — the world's own authored program (a rule's effects, a kit's generate effect) is the document acting on itself, not an actor submitting: the grant table is never consulted for it and holds no rows for it. Every other gate still runs: compose, whole-document validate, envelope, solids. To change what it does, change the document — authoring a rule takes mutate section:rules, authoring a kit takes mutate section:kits.]");
                }

                // The DOCUMENT principal's sibling honesty branch: it holds no LIVE row either (the grant door
                // refuses one as inert), so the table's NoHold verdict would be a true statement about the table and
                // a useless one about where the capability actually lives.
                if (query.Principal.Kind == PrincipalKind.Document) {
                    return new CommandResult(Output: $"[world.why: {query.Principal.Describe()} {query.Capability.ToString().ToLowerInvariant()} {query.Subject.Describe()} = not-in-this-table — a document holds no live grant rows: the cross-document durable-state write-back channel reads its rows off the OWNER identity's OWN document grants section (Server.WorldOwnedWorlds.Decide), never off this world's live table, and the grant door refuses a live row for it as accepted-and-inert. world.grants {query.Principal.Describe()} echoes the authored rows where they actually live; world.grant.set/world.grant.remove (and chat.allow/chat.block) author them.]");
                }

                // CC/DEATH GATING (composition-core, Seam A) is checked FIRST, ahead of the ordinary Allows() call —
                // the SAME order WorldServer.ApplyIntentSubmission checks in — so this read-back can never disagree
                // with the door: a Drive/body query against a gated body is answered by the state fact, not by
                // whatever the grant table would otherwise say.
                var verdict = (((query.Capability == WorldCapability.Drive) && (query.Subject.Kind == GrantSubjectKind.Body) && server.Grants.TryGetDriveGate(
                    bodyIndex: query.Subject.Value,
                    gateRow: out var gateRow
                ))
                    ? new GrantVerdict(
                        Rule: GrantRule.DriveGated,
                        GateRow: gateRow
                    )
                    : server.Grants.Allows(
                        principal: query.Principal,
                        capability: query.Capability,
                        subject: query.Subject
                    )
                );
                // A row-scoped mutate query is answered in the SAME order WorldServer.TryAdmitMutation decides it —
                // the owning section's hold FIRST, the concrete row only when that misses — so a principal holding
                // the section reads 'allowed' here instead of a table-true, door-false 'no-hold' over the row.
                var answeredSubject = query.Subject;
                var rowScopedSection = (((query.Capability == WorldCapability.Mutate) && (query.Subject.Kind is GrantSubjectKind.Creation or GrantSubjectKind.Placement))
                    ? GrantSubject.Section(section: ((query.Subject.Kind == GrantSubjectKind.Creation)
                    ? WorldSection.Creations
                    : WorldSection.Placements))
                    : ((GrantSubject?)null)
                );

                if (rowScopedSection is { } owningSection) {
                    var sectionVerdict = server.Grants.Allows(
                        principal: query.Principal,
                        capability: WorldCapability.Mutate,
                        subject: owningSection
                    );

                    if (sectionVerdict.IsAllowed) {
                        verdict = sectionVerdict;
                        answeredSubject = owningSection;
                    }
                }
                var detail = verdict.Rule switch {
                    GrantRule.ReserverMatch => "the principal holds the exclusive reservation over this subject; every other principal is denied there",
                    GrantRule.BeatenByReserver => $"exclusively reserved by {(verdict.Reserver?.Describe() ?? "?")} — the reservation overrides every grant, including a row the principal may genuinely hold (world.grants {args[0].ToString()} lists its rows)",
                    GrantRule.ConcreteHold => "a row names this subject directly",
                    GrantRule.WildcardHold => "the 'all' wildcard row covers it",
                    // The group-expansion fallback: the caller holds NO row of its own here — it is a CURRENT member
                    // of group:<id>, whose OWN row names this subject or its wildcard, read fresh every check (world.groups
                    // group:{Group} lists its current roster; leaving is what makes this answer flip on the NEXT check).
                    GrantRule.GroupHold => $"the caller holds no row of its own, but is a current member of group:{verdict.Group}, whose own row names this subject or its wildcard (world.groups {verdict.Group} lists its roster) — checked FRESH every time, never latched",
                    // The ownership-expansion fallback — the SAME shape as GroupHold, sourced from a document-authored
                    // OWNERSHIP binding rather than a membership row: ownership is a deciding FACT this door consults,
                    // never a grant of its own.
                    GrantRule.OwnershipHold => $"the caller holds no row of its own and is not a reaching member, but OWNS group:{verdict.Group} (an ownership binding, never a grant), whose own row names this subject or its wildcard (world.groups {verdict.Group} lists its roster) — checked FRESH every time, never latched",
                    // Seam A: a STATE FACT, not a grant — refused regardless of whatever the table below would have
                    // answered, including an exclusive reservation the principal genuinely holds.
                    GrantRule.DriveGated => $"body:{query.Subject.Value} carries a nonzero cell on drive-gate row '{verdict.GateRow}' (world.state {verdict.GateRow} shows it) — refused regardless of any Drive hold until that cell reads zero again; DOOR-READS-STATE, never a grant",
                    GrantRule.NoHold => "no row of the principal's set for this capability names the subject, and no wildcard covers it",
                    _ => "?",
                };
                // Which subject the answer actually rests on: for a row-scoped mutate query that is the owning
                // section when its coarse hold carried the check, and the row itself otherwise. The two are the same
                // subject for every other query, and the fragment stays empty there.
                var via = ((rowScopedSection is { } named)
                    ? (!verdict.IsAllowed
                    ? $" — neither mutate {named.Describe()} nor this row is held"
                    : ((answeredSubject == named)
                    ? $" via mutate {named.Describe()}, the section hold, which admits every row it carries"
                    : $" via the row hold alone — mutate {named.Describe()} is not held, so no other row of that section is reachable"))
                    : string.Empty
                );
                var output = $"[world.why: {query.Principal.Describe()} {query.Capability.ToString().ToLowerInvariant()} {query.Subject.Describe()} = {(verdict.IsAllowed
                    ? "allowed"
                    : "denied")} ({verdict.Describe()}){via} — {detail}]";

                if (
                    verdict.IsAllowed &&
                    (query.Capability == WorldCapability.Drive)
                ) {
                    var rawHold = server.Grants.HoldCeiling(
                        principal: query.Principal,
                        subject: query.Subject
                    );
                    var seconds = ((double)FixedQ4816.FromRawBits(value: rawHold));

                    output += string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $" hold:{seconds:0.###}s engine-backstop:{WorldBody.MaxActionHoldSeconds:0.###}s"
                    );
                }

                // The DECIDING row's mask governs, exactly as the live dispatch and Edit doors decide it: a
                // ConcreteHold verdict reads the concrete row's mask, a WildcardHold verdict reads the wildcard
                // row's, and a row-scoped mutate query reads whichever of the section/row rows actually carried the
                // check — never a union, and never the queried row when a different one decided.
                var decidingSubject = ((verdict.Rule == GrantRule.WildcardHold)
                    ? GrantSubject.All
                    : answeredSubject
                );

                if (query.KindMask is { } queriedKinds) {
                    var hasMask = server.Grants.TryGetKindMask(
                        principal: query.Principal,
                        capability: query.Capability,
                        subject: decidingSubject,
                        out var deciding
                    );
                    var coverage = new StringBuilder();

                    foreach (var entry in WorldMutationKindCatalog.All()) {
                        if (!queriedKinds.Contains(ordinal: entry.Ordinal)) {
                            continue;
                        }

                        // An UNMASKED row admits every kind (the mask is opt-in narrowing, not a second gate), so
                        // it reads 'admitted' rather than 'denied-by-mask' — the same rule the Edit and addon
                        // dispatch doors enforce, said once here so the diagnostic cannot disagree with the door.
                        coverage.Append(value: ((coverage.Length == 0)
                            ? ""
                            : ", ")).Append(value: entry.Type.Name).Append(value: ':').Append(value: ((!hasMask || deciding.Contains(ordinal: entry.Ordinal))
                            ? "admitted"
                            : "denied-by-mask"));
                    }

                    output += $" verbs: {(hasMask
                        ? $"mask on {query.Capability.ToString().ToLowerInvariant()} {decidingSubject.Describe()} admits {deciding.Describe()}; "
                        : "(deciding row carries no mask — every kind admitted) ")}{coverage}";
                }

                if (query.WriteMask is { } queriedWrites) {
                    var hasWrites = server.Grants.TryGetWriteMask(
                        principal: query.Principal,
                        capability: query.Capability,
                        subject: decidingSubject,
                        out var decidingWrites
                    );
                    var coverage = new StringBuilder();

                    foreach (var kind in Enum.GetValues<WorldDocumentWriteKind>()) {
                        if (!queriedWrites.Contains(kind: kind)) {
                            continue;
                        }

                        coverage.Append(value: ((coverage.Length == 0)
                            ? ""
                            : ", ")).Append(value: kind.ToString()).Append(value: ':').Append(value: ((hasWrites && decidingWrites.Contains(kind: kind))
                            ? "admitted"
                            : "denied-by-mask"));
                    }

                    // Unlike verbs:, an ABSENT write mask denies: the cross-document channel's mask is what admits a
                    // foreign write at all (WorldOwnedWorlds.Decide), never an optional narrowing of something
                    // already admitted.
                    output += $" writes: {(hasWrites
                        ? $"mask admits {decidingWrites.Describe()}; "
                        : "(deciding row carries no write mask — the cross-document channel admits nothing) ")}{coverage}";
                }

                return new CommandResult(Output: output);
            }
        );
    }
}
