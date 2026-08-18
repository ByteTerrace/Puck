using System.Text.Json;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The World-side per-seat <see cref="IInputBindings"/> the <see cref="InputRouter"/> resolves through. It holds one
/// <see cref="PagedInputBindings"/> per local seat, each compiled from that
/// seat's composed document (the world's overlays ⊕ the seat's profile bindings ⊕ its live session rebinds — a
/// world authoring none binds nothing). Composition and compilation happen only on a change (a profile selection, a
/// rebind, an overlay mutation) — never per frame; the per-signal resolve path stays the existing paged lookups. A
/// seat's runtime mode is not a layer: every group is always compiled in, and the seat's active group derives as
/// <c>first matching context row's group (document order) ?? the profile's default group (the first row's)</c>,
/// applied as a pointer-level switch on the compiled profile. Context rows are the composed document's
/// <c>contexts</c> section keyed on the published per-seat family states (<see cref="WorldContextFamilies"/>) pushed
/// in through <see cref="SetContextState"/> — roster and engagement by the post-step sync, editor by its session,
/// and <c>state:&lt;row&gt;</c> families from the routed definition's delivered state revision, so gameplay rules can
/// switch controls by writing ordinary world state;
/// across families the first matching row wins and a shadowed later match is reported, never silent
/// (<see cref="DescribeContextDerivation"/> — the <c>player.bindings</c> read-back).
/// </summary>
/// <remarks>Single-threaded, like every input-fold type here: recomposition runs on the launcher's window-pump thread
/// (a verb handler, a roster mutation, or the post-step overlay sync), and <see cref="Resolve(int, in InputSignal)"/>
/// runs on the same thread inside the router's snapshot fold. No lock guards this state. Constructed early in
/// composition (before the container is built) from the boot world definition; the per-seat profile and session
/// layers start null, and the roster/verbs push them in as they change. Chord-command edges (<see cref="IChordEdgeSource"/>) forward to the
/// resolving seat's paged bindings.</remarks>
public sealed class WorldSeatBindings : IInputBindings, IChordEdgeSource, IInputBindingsReloadSource {
    // Built-ins are reported first; state-backed families follow in authored context-row order.
    private static readonly string[] BuiltInContextFamilies = [.. WorldContextFamilies.Families];

    /// <summary>The number of local seats this router resolves for.</summary>
    public const int SeatCount = WorldPopulationLimits.LocalSeatCount;

    // The exact channel row list each seat's m_channels entry was compiled from — the second half of SyncSeat's
    // per-seat change test, so a channels-only mutation (which leaves the overlay list reference-equal) still
    // re-derives that seat's table.
    private readonly IReadOnlyList<WorldChannel>[] m_channelSource;
    private readonly WorldChannelTable[] m_channels;
    // Per seat: family name → current published state. Built-ins are always present; state-backed entries exist only
    // while the composed document references them and the routed world declares their row.
    private readonly Dictionary<string, string>[] m_contextStates;
    private readonly string[][] m_effectiveChannelNames;
    // Exact effective-profile identity per seat. Reference changes are common at world-route seams; only a change to
    // the filtered composed document or channel-name ordinal map warrants resetting chord/page/latch state.
    private readonly byte[][] m_effectiveDocuments;
    private readonly WorldDefinition[] m_definitions;
    private readonly int[] m_stateEntityIndices;
    private readonly IReadOnlyList<WorldStateRow>[] m_stateSource;
    private readonly ulong[] m_stateTicks;
    // Per-seat: a seat's binding vocabulary composes from whichever document currently frames it
    // (WorldInstanceHost.ResolveRoutedDefinition's own routed lookup — the same per-seat source
    // WorldSeatViewInput already reads for the pitch clamp), never one world shared by every seat. Every seat
    // starts seeded from the boot definition; SyncSeat re-points one seat's own entries the instant its resolved
    // definition changes reference.
    private readonly IReadOnlyList<WorldBindingOverlay>[] m_overlays;
    private readonly BindingProfileDocument?[] m_profileBindings;
    private readonly IReadOnlyList<BindingContextDefinition>[] m_seatContexts;
    private readonly PagedInputBindings[] m_seats;
    private readonly BindingProfileDocument?[] m_sessionRebinds;

    event Action<int?> IInputBindingsReloadSource.Reloading {
        add => Reloading += value;
        remove => Reloading -= value;
    }

    private event Action<int?>? Reloading;

    private BindingProfileDocument?[] BaseLayers(IReadOnlyList<WorldBindingOverlay> overlays, BindingProfileDocument? profile, BindingProfileDocument? session) {
        var layers = new BindingProfileDocument?[(overlays.Count + 2)];
        var index = 0;

        foreach (var overlay in overlays) {
            layers[index++] = overlay.Document;
        }

        layers[index++] = profile;
        // Live session rebinds compose LAST — the freshest authoring wins within every group.
        layers[index] = session;

        return layers;
    }
    private static string[] ChannelNames(IReadOnlyList<WorldChannel> channels) {
        var names = new string[channels.Count];

        for (var index = 0; (index < names.Length); index++) {
            names[index] = channels[index].Name;
        }

        return names;
    }
    // The out-of-range-slot defensive fallback (ComposedDocument's own guard) — slot 0's own routed overlays stand
    // in since there is no "the" world overlay list any more (each seat carries its own, per SyncSeat).
    private BindingProfileDocument ComposeBase() {
        return WorldBindingComposer.Compose(BaseLayers(
            overlays: m_overlays[0],
            profile: null,
            session: null
        ));
    }
    private BindingProfileDocument ComposeSeat(int slot) {
        return WorldBindingComposer.Compose(BaseLayers(
            overlays: m_overlays[slot],
            profile: m_profileBindings[slot],
            session: m_sessionRebinds[slot]
        ));
    }
    // The derivation, applied: first matching context row's group (document order) ?? null (the profile default). The
    // apply is the existing pointer-level group switch; a winner row's group is declared by construction
    // (BindingProfile.Compile refuses a contexts row naming an undeclared group).
    private void DeriveActiveGroup(int slot, bool releasePriorGroup = true) {
        var (_, _, winnerGroup) = FirstMatch(slot: slot);
        var previousGroup = m_seats[slot].ViewFor(slot: slot).Group;

        _ = m_seats[slot].SetActiveGroup(
            slot: slot,
            group: winnerGroup
        );

        if (
            releasePriorGroup &&
            !string.Equals(
                a: previousGroup,
                b: m_seats[slot].ViewFor(slot: slot).Group,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            // A control-mode switch is a complete input boundary: cancel routed holds/toggles and clear chord/page
            // latches from the old group. A still-held physical source may reassert only continuous input through
            // the new group on a later capture; edge commands never leak across the transition.
            Reloading?.Invoke(obj: slot);
            m_seats[slot].Reset(slot: slot);
        }
    }
    // A channel reference's declared name for a skip narration, quoted — the local twin of ChannelRef.Describe()
    // (internal to Puck.Commands, unreachable from this assembly).
    private static string DescribeChannel(ChannelRef reference) => ((reference is ChannelRef.Name name)
        ? $"\"{name.Value}\""
        : "(reference)"
    );
    // The first context row (document order) whose family currently holds the row's state for the seat — the
    // across-family precedence rule: authored row order, first match wins.
    private (string? Family, string? State, string? Group) FirstMatch(int slot) {
        foreach (var row in m_seatContexts[slot]) {
            if (
                m_contextStates[slot].TryGetValue(
                    key: row.Family,
                    value: out var state
                ) &&
                string.Equals(
                    a: state,
                    b: row.State,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                return (Family: row.Family, State: row.State, Group: row.Group);
            }
        }

        return (Family: null, State: null, Group: null);
    }
    private static Dictionary<string, WorldStateRow> StateRowsByName(IReadOnlyList<WorldStateRow> rows) {
        var result = new Dictionary<string, WorldStateRow>(
            capacity: rows.Count,
            comparer: StringComparer.Ordinal
        );

        foreach (var row in rows) {
            if (row is not null) {
                result[row.Name] = row;
            }
        }

        return result;
    }
    // Publishes only the state-backed families the seat's composed document actually references. Called on a state
    // revision, route/entity change, or binding recompose — never on an unchanged tick.
    private void PublishStateContexts(int slot, ulong tick) {
        var definition = m_definitions[slot];
        var entityIndex = m_stateEntityIndices[slot];

        foreach (var row in m_seatContexts[slot]) {
            if (WorldStateBindingContext.TryRead(
                definition: definition,
                family: row.Family,
                entityIndex: entityIndex,
                tick: tick,
                state: out var state
            )) {
                m_contextStates[slot][row.Family] = state;
            } else if (WorldStateBindingContext.TryParseFamily(
                family: row.Family,
                rowName: out _
            )) {
                _ = m_contextStates[slot].Remove(key: row.Family);
            }
        }
    }
    private void RecomposeSeat(int slot) {
        var label = $"seat {(slot + 1)}";
        // Drop any page (or bare-command/channel row) naming a command outside this composition's registered
        // vocabulary, or a channel this seat's currently routed table cannot carry — the engine-default document
        // always compiles in the editor/sculpt groups, but a headless boot never registers EditorCommandModule, so
        // those pages are unreachable by construction; a channel row goes stale the same way the moment a seat
        // crosses into a world that never declared it. One narration line per skipped page/row, keyed on the
        // registration fact (WorldAffordances.IsCommandRegistered) or the channel-table lookup — a mixed page (e.g.
        // the play group's base page, which folds editor.enter beside its movement rows) keeps its registered and
        // compatible entries and loses only the ones this composition cannot carry. Compatibility includes the
        // destination's shape: a non-default scale valid for an analog channel becomes unavailable when a different
        // world declares the same name as binary, just as surely as when that world omits the name entirely.
        var document = SkipUnregisteredPages(
            document: ComposeSeat(slot: slot),
            channels: m_channels[slot],
            label: label
        );

        // Compile the seat's composed document and hot-swap it. Whatever vocabulary trouble survives the skip above
        // (a bindability or value-kind mismatch — a genuine authoring error, never a crossing gap) still rejects the
        // whole recompose loudly, keeping the seat on its prior mapping rather than taking the input path down. This
        // seat's own channel table — the routed document's, per SyncSeat — never the boot table, so a rebind valid
        // in the world this seat currently presents from is never rejected against a different world's vocabulary.
        if (RejectedByVocabulary(
            document: document,
            channels: m_channels[slot],
            label: label
        )) {
            return;
        }

        var documentBytes = SerializeEffectiveDocument(document: document);
        var channelNames = ChannelNames(channels: m_channelSource[slot]);

        if (
            m_effectiveDocuments[slot].AsSpan().SequenceEqual(other: documentBytes) &&
            m_effectiveChannelNames[slot].AsSpan().SequenceEqual(other: channelNames)
        ) {
            // A new document instance with the same effective profile is a true no-op: keep the live tracker,
            // armed chord rows, release latches, and held commands intact. Context references may be new even
            // though their content is identical, so retain the newest document view for read-back.
            m_seatContexts[slot] = (document.Contexts ?? []);
            DeriveActiveGroup(slot: slot);
            return;
        }

        try {
            var channels = m_channels[slot];

            var profile = BindingProfile.Compile(
                document: document,
                channelCommandName: channel => RoutedChannelCommandName(
                    channel: channel,
                    channels: channels
                )
            );

            Reloading?.Invoke(obj: slot);
            m_seats[slot].Reload(profile: profile);
            m_effectiveDocuments[slot] = documentBytes;
            m_effectiveChannelNames[slot] = channelNames;
        } catch (ArgumentException exception) {
            Console.Error.WriteLine(value: $"[player.bindings] {label} recompose rejected ({exception.Message.ReplaceLineEndings(replacementText: " ")}); keeping the prior mapping.");

            return;
        }

        // The new document's context rows replace the seat's cached set, and the active group re-derives against
        // them (the reload already re-applied the last APPLIED group; the derivation may now pick a different one).
        m_seatContexts[slot] = (document.Contexts ?? []);
        PublishStateContexts(
            slot: slot,
            tick: m_stateTicks[slot]
        );
        DeriveActiveGroup(
            slot: slot,
            releasePriorGroup: false
        );
    }
    // The STRUCTURAL half of the recompose gate, run on whatever survives the skip above: print every finding and
    // keep the prior mapping when the composed document references a command the registry does not carry or sends one
    // the wrong value kind. Route-dependent channel findings are handled row-locally by the skip above: retaining an
    // older compiled ordinal map after SyncSeat has already installed a new channel table would pair input with the
    // wrong destination. WorldAffordances itself skips only the unavailable command-registry half before install;
    // channel and structural context admission remain unconditional; state-row resolution is owned by the world
    // document validator and explicit live-rebind preflight.
    private static bool RejectedByVocabulary(BindingProfileDocument document, WorldChannelTable channels, string label) {
        var errors = new List<string>();

        WorldAffordances.Validate(
            channels: channels,
            document: document,
            errors: errors
        );

        if (errors.Count == 0) {
            return false;
        }

        foreach (var error in errors) {
            Console.Error.WriteLine(value: $"[player.bindings] {label} recompose rejected: {error}; keeping the prior mapping.");
        }

        return true;
    }
    private static ulong CompletedTick(ulong endpointNextInputTick) => ((endpointNextInputTick > 0UL)
        ? (endpointNextInputTick - 1UL)
        : 0UL
    );
    // Runtime lowering for an AUTHORED channel name. The name is resolved only while this seat's composed profile is
    // built, against this seat's currently routed table; the resulting command lives in the fixed ordinal vocabulary
    // PlayerCommandModule registers in full. A late-mounted or remote world therefore needs no command-registry
    // mutation, while a malformed/undeclared name still refuses loudly at the same composition boundary.
    private static string RoutedChannelCommandName(WorldChannelTable channels, ChannelRef channel) {
        if (
            (channel is ChannelRef.Name name) &&
            channels.TryGetOrdinal(
            name: name.Value,
            ordinal: out var ordinal
        )
        ) {
            return PlayerCommandNames.RoutedChannelCommandName(ordinal: ordinal);
        }

        var description = ((channel is ChannelRef.Name unresolved)
            ? $"name:{unresolved.Value}"
            : channel.GetType().Name
        );

        throw new ArgumentException(
            message: $"Channel {description} resolves no declared channel.",
            paramName: nameof(channel)
        );
    }
    private static byte[] SerializeEffectiveDocument(BindingProfileDocument document) {
        return JsonSerializer.SerializeToUtf8Bytes(
            value: document,
            jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument
        );
    }
    // One row's half of the skip: a PAGE keeps its registered/resolvable entries and loses only the ones this
    // composition cannot carry (a mixed page — the play group's base page folds editor.enter beside its movement
    // rows — narrates once per finding and survives with the rest intact); a bare-COMMAND/CHANNEL row has no smaller
    // unit to keep, so an unregistered command or an unresolved channel drops it whole. Returns the row UNCHANGED
    // (by reference — the ReferenceEquals check above skips the allocation) when nothing in it is unregistered or
    // unavailable, or a rewritten row when any page entry is dropped. A page itself remains even when filtering
    // empties it: it may be the group's required resting page, and deleting that structural row would reject the
    // recompose and preserve a prior world's ordinal map. A CHANNEL destination is unavailable when its name is
    // absent OR its authored scale is incompatible with the destination shape. Both are route-dependent facts, so
    // neither may preserve a prior world's compiled ordinal map.
    private static BindingChordDefinition? SkipUnregisteredEntries(BindingChordDefinition row, WorldChannelTable channels, string? label) {
        if (row.Page is { } page) {
            var entries = page.Entries;
            List<BindingPageEntryDefinition>? kept = null;
            var droppedCommandCount = 0;
            List<string>? droppedChannelFindings = null;

            for (var entryIndex = 0; (entryIndex < entries.Count); entryIndex++) {
                var entry = entries[entryIndex];
                var unregisteredCommand = (!string.IsNullOrEmpty(value: entry.Command) && !WorldAffordances.IsCommandRegistered(command: entry.Command));
                var channelFinding = string.Empty;
                var unavailableChannel = ((entry.Channel is { } channelRef) &&
                    TryDescribeUnavailableChannel(
                    channels: channels,
                    reference: channelRef,
                    scale: entry.Scale,
                    finding: out channelFinding
                ));

                if (
                    !unregisteredCommand &&
                    !unavailableChannel
                ) {
                    kept?.Add(item: entry);

                    continue;
                }

                kept ??= [.. entries.Take(count: entryIndex)];

                if (unavailableChannel) {
                    (droppedChannelFindings ??= []).Add(item: channelFinding);
                } else {
                    droppedCommandCount++;
                }
            }

            if (
                (droppedCommandCount == 0) &&
                (droppedChannelFindings is null)
            ) {
                return row;
            }

            if (label is not null) {
                if (droppedCommandCount > 0) {
                    Console.Error.WriteLine(value: $"[player.bindings] {label}: page \"{page.Id}\" (group \"{row.Group}\") skipped {droppedCommandCount} unregistered command{((droppedCommandCount == 1)
                        ? ""
                        : "s")} — its commands are not registered in this composition.");
                }

                if (droppedChannelFindings is not null) {
                    Console.Error.WriteLine(value: $"[player.bindings] {label}: page \"{page.Id}\" (group \"{row.Group}\") skipped {droppedChannelFindings.Count} unavailable channel binding{((droppedChannelFindings.Count == 1)
                        ? ""
                        : "s")} ({string.Join(
                        separator: "; ",
                        values: droppedChannelFindings
                    )}).");
                }
            }

            return (row with { Page = (page with { Entries = kept! }) });
        }

        if (row.Command is { } command) {
            if (command.Channel is { } chordChannel) {
                if (!TryDescribeUnavailableChannel(
                    channels: channels,
                    reference: chordChannel,
                    scale: command.Scale,
                    finding: out var channelFinding
                )) {
                    return row;
                }

                if (label is not null) {
                    Console.Error.WriteLine(value: $"[player.bindings] {label}: row [{string.Join(
                        separator: '+',
                        values: row.Members
                    )}] (group \"{row.Group}\") skipped — its channel binding is unavailable ({channelFinding}).");
                }

                return null;
            }

            if (
                !string.IsNullOrEmpty(value: command.Command) &&
                !WorldAffordances.IsCommandRegistered(command: command.Command)
            ) {
                if (label is not null) {
                    Console.Error.WriteLine(value: $"[player.bindings] {label}: row [{string.Join(
                        separator: '+',
                        values: row.Members
                    )}] (group \"{row.Group}\") skipped — its command is not registered in this composition.");
                }

                return null;
            }
        }

        return row;
    }
    // Drops every page (or bare-command/channel row) naming a command outside the registered vocabulary, or a
    // channel CHANNELS cannot carry, narrating ONE line per skipped row rather than one per offending entry — the
    // composer half of the gate, run BEFORE the still-standing RejectedByVocabulary/ArgumentException gates above so
    // a genuine mistake (bindability or value kind) in what SURVIVES the skip still rejects loudly. Before the
    // command vocabulary is installed, IsCommandRegistered admits every command while the per-world channel half
    // continues to filter normally — the same independent-lookup contract WorldAffordances.Validate exposes. Building
    // a new list only when a row actually changes keeps the common (nothing unregistered/unavailable) recompose
    // allocation-free. A
    // null label filters SILENTLY — ComposedDocument's read-back reads the seat's own recompose (or the boot sweep)
    // already narrated the same finding; echoing it again on every read would trade one flood for another.
    private static BindingProfileDocument SkipUnregisteredPages(BindingProfileDocument document, WorldChannelTable channels, string? label) {
        var rows = document.Chords;
        List<BindingChordDefinition>? rewritten = null;

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            var row = rows[rowIndex];
            var filtered = SkipUnregisteredEntries(
                channels: channels,
                label: label,
                row: row
            );

            if (ReferenceEquals(
                objA: filtered,
                objB: row
            )) {
                rewritten?.Add(item: row);

                continue;
            }

            rewritten ??= [.. rows.Take(count: rowIndex)];

            if (filtered is not null) {
                rewritten.Add(item: filtered);
            }
        }

        var chords = (((IReadOnlyList<BindingChordDefinition>?)rewritten) ?? rows);
        var wheels = SkipUnregisteredWheels(
            wheels: document.Wheels,
            chords: chords,
            label: label
        );

        if (
            (rewritten is null) &&
            ReferenceEquals(
            objA: wheels,
            objB: document.Wheels
        )
        ) {
            return document;
        }

        return (document with { Chords = chords, Wheels = wheels });
    }
    // One wheel's half of the skip — see SkipUnregisteredWheels.
    private static BindingWheelDefinition? SkipUnregisteredSectors(BindingWheelDefinition wheel, IReadOnlyList<BindingChordDefinition> chords, string? label) {
        var pageIds = new HashSet<string>(
            collection: chords.Where(predicate: static row => (row.Page is not null)).Select(selector: static row => row.Page!.Id),
            comparer: StringComparer.Ordinal
        );
        var survivingHoldPages = wheel.HoldPages.Where(predicate: pageIds.Contains).ToArray();

        if (survivingHoldPages.Length == 0) {
            if (label is not null) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: wheel \"{wheel.Id}\" skipped — none of its hold pages are in this composition.");
            }

            return null;
        }

        List<BindingPageDefinition>? keptRings = null;
        var changed = (survivingHoldPages.Length != wheel.HoldPages.Count);

        for (var ringIndex = 0; (ringIndex < wheel.Rings.Count); ringIndex++) {
            var ring = wheel.Rings[ringIndex];
            var entries = (ring.Entries ?? []);
            List<BindingPageEntryDefinition>? keptSectors = null;
            List<string>? dropped = null;

            for (var sectorIndex = 0; (sectorIndex < entries.Count); sectorIndex++) {
                var sector = entries[sectorIndex];

                if (
                    !string.IsNullOrEmpty(value: sector.Command) &&
                    !WorldAffordances.IsCommandRegistered(command: sector.Command)
                ) {
                    (dropped ??= []).Add(item: sector.Command);
                    keptSectors ??= [.. entries.Take(count: sectorIndex)];

                    continue;
                }

                keptSectors?.Add(item: sector);
            }

            if (dropped is null) {
                keptRings?.Add(item: ring);

                continue;
            }

            changed = true;
            keptRings ??= [.. wheel.Rings.Take(count: ringIndex)];

            if ((keptSectors?.Count ?? 0) >= BindingWheelDefinition.MinSectorsPerRing) {
                if (label is not null) {
                    Console.Error.WriteLine(value: $"[player.bindings] {label}: wheel \"{wheel.Id}\" ring \"{ring.Id}\" skipped {dropped.Count} unregistered command{((dropped.Count == 1)
                        ? ""
                        : "s")} ({string.Join(
                        separator: ", ",
                        values: dropped
                    )}) — not registered in this composition.");
                }

                keptRings.Add(item: (ring with { Entries = keptSectors! }));
            } else if (label is not null) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: wheel \"{wheel.Id}\" ring \"{ring.Id}\" skipped — fewer than {BindingWheelDefinition.MinSectorsPerRing} of its sectors are registered in this composition ({string.Join(
                    separator: ", ",
                    values: dropped
                )} dropped).");
            }
        }

        if (!changed) {
            return wheel;
        }

        if ((keptRings?.Count ?? 0) == 0) {
            if (label is not null) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: wheel \"{wheel.Id}\" skipped — none of its rings survive in this composition.");
            }

            return null;
        }

        return (wheel with {
            HoldPages = survivingHoldPages,
            Rings = (keptRings ?? wheel.Rings),
        });
    }
    // The wheels' half of the registration skip, run against the ALREADY-FILTERED chord rows: a sector naming an
    // unregistered command drops (one narration per ring, the page-skip convention), a ring the drops leave below
    // the compiled minimum drops whole (a one-sector radial band selects nothing honestly), a wheel left ringless —
    // or whose hold page was itself dropped — drops whole. What survives always recompiles, so a registration gap
    // (a headless boot never registering the editor verbs a wheel's sectors commit) degrades the wheel instead of
    // rejecting the whole seat document. Returns the list UNCHANGED (by reference) when nothing was dropped.
    private static IReadOnlyList<BindingWheelDefinition>? SkipUnregisteredWheels(IReadOnlyList<BindingWheelDefinition>? wheels, IReadOnlyList<BindingChordDefinition> chords, string? label) {
        if (wheels is null) {
            return null;
        }

        List<BindingWheelDefinition>? rewritten = null;

        for (var wheelIndex = 0; (wheelIndex < wheels.Count); wheelIndex++) {
            var wheel = wheels[wheelIndex];
            var filtered = SkipUnregisteredSectors(
                chords: chords,
                label: label,
                wheel: wheel
            );

            if (ReferenceEquals(
                objA: filtered,
                objB: wheel
            )) {
                rewritten?.Add(item: wheel);

                continue;
            }

            rewritten ??= [.. wheels.Take(count: wheelIndex)];

            if (filtered is not null) {
                rewritten.Add(item: filtered);
            }
        }

        return ((rewritten is null)
            ? wheels
            : ((rewritten.Count > 0)
                ? rewritten
                : null
        ));
    }
    // Route-local channel compatibility. Existence and shape belong to the destination world, so a stored profile or
    // session row can be valid where it was authored and unavailable where the seat travels next. Such a row is
    // filtered from the composed view, never deleted from its layer, and can therefore return when the seat travels
    // back. The live player.bind probe prevents a newly-authored incompatible row from entering in the first place.
    private static bool TryDescribeUnavailableChannel(WorldChannelTable channels, ChannelRef reference, float? scale, out string finding) {
        var description = DescribeChannel(reference: reference);

        if (!channels.TryGetOrdinal(
            ordinal: out var ordinal,
            reference: reference
        )) {
            finding = $"{description} is not declared in this composition";

            return true;
        }

        if (
            (scale is { } authoredScale) &&
            (authoredScale != 1f) &&
            (channels.Shape(ordinal: ordinal) == ChannelShape.Binary)
        ) {
            finding = $"{description} resolves to a binary channel but carries scale {authoredScale} instead of +1";

            return true;
        }

        finding = string.Empty;

        return false;
    }

    /// <summary>The channel vocabulary seat <paramref name="slot"/>'s bindings resolve against — that seat's own
    /// currently-routed document's live table (see <see cref="SyncSeat"/>), so a crossed seat is linted against the
    /// destination's channels, never always the boot world's. An out-of-range slot reads slot 0's table.</summary>
    /// <param name="slot">The 0-based local roster slot.</param>
    public WorldChannelTable Channels(int slot) => m_channels[((((uint)slot) < SeatCount)
        ? slot
        : 0)];
    /// <summary>The document the seat currently resolves through — the full composed stack (engine default ⊕ overlays ⊕
    /// profile ⊕ session), with the same unregistered-command/unavailable-channel skip <see cref="RecomposeSeat"/>
    /// applies (silent here — this read never narrates; the recompose that already ran, or the boot sweep, already
    /// did) so the <c>player.bindings</c> echo never claims a dead page or a channel this seat's routed world cannot
    /// carry.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The composed document, or the composed base for an out-of-range slot.</returns>
    public BindingProfileDocument ComposedDocument(int slot) {
        var channelSlot = ((((uint)slot) < SeatCount)
            ? slot
            : 0
        );

        return SkipUnregisteredPages(
            document: ((((uint)slot) < SeatCount)
            ? ComposeSeat(slot: slot)
            : ComposeBase()),
            channels: m_channels[channelSlot],
            label: null
        );
    }
    /// <summary>Describes a seat's full context derivation for the <c>player.bindings</c> read-back: each admitted
    /// family's current state with its matched row (winner or shadowed), the requested group, and the finally-resolved
    /// active group with its derivation step.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The derivation echo (slot 0's for an out-of-range slot).</returns>
    public WorldContextDerivationEcho DescribeContextDerivation(int slot) {
        slot = ((((uint)slot) < SeatCount)
            ? slot
            : 0
        );

        var (winnerFamily, winnerState, _) = FirstMatch(slot: slot);
        var reportedFamilies = new List<string>(capacity: (BuiltInContextFamilies.Length + m_seatContexts[slot].Count));

        reportedFamilies.AddRange(collection: BuiltInContextFamilies);

        foreach (var row in m_seatContexts[slot]) {
            if (
                WorldStateBindingContext.TryParseFamily(
                    family: row.Family,
                    rowName: out _
                ) &&
                !reportedFamilies.Contains(
                    value: row.Family,
                    comparer: StringComparer.Ordinal
                )
            ) {
                reportedFamilies.Add(item: row.Family);
            }
        }

        var families = new WorldContextFamilyEcho[reportedFamilies.Count];

        for (var familyIndex = 0; (familyIndex < reportedFamilies.Count); familyIndex++) {
            var family = reportedFamilies[familyIndex];
            _ = m_contextStates[slot].TryGetValue(
                key: family,
                value: out var state
            );
            state ??= string.Empty;
            string? matchedGroup = null;

            foreach (var row in m_seatContexts[slot]) {
                if (
                    string.Equals(
                    a: row.Family,
                    b: family,
                    comparisonType: StringComparison.Ordinal
                ) &&
                    string.Equals(
                    a: row.State,
                    b: state,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    matchedGroup = row.Group;

                    break;
                }
            }

            families[familyIndex] = new WorldContextFamilyEcho(
                Family: family,
                Group: matchedGroup,
                State: state,
                Wins: ((matchedGroup is not null) && string.Equals(
                    a: winnerFamily,
                    b: family,
                    comparisonType: StringComparison.Ordinal
                ))
            );
        }

        return new WorldContextDerivationEcho(
            ActiveGroup: m_seats[slot].ViewFor(slot: slot).Group,
            Families: families,
            Step: ((winnerFamily is not null)
            ? $"context {winnerFamily}={winnerState}"
            : "default")
        );
    }
    /// <inheritdoc/>
    public ReadOnlySpan<BindingChordEdge> DrainChordEdges(int slot) {
        return ((((uint)slot) < SeatCount)
            ? m_seats[slot].DrainChordEdges(slot: slot)
            : []
        );
    }
    /// <inheritdoc/>
    public IReadOnlyList<(int Slot, BindingChordEdge Edge)> DrainScheduledEdges() {
        // Scheduled edges (a Tapped row activator's deferred release) are not seat-scoped at the CALL site — the
        // router asks once per tick, not once per seat — so this aggregates every seat's own PagedInputBindings.
        // Most ticks every seat's list is empty; allocate only when at least one seat actually has something due.
        List<(int Slot, BindingChordEdge Edge)>? due = null;

        for (var slot = 0; (slot < SeatCount); slot++) {
            var seatDue = m_seats[slot].DrainScheduledEdges();

            if (seatDue.Count == 0) {
                continue;
            }

            (due ??= []).AddRange(collection: seatDue);
        }

        return (((IReadOnlyList<(int Slot, BindingChordEdge Edge)>?)due) ?? []);
    }
    /// <summary>The immutable view of the page the seat's held chord currently selects — the binding bar's read
    /// seam (a single volatile reference read; see <see cref="PagedInputBindings.ViewFor"/>).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The active page's precomputed view (slot 0's for an out-of-range slot).</returns>
    public BindingPageView PageView(int slot) =>
        m_seats[((((uint)slot) < SeatCount)
            ? slot
            : 0)].ViewFor(slot: slot);
    /// <inheritdoc/>
    public void Reset(int slot) {
        if (((uint)slot) < SeatCount) {
            m_seats[slot].Reset(slot: slot);
        }
    }
    /// <inheritdoc/>
    public void ResetAll() {
        for (var slot = 0; (slot < SeatCount); slot++) {
            m_seats[slot].Reset(slot: slot);
        }
    }
    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) {
        return ((((uint)slot) < SeatCount)
            ? m_seats[slot].Resolve(
                slot: slot,
                source: source
            )
            : null
        );
    }
    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, in InputSignal signal) {
        return ((((uint)slot) < SeatCount)
            ? m_seats[slot].Resolve(
                signal: in signal,
                slot: slot
            )
            : null
        );
    }
    /// <summary>The seat's current live session-rebind layer, or <see langword="null"/> when it has none.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public BindingProfileDocument? SessionRebind(int slot) => ((((uint)slot) < SeatCount)
        ? m_sessionRebinds[slot]
        : null
    );
    /// <summary>Gets the group the seat's composed <c>contexts</c> rows map a family state to — the first row
    /// (document order) for that family and state — or <see langword="null"/> when the document authors none: the
    /// state may still be published, but it moves the seat to no page.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="family">The admitted family name.</param>
    /// <param name="state">The family state.</param>
    public string? ContextGroup(int slot, string family, string state) {
        if (((uint)slot) >= SeatCount) {
            return null;
        }

        foreach (var row in m_seatContexts[slot]) {
            if (
                string.Equals(
                a: row.Family,
                b: family,
                comparisonType: StringComparison.Ordinal
            ) &&
                string.Equals(
                a: row.State,
                b: state,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return row.Group;
            }
        }

        return null;
    }
    /// <summary>Gets a seat's active group and that group's resting page id — where a live rebind that names no
    /// group lands.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public (string Group, string PageId) RestingPage(int slot) {
        slot = ((((uint)slot) < SeatCount)
            ? slot
            : 0
        );

        var group = m_seats[slot].ViewFor(slot: slot).Group;

        return (group, (m_seats[slot].RestingPageIdOf(group: group) ?? group));
    }
    /// <summary>Publishes one built-in context family's current state for a seat (see
    /// <see cref="WorldContextFamilies"/>) and re-derives the seat's active group when the state changed. The roster,
    /// engagement, and editor publishers use this path; state-backed families are published only from the routed
    /// definition by <see cref="SyncSeat"/>. An unknown or state-backed family is ignored.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="family">The admitted family name (e.g. <see cref="WorldContextFamilies.Engagement"/>).</param>
    /// <param name="state">The family's current state for the seat (e.g.
    /// <see cref="WorldContextFamilies.EngagementEngaged"/>).</param>
    public void SetContextState(int slot, string family, string state) {
        if (
            (((uint)slot) >= SeatCount) ||
            (WorldContextFamilies.StatesOf(family: family) is null) ||
            (m_contextStates[slot].TryGetValue(
                key: family,
                value: out var current
            ) && string.Equals(
                a: current,
                b: state,
                comparisonType: StringComparison.Ordinal
            ))
        ) {
            return;
        }

        m_contextStates[slot][family] = state;
        DeriveActiveGroup(slot: slot);
    }
    /// <summary>Delivers everything a selected profile carries to a seat — its binding layer and its control feel —
    /// and recomposes that seat. Called by the roster on a profile selection / join / live identity switch. One door
    /// for the whole profile rather than one per layer: a seat is handed a coherent set at a single moment, and a
    /// layer added later cannot be delivered at some call sites and forgotten at others.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="bindings">The profile's binding section, or <see langword="null"/> when it carries none.</param>
    public void SetProfileLayers(int slot, BindingProfileDocument? bindings) {
        if (((uint)slot) >= SeatCount) {
            return;
        }

        m_profileBindings[slot] = bindings;
        RecomposeSeat(slot: slot);
    }
    /// <summary>Sets a seat's live session-rebind layer and recomposes that seat — the <c>player.bind</c> path. The layer
    /// is unsaved until <c>identity.bindings.save</c> folds it into the seat's identity; passing null clears it.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="rebinds">The session rebind document, or <see langword="null"/> to clear it.</param>
    public void SetSessionRebind(int slot, BindingProfileDocument? rebinds) {
        if (((uint)slot) >= SeatCount) {
            return;
        }

        m_sessionRebinds[slot] = rebinds;
        RecomposeSeat(slot: slot);
    }
    /// <summary>Reflects seat <paramref name="slot"/>'s currently routed definition: binding pages, wheels,
    /// state-backed contexts, and channels all follow its complete <see cref="Client.WorldSeatAuthorityRouter"/>
    /// route, never a single world shared by every seat. Unchanged state, overlays, channels, and controlled entity
    /// short-circuit by reference/value comparison on the ordinary tick. All parts are tested because a channel a
    /// seat binds and the
    /// channel table that declares it are one document's two faces — refreshing one without the other is how a seat
    /// comes to be linted against a vocabulary the world no longer has.</summary>
    /// <param name="slot">The 0-based local roster slot.</param>
    /// <param name="definition">The document seat <paramref name="slot"/> is currently routed to present from.</param>
    /// <param name="entityIndex">The controlled body's entity index, used to address a keyed state context.</param>
    /// <param name="nextInputTick">The routed authority's next input tick; the preceding delivered tick is used to
    /// read state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void SyncSeat(int slot, WorldDefinition definition, int entityIndex, ulong nextInputTick) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (((uint)slot) >= SeatCount) {
            return;
        }

        var overlays = definition.BindingOverlays;
        var channels = definition.Channels;
        var state = definition.State;
        var stateChanged = (
            !ReferenceEquals(
                objA: state,
                objB: m_stateSource[slot]
            ) ||
            (entityIndex != m_stateEntityIndices[slot])
        );
        var bindingsChanged = (
            !ReferenceEquals(
                objA: overlays,
                objB: m_overlays[slot]
            ) ||
            !ReferenceEquals(
                objA: channels,
                objB: m_channelSource[slot]
            )
        );

        m_definitions[slot] = definition;
        m_stateTicks[slot] = CompletedTick(endpointNextInputTick: nextInputTick);

        if (stateChanged) {
            m_stateSource[slot] = state;
            m_stateEntityIndices[slot] = entityIndex;
            PublishStateContexts(
                slot: slot,
                tick: m_stateTicks[slot]
            );
            if (!bindingsChanged) {
                DeriveActiveGroup(slot: slot);
            }
        }

        if (!bindingsChanged) {
            return;
        }

        m_overlays[slot] = (overlays ?? []);
        m_channelSource[slot] = channels;
        m_channels[slot] = WorldChannelTable.Compile(channels: channels);
        RecomposeSeat(slot: slot);
    }
    /// <summary>Checks a prospective live session layer against the seat's actual current composition and routed
    /// channel table without installing it. Stale route-local rows in older layers receive the same surgical filtering
    /// <see cref="RecomposeSeat"/> applies, while a structural or surviving vocabulary error refuses the candidate.
    /// This is the truthful preflight for <c>player.bind</c>: compiling against only the boot default could reject a
    /// group supplied by the destination overlay, or accept a candidate the destination composition cannot reload.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="rebinds">The prospective complete session-rebind layer.</param>
    /// <param name="reason">The first refusal reason, or an empty string on success.</param>
    /// <returns>Whether installing <paramref name="rebinds"/> can recompose the seat at its current route.</returns>
    public bool TryValidateSessionRebind(int slot, BindingProfileDocument rebinds, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: rebinds);

        if (((uint)slot) >= SeatCount) {
            reason = $"seat slot {slot} is outside 0..{(SeatCount - 1)}";

            return false;
        }

        try {
            var channels = m_channels[slot];
            var document = WorldBindingComposer.Compose(BaseLayers(
                overlays: m_overlays[slot],
                profile: m_profileBindings[slot],
                session: rebinds
            ));

            document = SkipUnregisteredPages(
                channels: channels,
                document: document,
                label: null
            );

            var errors = new List<string>();

            WorldAffordances.Validate(
                channels: channels,
                document: document,
                errors: errors
            );
            WorldStateBindingContext.Validate(
                document: document,
                stateRows: StateRowsByName(rows: m_definitions[slot].State),
                errors: errors
            );

            if (errors.Count > 0) {
                reason = errors[0];

                return false;
            }

            _ = BindingProfile.Compile(
                document: document,
                channelCommandName: channel => RoutedChannelCommandName(
                    channel: channel,
                    channels: channels
                )
            );
        } catch (ArgumentException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Runs the affordance-vocabulary check over every seat's composed document and prints one loud line per
    /// finding — the composition root's post-build sweep covering the layers that composed before the vocabulary
    /// existed (the world's boot overlays compile at construction, pre-container). Findings do
    /// not un-bind anything: the boot mapping already resolved, and a dead entry resolves to nothing at dispatch — the
    /// sweep exists so that silence is loud instead. Runs the same unregistered-command/unavailable-channel skip
    /// <see cref="RecomposeSeat"/> applies first (one narration line per page/row, e.g. the whole <c>editor</c>/
    /// <c>sculpt</c> groups on a boot shape that never registered them), then reports whatever vocabulary findings
    /// remain in the filtered document per entry — a genuine bindability or value-kind mistake still
    /// gets its full detail. Every seat is swept individually (never deduplicated by "no profile/session layer")
    /// because <see cref="SyncSeat"/> can leave two such seats composed against different worlds the instant they
    /// route differently — this runs once, at boot, before any seat has crossed, so the cost of always sweeping four
    /// seats is negligible either way.</summary>
    public void ValidateAffordancesLoudly() {
        var errors = new List<string>();

        for (var slot = 0; (slot < SeatCount); slot++) {
            var label = $"seat {(slot + 1)}";
            var document = SkipUnregisteredPages(
                document: ComposeSeat(slot: slot),
                channels: m_channels[slot],
                label: label
            );

            errors.Clear();
            WorldAffordances.Validate(
                document: document,
                channels: m_channels[slot],
                errors: errors
            );
            foreach (var error in errors) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: {error}");
            }
        }
    }
    /// <summary>The wheel the seat's active page presents, or <see langword="null"/> when no wheel is held open —
    /// the radial presenter's one open/closed read (see <see cref="PagedInputBindings.WheelFor"/>).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The active wheel view, or <see langword="null"/> (always <see langword="null"/> for an out-of-range
    /// slot).</returns>
    public BindingWheelView? WheelView(int slot) =>
        ((((uint)slot) < SeatCount)
            ? m_seats[slot].WheelFor(slot: slot)
            : null
        );

    /// <summary>Initializes a new instance over the boot world definition. Every seat starts compiled from the
    /// world's composed overlays (a world authoring none has no bindings); profile and session layers are null.</summary>
    /// <param name="definition">The boot world definition, supplying both the binding overlays (layer 0..) and the
    /// channel table those overlays name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public WorldSeatBindings(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        m_profileBindings = new BindingProfileDocument?[SeatCount];
        m_sessionRebinds = new BindingProfileDocument?[SeatCount];
        m_contextStates = new Dictionary<string, string>[SeatCount];
        m_seatContexts = new IReadOnlyList<BindingContextDefinition>[SeatCount];
        m_overlays = new IReadOnlyList<WorldBindingOverlay>[SeatCount];
        m_channelSource = new IReadOnlyList<WorldChannel>[SeatCount];
        m_channels = new WorldChannelTable[SeatCount];
        m_effectiveDocuments = new byte[SeatCount][];
        m_effectiveChannelNames = new string[SeatCount][];
        m_definitions = new WorldDefinition[SeatCount];
        m_stateEntityIndices = new int[SeatCount];
        m_stateSource = new IReadOnlyList<WorldStateRow>[SeatCount];
        m_stateTicks = new ulong[SeatCount];

        // Every seat's authority claim begins at boot, so
        // every seat seeds from the SAME boot overlays/channels here — SyncSeat is what lets them diverge later.
        var bootOverlays = (definition.BindingOverlays ?? []);
        var bootChannels = definition.Channels;
        var bootTable = WorldChannelTable.Compile(channels: bootChannels);

        for (var slot = 0; (slot < SeatCount); slot++) {
            m_overlays[slot] = bootOverlays;
            m_channelSource[slot] = bootChannels;
            m_channels[slot] = bootTable;
            m_definitions[slot] = definition;
            m_stateEntityIndices[slot] = slot;
            m_stateSource[slot] = definition.State;
        }

        var seedDocument = ComposeSeat(slot: 0);
        var seedDocumentBytes = SerializeEffectiveDocument(document: seedDocument);
        var seedChannelNames = ChannelNames(channels: bootChannels);
        var seedBase = BindingProfile.Compile(
            document: seedDocument,
            channelCommandName: channel => RoutedChannelCommandName(
                channel: channel,
                channels: bootTable
            )
        );

        m_seats = new PagedInputBindings[SeatCount];

        for (var slot = 0; (slot < SeatCount); slot++) {
            m_seats[slot] = new PagedInputBindings(profile: seedBase);
            m_contextStates[slot] = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                [WorldContextFamilies.Roster] = WorldContextFamilies.RosterUnjoined,
                [WorldContextFamilies.Engagement] = WorldContextFamilies.EngagementNone,
                [WorldContextFamilies.Editor] = WorldContextFamilies.EditorNone,
            };
            m_seatContexts[slot] = (seedDocument.Contexts ?? []);
            m_effectiveDocuments[slot] = seedDocumentBytes;
            m_effectiveChannelNames[slot] = seedChannelNames;
            PublishStateContexts(
                slot: slot,
                tick: 0UL
            );
            DeriveActiveGroup(
                slot: slot,
                releasePriorGroup: false
            );
        }
    }
}
