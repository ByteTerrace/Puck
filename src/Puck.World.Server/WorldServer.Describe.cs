using Puck.Commands;
using System.Globalization;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // The `world.properties` read-back: with no body index, the declared vocabulary; with one, which registered
    // properties are currently ON for that carrier (a nonzero cell at key=<bodyIndex>) — resolved through
    // WorldStateReader.TryRead, the SAME (row, key) read a rule's gate and world.state itself run, so this cannot
    // report a tag the engine would not have read.
    public string DescribeProperties(int? bodyIndex) {
        var names = (m_document.Definition.Properties?.Names ?? []);

        if (bodyIndex is not { } index) {
            return ((names.Count == 0)
                ? "[world.properties: none]"
                : $"[world.properties: {string.Join(
                    separator: ", ",
                    values: names
                )}]"
            );
        }

        if (
            (index < 0) ||
            (index >= m_document.Definition.Population.Capacity)
        ) {
            return $"[world.properties {index}: outside 0..{(m_document.Definition.Population.Capacity - 1)} for the authored population capacity]";
        }

        var tick = (NextInputTick - 1UL);
        var key = IndexKeyCache.Get(index: index);
        var tags = new List<string>();

        foreach (var name in names) {
            if (
                WorldStateReader.TryRead(
                definition: m_document.Definition,
                key: key,
                rawValue: out var raw,
                row: out _,
                rowName: name,
                text: out _,
                tick: tick,
                engineTick: CompletedEngineTicks
            ) &&
                (raw is { } value) &&
                (value != 0)
            ) {
                tags.Add(item: name);
            }
        }

        return $"[world.properties {index}: {((tags.Count == 0)
            ? "none"
            : string.Join(
                separator: ", ",
                values: tags
            ))}]";
    }
    // The `world.rules` read-back. An `all` gate prints ITS PREDICATES, never a List type name — the whole reason a
    // compiled conjunct carries its authored spelling beside its resolved form.
    //
    // `latch=held|open` is m_ruleGateHeld, and the KEY names what the values say: the gate-held latch is HELD when
    // the gate held at the last evaluation (an Edge rule will not fire again until it lets go) and OPEN when it did
    // not (so the next tick the gate holds is a crossing, and an Edge rule fires). It read `armed=` before, which
    // inverted the sense it implied — a latch reading `armed=open` is the state in which an edge rule IS armed.
    public string DescribeRules() => DescribeCompiledRules(
        catalog: m_arena.Catalog,
        latch: m_ruleHost.RuleGateHeld,
        rules: m_ruleHost.Rules,
        verb: "world.rules"
    );
    // The dependents a placement-removal guard names: every speaker anchored to the placement (null = none).
    public static string? DescribeSpeakersAnchoredTo(IReadOnlyList<WorldSpeaker> speakers, string placementId) {
        List<string>? names = null;

        foreach (var speaker in speakers) {
            if (
                (speaker is WorldSpeaker.Anchored { Anchor: WorldAnchor.Placement anchor }) &&
                string.Equals(
                a: anchor.PlacementId,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                (names ??= new List<string>()).Add(item: $"'{speaker.Name}'");
            }
        }

        return ((names is null)
            ? null
            : string.Join(
                separator: ", ",
                values: names
            )
        );
    }
    // The dependents a tune/patch-removal guard names among speaker feeds (null = none).
    public static string? DescribeSpeakersSourcing(IReadOnlyList<WorldSpeaker> speakers, Func<WorldSpeakerSource, bool> matches) {
        List<string>? names = null;

        foreach (var speaker in speakers) {
            if (
                (speaker.Feed?.Source is { } source) &&
                matches(arg: source)
            ) {
                (names ??= new List<string>()).Add(item: $"'{speaker.Name}'");
            }
        }

        return ((names is null)
            ? null
            : string.Join(
                separator: ", ",
                values: names
            )
        );
    }
    // A short mutation label for the accept/reject console line — the kind plus its stable-id subject.
    public static string Describe(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertKit m => $"UpsertKit '{m.Kit.Name}'",
        WorldMutation.RemoveKit m => $"RemoveKit '{m.Name}'",
        WorldMutation.SetDefaultSeatKit m => $"SetDefaultSeatKit '{m.Name}'",
        WorldMutation.SetKitAssignment m => $"SetKitAssignment '{m.Assignment.Sequence.Name}'",
        WorldMutation.UpsertScreen m => $"UpsertScreen {m.Screen.Index}",
        WorldMutation.RemoveScreen m => $"RemoveScreen {m.Index}",
        WorldMutation.UpsertMachine m => $"UpsertMachine '{m.Machine.Name}'",
        WorldMutation.RemoveMachine m => $"RemoveMachine '{m.Name}'",
        WorldMutation.UpsertCamera m => $"UpsertCamera '{m.Camera.Name}'",
        WorldMutation.RemoveCamera m => $"RemoveCamera '{m.Name}'",
        WorldMutation.SetSpawns => "SetSpawns",
        WorldMutation.SetMotion => "SetMotion",
        WorldMutation.SetPopulationDefaults => "SetPopulationDefaults",
        WorldMutation.SetPopulationDistribution => "SetPopulationDistribution",
        WorldMutation.SetPopulationCensus => "SetPopulationCensus",
        WorldMutation.SetRenderDefaults => "SetRenderDefaults",
        WorldMutation.UpsertAddon m => $"UpsertAddon '{m.Addon.Name}'",
        WorldMutation.RemoveAddon m => $"RemoveAddon '{m.Name}'",
        WorldMutation.UpsertBindingOverlay m => $"UpsertBindingOverlay '{m.Overlay.Id}'",
        WorldMutation.RemoveBindingOverlay m => $"RemoveBindingOverlay '{m.Id}'",
        WorldMutation.UpsertCreation m => $"UpsertCreation '{m.Creation.Id}'",
        WorldMutation.RemoveCreation m => $"RemoveCreation '{m.Id}'",
        WorldMutation.UpsertPlacement m => $"UpsertPlacement '{m.Placement.Id}'",
        WorldMutation.RemovePlacement m => $"RemovePlacement '{m.Id}'",
        WorldMutation.SetAuthoringDefaults => "SetAuthoringDefaults",
        WorldMutation.UpsertSpeaker m => $"UpsertSpeaker '{m.Speaker.Name}'",
        WorldMutation.RemoveSpeaker m => $"RemoveSpeaker '{m.Name}'",
        WorldMutation.UpsertTune m => $"UpsertTune '{m.Tune.Name}'",
        WorldMutation.RemoveTune m => $"RemoveTune '{m.Name}'",
        WorldMutation.UpsertPatch m => $"UpsertPatch '{m.Patch.Name}'",
        WorldMutation.RemovePatch m => $"RemovePatch '{m.Name}'",
        WorldMutation.SetAudioDefaults => "SetAudioDefaults",
        WorldMutation.SetCollision => "SetCollision",
        WorldMutation.SetHostDefaults => "SetHostDefaults",
        WorldMutation.SetViewDefaults => "SetViewDefaults",
        WorldMutation.SetViewSeatRig => "SetViewSeatRig",
        WorldMutation.SetViewSeatControl => "SetViewSeatControl",
        WorldMutation.SetViewPost m => $"SetViewPost {m.Post.Count}",
        WorldMutation.SetPlayerDefaults => "SetPlayerDefaults",
        WorldMutation.SetPlayerSeatLook => "SetPlayerSeatLook",
        WorldMutation.UpsertViewLayout m => $"UpsertViewLayout '{m.Layout.Name}'",
        WorldMutation.RemoveViewLayout m => $"RemoveViewLayout '{m.Name}'",
        WorldMutation.UpsertViewGraph m => $"UpsertViewGraph '{m.Graph.Name}'",
        WorldMutation.RemoveViewGraph m => $"RemoveViewGraph '{m.Name}'",
        WorldMutation.CommitViewGraph m => $"CommitViewGraph '{m.Name}'",
        WorldMutation.UpsertLook m => $"UpsertLook '{m.Look.Name}'",
        WorldMutation.RemoveLook m => $"RemoveLook '{m.Name}'",
        WorldMutation.UpsertDynamics m => $"UpsertDynamics '{m.Row.Name}'",
        WorldMutation.RemoveDynamics m => $"RemoveDynamics '{m.Name}'",
        WorldMutation.UpsertCurve m => $"UpsertCurve '{m.Row.Name}'",
        WorldMutation.RemoveCurve m => $"RemoveCurve '{m.Name}'",
        WorldMutation.SetLookAssignment m => $"SetLookAssignment '{m.Assignment.Sequence.Name}'",
        WorldMutation.UpsertGrant m => $"UpsertGrant {m.Row.Grantee.Describe()} {m.Row.Capability.ToString().ToLowerInvariant()} {m.Row.Subject.Describe()}",
        WorldMutation.RemoveGrant m => $"RemoveGrant {m.Target.Grantee.Describe()} {m.Target.Capability.ToString().ToLowerInvariant()} {m.Target.Subject.Describe()}",
        WorldMutation.UpsertHudPanel m => $"UpsertHudPanel '{m.Panel.Id}'",
        WorldMutation.RemoveHudPanel m => $"RemoveHudPanel '{m.Id}'",
        WorldMutation.UpsertHudElement m => $"UpsertHudElement '{m.PanelId}'.'{m.Element.Id}'",
        WorldMutation.RemoveHudElement m => $"RemoveHudElement '{m.PanelId}'.'{m.ElementId}'",
        WorldMutation.SetHudDefaults => "SetHudDefaults",
        WorldMutation.TransformState => "TransformState",
        WorldMutation.Batch m => $"Batch[{string.Join(
        separator: ", ",
        values: m.Mutations.Select(selector: Describe)
    )}]",
        WorldMutation.UpsertStateRow m => $"UpsertStateRow '{m.Row.Name}'",
        WorldMutation.RemoveStateRow m => $"RemoveStateRow '{m.Name}'",
        WorldMutation.UpsertStateCell m => $"UpsertStateCell '{m.Row}'.'{m.Key}'",
        WorldMutation.RemoveStateCell m => $"RemoveStateCell '{m.Row}'.'{m.Key}'",
        WorldMutation.SetInputHold => "SetInputHold",
        WorldMutation.Generate m => $"Generate '{m.Row}'",
        WorldMutation.UpsertWorldRule m => $"UpsertWorldRule '{m.Rule.Name}'",
        WorldMutation.RemoveWorldRule m => $"RemoveWorldRule '{m.Name}'",
        WorldMutation.UpsertGroupKind m => $"UpsertGroupKind '{m.Kind.Name}'",
        WorldMutation.RemoveGroupKind m => $"RemoveGroupKind '{m.Name}'",
        WorldMutation.FormGroup m => $"FormGroup '{m.Id}' kind '{m.KindName}'",
        WorldMutation.JoinGroup m => $"JoinGroup '{m.GroupId}' <- {m.Member.Describe()}",
        WorldMutation.LeaveGroup m => $"LeaveGroup '{m.GroupId}' <- {m.Member.Describe()}",
        WorldMutation.KickMember m => $"KickMember '{m.GroupId}' <- {m.Member.Describe()}",
        WorldMutation.OfferOwnership m => $"OfferOwnership '{m.Subject.Describe()}' {m.Principal.Describe()} -> escrow(recipient={m.Recipient.Describe()},deadline={m.DeadlineTick})",
        WorldMutation.SettleOwnership m => (m.Reclaim
        ? $"SettleOwnership '{m.Subject.Describe()}' reclaim by {m.Principal.Describe()}"
        : $"SettleOwnership '{m.Subject.Describe()}' accept by {m.Principal.Describe()}"),
        WorldMutation.SetProperty m => (m.Remove
        ? $"RemoveProperty '{m.Name}'"
        : $"UpsertProperty '{m.Name}'"),
        WorldMutation.UpsertInteraction m => $"UpsertInteraction '{m.Interaction.Name}'",
        WorldMutation.RemoveInteraction m => $"RemoveInteraction '{m.Name}'",
        _ => "unknown",
    };
    /// <summary>Composes the <c>body.channels</c> echo — the fold and held-image join's read-back
    /// (the arithmetic rule lives in <see cref="FixedContributionFold"/>), so a script can tell "the addon asked for more
    /// and the pool held it" apart from "the addon asked for exactly this" without inferring it from displacement
    /// across ticks. Reports every declared channel of <paramref name="bodyIndex"/>'s last write: the folded value
    /// the simulation received, the owning seat's own base <c>h</c>, every contributor that reached it tagged by
    /// principal (trusted/untrusted), the pool ceiling in force, whether the pool actually clamped, the held overlay
    /// admitted later by <see cref="WorldBody"/>, and the value after that overlay composed with the movement tier.</summary>
    /// <param name="bodyIndex">The 0-based body index already resolved to a live body.</param>
    /// <param name="body">The live body retaining the later held-overlay decision.</param>
    public string DescribeChannels(int bodyIndex, WorldBody body) {
        // The fold — and this read-back — only ever exists over a HUMAN-OCCUPIED LOCAL SEAT
        // (WorldPopulation.IsHumanOccupied; the whole per-seat retention above is sized WorldPopulation.LocalSeatCount).
        // A peer-slice population entry (4 through capacity minus one) or an unoccupied local seat is a bot at full authority by construction — there
        // is no base/pool/contributor to report, so say that rather than fabricating one.
        if (!m_population.IsHumanOccupied(bodyIndex: bodyIndex)) {
            return $"[body.channels: body:{bodyIndex} is not human-occupied — the co-driving pool only ever exists over an occupied local seat (see world.population); nothing folds here]";
        }

        // The application-set summary: every target this seat's channels reach, each with its kit and reach mask, so
        // the same read-back that already shows the fold shows the whole engagement truth beside it (CLAUDE.md's
        // read-back rule: no decision surface without an echoing verb). The own-body member is listed like any
        // other, so its ABSENCE — capture — is legible rather than inferred.
        var applyPrincipal = Principal.Seat(slot: bodyIndex);
        var applications = m_grants.Applications(principal: applyPrincipal);
        var routeText = $"applications={((applications.Count == 0)
            ? "none"
            : string.Join(
                separator: ",",
                values: applications.Select(selector: static application => application.Describe())
            ))}";

        var channels = m_population.Channels;
        var h = m_tick.ChannelReadBase[bodyIndex];
        var folded = m_tick.ChannelReadFolded[bodyIndex];
        var held = body.ChannelReadHeld;
        var composed = body.ChannelReadComposed;
        var baseSlot = (bodyIndex * ChannelLimits.MaxChannels);
        var contributorBase = (bodyIndex * WorldTick.MaxReadContributorsPerSeat);
        var contributorCount = m_tick.ChannelReadContributorCount[bodyIndex];
        var segments = new List<string>(capacity: ChannelLimits.MaxChannels);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (!channels.IsDeclared(ordinal: ordinal)) {
                continue;
            }

            var slot = (baseSlot + ordinal);
            var trustedTags = new List<string>();
            var untrustedTags = new List<string>();

            for (var contributor = 0; (contributor < contributorCount); contributor++) {
                var contributorSlot = (contributorBase + contributor);

                if (!m_tick.ChannelReadContributorMask[contributorSlot].Contains(ordinal: ordinal)) {
                    continue;
                }

                (m_tick.ChannelReadContributorTrusted[contributorSlot]
                    ? trustedTags
                    : untrustedTags).Add(item: m_tick.ChannelReadContributor[contributorSlot].Describe());
            }

            var ceiling = m_tick.ChannelReadCeiling[slot];

            segments.Add(item: $"{channels.Name(ordinal: ordinal)}:{ShapeWord(shape: channels.Shape(ordinal: ordinal))} folded={folded[ordinal]}({folded[ordinal].Value}) h={h[ordinal]}({h[ordinal].Value}) held={held[ordinal]}({held[ordinal].Value}) composed={composed[ordinal]}({composed[ordinal].Value}) trusted=[{string.Join(
                separator: ",",
                values: trustedTags
            )}] untrusted=[{string.Join(
                separator: ",",
                values: untrustedTags
            )}] ceiling={((ceiling > 0)
                ? $"{FixedQ4816.FromRawBits(value: ceiling)}({ceiling})"
                : "none")} clamped={(m_tick.ChannelReadClamped[slot]
                ? "yes"
                : "no")}");
        }

        return $"[body.channels: body:{bodyIndex} {routeText} pointer={DescribePointer(ray: composed.SourceRay)} {string.Join(
            separator: " | ",
            values: segments
        )}]";
    }

    // body.channels' pointer read-back: the ray the body integrated this tick and where it maps on every Simulation
    // screen, through the same source-normalized mapping the $pointer: rule read runs.
    private string DescribePointer(SourceRay? ray) {
        if (ray is not { } pointer) {
            return "none";
        }

        static string Vector(FixedVector3 value) => $"({value.X}, {value.Y}, {value.Z})";

        var hits = new List<string>();

        foreach (var screen in Definition.Screens) {
            if (screen.Route.Input != SourceDestination.Simulation) {
                continue;
            }

            var mapping = WorldScreenMappings.Normalized(screen: screen);

            if (!mapping.TryValidate(refusal: out var refusal)) {
                hits.Add(item: $"screen:{screen.Index}=unmappable({refusal})");

                continue;
            }

            var hit = mapping.MapRay(ray: pointer);

            hits.Add(item: (hit.IsOnSource
                ? $"screen:{screen.Index}=on({hit.Coordinate.X}, {hit.Coordinate.Y})"
                : $"screen:{screen.Index}={hit.Outcome}"
            ));
        }

        return $"{Vector(value: pointer.Origin)}->{Vector(value: pointer.Direction)}[{string.Join(
            separator: ",",
            values: hits
        )}]";
    }
    // Shared by DescribeRules/DescribeInteractions: an `all` gate prints ITS PREDICATES, never a List type name — the
    // whole reason a compiled conjunct carries its authored spelling beside its resolved form.
    //
    // `latch=held|open` is HELD when the gate held at the last evaluation (an Edge row will not fire again until it
    // lets go) and OPEN when it did not (so the next tick the gate holds is a crossing, and an Edge row fires).
    private static string DescribeCompiledRules(string verb, CompiledRule[] rules, RuleLatch latch, StateCatalog catalog) {
        if (rules.Length == 0) {
            return $"[{verb}: none]";
        }

        var lines = new List<string>(capacity: rules.Length);

        foreach (var rule in rules) {
            var gate = ((rule.Gate.Length == 0)
                ? "always"
                : string.Join(
                    separator: " and ",
                    values: rule.Gate.Select(selector: static predicate => predicate.Describe)
                )
            );
            var effects = string.Join(
                separator: "; ",
                values: rule.Effects.Select(selector: static effect => effect.Describe)
            );

            var world = (rule as CompiledWorldFactsRule);

            if (world?.Decision is { } decision) {
                lines.Add(item: $"{rule.Name} decision={decision.Mode} options={decision.Options.Length} when {gate} -> common [{effects}]; choices/timers: world.decisions");
                continue;
            }

            var held = latch.Held(name: rule.Name);
            var boundValues = ((rule.Locals is { Length: > 0 } declared)
                ? $" local [{string.Join(
                    separator: ", ",
                    values: declared.Select(selector: static b => $"{b.Name}:{b.Kind.ToString().ToLowerInvariant()}")
                )}]"
                : string.Empty
            );
            var scope = ((world?.Interaction is { } interaction)
                ? $" {interaction.CoOccurrence.ToString().ToLowerInvariant()} {interaction.Left} x {interaction.Right}{((interaction.CoOccurrence == WorldInteractionCoOccurrence.Distance)
                    ? $" <= {((double)interaction.Range)}"
                    : string.Empty)}"
                : ((rule.ForEachOrdinal >= 0)
                    ? $" forEach {catalog.Descriptors[rule.ForEachOrdinal].Name}"
                    : string.Empty
            ));

            lines.Add(item: $"{rule.Name} mode={rule.Mode.ToString().ToLowerInvariant()}{scope}{boundValues} latch={(held
                ? "held"
                : "open")} when {gate} -> {effects}");
        }

        return $"[{verb}: {string.Join(
            separator: " | ",
            values: lines
        )}]";
    }

    public static string DescribeContacts(int index, WorldBody body) {
        var normal = body.LastObstructionNormal;
        var obstruction = ((normal == FixedVector3.Zero)
            ? "none"
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"({((double)normal.X):0.###},{((double)normal.Y):0.###},{((double)normal.Z):0.###})"
            )
        );

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.contacts: p{index} grounded={(body.Grounded
            ? "true"
            : "false")} planarSpeed={body.PlanarSpeed:0.00} resolved={body.ContactCount} inMedium={(body.InMedium
            ? "true"
            : "false")} atMediumBand={(body.AtMediumBand
            ? "true"
            : "false")} obstruction={obstruction}]"
        );
    }
    public string DescribeDocumentReceipt(string? ownerId) {
        if (
            (m_document.LastDocumentReceipt is not { } receipt) ||
            !string.Equals(
            a: receipt.Submission.OwnerDocumentId,
            b: ownerId,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return "none";
        }
        return $"{receipt.Submission.Kind.ToString().ToLowerInvariant()}:{receipt.Submission.Slot}@{receipt.Submission.Tick}:{(receipt.Accepted
            ? "accepted"
            : "refused")}({receipt.Reason})";
    }
    public string DescribeDurableOutputs(int entityIndex) {
        var values = m_population.DurableStateOutputs
            .Where(predicate: output => (output.EntityIndex == entityIndex))
            .Select(selector: output => $"{output.Value.Name}@{output.Tick}");
        var text = string.Join(
            separator: ",",
            values: values
        );

        return ((text.Length == 0)
            ? "none"
            : text
        );
    }
    // The `world.interactions` read-back — the SAME line shape DescribeRules gives a compiled rule, since an
    // interaction IS a compiled rule under the hood (see WorldFactsCompiler.CompileAllInteractions). This is also the
    // "echo an interaction firing" read-back the effect substrate promises: `latch=held` at the last evaluation IS
    // "this interaction fired (or is still holding, under Level)", the identical signal a rule's own latch already
    // gives.
    public string DescribeInteractions() => DescribeCompiledRules(
        catalog: m_arena.Catalog,
        latch: m_ruleHost.InteractionGateHeld,
        rules: m_ruleHost.Interactions,
        verb: "world.interactions"
    );
    // Every dependent a patch-removal guard names: synth-sourced speakers plus placement emission facets
    // (creation sounds carry their patches INLINE, so they can never dangle). Null = none.
    public static string? DescribePatchDependents(WorldDefinition current, string patchId) {
        List<string>? dependents = null;

        if (DescribeSpeakersSourcing(
            speakers: current.Speakers,
            matches: source => ((source is WorldSpeakerSource.Synth synth) && string.Equals(
                a: synth.PatchId,
                b: patchId,
                comparisonType: StringComparison.Ordinal
            ))
        ) is { } speakers) {
            (dependents ??= new List<string>()).Add(item: $"speaker(s) {speakers}");
        }

        foreach (var placement in current.Placements) {
            if (
                (placement.Emission is { } emission) &&
                string.Equals(
                a: emission.PatchId,
                b: patchId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                (dependents ??= new List<string>()).Add(item: $"placement '{placement.Id}'");
            }
        }

        return ((dependents is null)
            ? null
            : string.Join(
                separator: ", ",
                values: dependents
            )
        );
    }

    // The lowercase shape word the fold's own read-back names a channel's shape with — the single place these words
    // are produced, never re-derived elsewhere.
    private static string ShapeWord(ChannelShape shape) => shape switch {
        ChannelShape.Unipolar => "unipolar",
        ChannelShape.Binary => "binary",
        _ => "bipolar",
    };
}
