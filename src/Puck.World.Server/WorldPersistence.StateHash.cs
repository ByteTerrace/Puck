using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    /// <summary>Gets the first stale arena digest the always-on sample (<see cref="Puck.State.StateArena.CheckDigests"/>) found
    /// beside an authority hash, or <see langword="null"/> while every sample agreed.</summary>
    public string? StaleDigest { get; private set; }
    /// <summary>Gets the <see cref="System.Diagnostics.Stopwatch"/> ticks the always-on digest sample has taken: a
    /// diagnostic read beside the hash, never folded into state.</summary>
    public long DigestCheckTicks { get; private set; }

    // The arena's incremental digest, sampled against its stored bytes every time it is taken (one row and one
    // segment in rotation). A stale sample is reported once by name and healed: the arena dropped its cache, so the
    // digest this hash folds is taken afresh.
    private ulong CheckedArenaHash() {
        var digest = Host.Arena.ComputeHash();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        if (Host.Arena.CheckDigests() is { } fault) {
            if (StaleDigest is null) {
                StaleDigest = $"tick {(Host.NextInputTick - 1UL)}: {fault}";
                Host.Output.Narrate(channel: "world.state.hash", text: $"[world.state.hash: stale digest at {StaleDigest}]");
            }
            digest = Host.Arena.ComputeHash();
        }
        DigestCheckTicks += (System.Diagnostics.Stopwatch.GetTimestamp() - started);
        return digest;
    }

    // The one switch over the world hash's component vocabulary. WorldStateHashComposition owns which components a
    // scope folds and in what order; each arm here folds one of them through its own owner, and a component with no
    // arm throws rather than folding nothing. Recompute folds the arena's rows afresh instead of from their
    // version-keyed digests; every other arm folds whole either way.
    internal void AppendStateHashComponent(ref Fnv1aHash hash, WorldStateHashComponent component, ulong seed, ulong tick, bool recompute) {
        switch (component) {
            case WorldStateHashComponent.Input:
                Host.Input.AppendStateHash(hash: ref hash);
                Host.Tick.AppendRoutedInputHash(hash: ref hash);
                Host.Tick.AppendFederatedInputHash(hash: ref hash);
                break;
            case WorldStateHashComponent.Custody:
                Host.TransferEscrow.Custody.AppendStateHash(hash: ref hash);
                break;
            case WorldStateHashComponent.ScopedReplays:
                hash.Add(value: ((uint)Host.ScopedReplays.Count));
                foreach (var scope in Host.ScopedReplaySpan) { scope.AppendStateHash(hash: ref hash); }
                break;
            case WorldStateHashComponent.Arena:
                hash.Add(value: (recompute
                    ? Host.Arena.RecomputeHash()
                    : CheckedArenaHash()));

                break;
            case WorldStateHashComponent.BoardEnforcement:
                BoardEnforcement.AppendStateHash(hash: ref hash);

                break;
            case WorldStateHashComponent.BodyActionState:
                hash.Add(value: ((uint)Host.Population.Capacity));

                for (var index = 0; (index < Host.Population.Capacity); index++) {
                    var body = Host.Population.EntryBody(index: index);

                    hash.Add(value: ((byte)((body is null)
                        ? 0
                        : 1)));

                    if (body is not null) {
                        hash.Add(value: ((uint)index));
                        body.AppendActionStateHash(hash: ref hash);
                        WorldStateHashComposition.AppendIdentityRecords(hash: ref hash, identity: body.Profile);
                    }
                }

                break;
            case WorldStateHashComponent.BodyContinuation:
                Host.Population.AppendContinuationHash(hash: ref hash);
                Host.Population.AppendSleepStateHash(hash: ref hash);
                Host.Events.AppendDiscoveryHash(hash: ref hash);

                break;
            case WorldStateHashComponent.Declaration:
                WorldStateHashComposition.AppendDeclaration(
                    hash: ref hash,
                    state: Host.Document.Definition.StateRaw
                );
                WorldStateHashComposition.AppendPoolBindings(hash: ref hash, definition: Host.Document.Definition);

                break;
            case WorldStateHashComponent.Decisions:
                Decisions.AppendStateHash(hash: ref hash);

                break;
            case WorldStateHashComponent.Flock:
                Host.Population.AppendFlockStateHash(hash: ref hash);

                break;
            case WorldStateHashComponent.HostOwnedRows:
                hash.Add(value: Host.Population.FieldRegions.Count);
                for (var regionIndex = 0; (regionIndex < Host.Population.FieldRegions.Count); regionIndex++) {
                    var region = Host.Population.FieldRegions[regionIndex];

                    hash.Add(value: region.Name.Length);
                    hash.Add(value: region.Name);
                    hash.Add(value: region.Lattice.FieldCount);
                    for (var fieldIndex = 0; (fieldIndex < region.Lattice.Input.Fields.Count); fieldIndex++) {
                        var field = region.Lattice.Input.Fields[fieldIndex];

                        hash.Add(value: field.Name.Length); hash.Add(value: field.Name);
                    }
                    region.Lattice.AppendStateHash(hash: ref hash);
                }

                break;
            case WorldStateHashComponent.InteractionLatch:
                Host.RuleHost.InteractionGateHeld.AppendStateHash(
                    compiled: Host.RuleHost.Interactions,
                    hash: ref hash
                );

                break;
            case WorldStateHashComponent.Navigation:
                Host.Population.AppendNavigationStateHash(hash: ref hash);

                break;
            case WorldStateHashComponent.PopulationPose:
                hash.Add(value: WorldReplaySnapshot.HashState(population: Host.Population));

                break;
            case WorldStateHashComponent.RuleGroups:
                Host.RuleHost.GroupState.AppendStateHash(
                    compiled: Host.RuleHost.Groups,
                    hash: ref hash
                );

                break;
            case WorldStateHashComponent.RuleLatch:
                // Rule bindings are iteration keys: they fold by name, so a restored arena's intern order is no
                // part of the hash.
                Host.RuleHost.RuleGateHeld.AppendStateHash(
                    compiled: Host.RuleHost.Rules,
                    hash: ref hash,
                    keyNames: Host.Arena.Keys.Names
                );

                break;
            case WorldStateHashComponent.Search:
                Host.Search.AppendStateHash(hash: ref hash);

                break;
            case WorldStateHashComponent.Seed:
                hash.Add(value: seed);

                break;
            case WorldStateHashComponent.Tick:
                hash.Add(value: tick);

                break;
            case WorldStateHashComponent.Topologies:
                WorldStateHashComposition.AppendTopologies(
                    hash: ref hash,
                    state: Host.Document.Definition.StateRaw
                );

                break;
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(component));
        }
    }
}
