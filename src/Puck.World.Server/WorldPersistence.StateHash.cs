using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    // The one switch over the world hash's component vocabulary. WorldStateHashComposition owns which components a
    // scope folds and in what order; each arm here folds one of them through its own owner, and a component with no
    // arm throws rather than folding nothing.
    internal void AppendStateHashComponent(ref Fnv1aHash hash, WorldStateHashComponent component, ulong seed, ulong tick) {
        switch (component) {
            case WorldStateHashComponent.Arena:
                hash.Add(value: Host.Arena.ComputeHash());

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
                hash.Add(value: ((byte)((Host.Population.Fields is null)
                    ? 0
                    : 1)));
                Host.Population.Fields?.AppendStateHash(hash: ref hash);

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
                Host.RuleHost.RuleGateHeld.AppendStateHash(
                    compiled: Host.RuleHost.Rules,
                    hash: ref hash
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
