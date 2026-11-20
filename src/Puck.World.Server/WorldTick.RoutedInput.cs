using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A pending body-to-body fold derived from already admitted input. Its image uses the source mapping;
/// delivery resolves logical names against the target's live mapping. It is not an ingress event stream.</summary>
public readonly record struct WorldRoutedInput(int SourceBody, int SourceGeneration, int TargetBody, int TargetGeneration,
    int PopulationRevision, Principal Principal, ulong Revision, ulong SourceEpoch, ulong TargetEpoch, ushort Mapping, PlayerIntent Image);
public sealed partial class WorldTick {
    private void QueueRoutedInput(BodyRouteContribution contribution) {
        if ((Host.Body(index: contribution.SourceBody) is not { } source) || (Host.Body(index: contribution.TargetBody) is not { } target)) { return; }
        var revision = (Host.Input.TryGet(contribution.Principal, out var owner) ? owner!.Revision : 0);

        m_routedInputs.Enqueue(item: new(contribution.SourceBody, Host.Population.Generation(index: contribution.SourceBody),
            contribution.TargetBody, Host.Population.Generation(index: contribution.TargetBody), Host.Population.Revision,
            contribution.Principal, revision, source.InputEpoch, target.InputEpoch, source.InputMappingId, contribution.Intent));
    }
    private void ApplyRoutedInputs() {
        while (m_routedInputs.TryDequeue(result: out var route)) {
            if ((Host.Body(index: route.SourceBody) is not { } source) || (Host.Body(index: route.TargetBody) is not { } target) ||
                (Host.Population.Generation(index: route.SourceBody) != route.SourceGeneration) ||
                (Host.Population.Generation(index: route.TargetBody) != route.TargetGeneration) ||
                (Host.Population.Revision != route.PopulationRevision) || (source.InputEpoch != route.SourceEpoch) ||
                (source.InputMappingId != route.Mapping) || (target.InputEpoch != route.TargetEpoch) ||
                ((Host.Input.TryGet(route.Principal, out var owner) ? owner!.Revision : 0) != route.Revision) ||
                TryDriveGateVerdict(bodyIndex: route.TargetBody, verdict: out _) ||
                !Host.GrantTable.Allows(route.Principal, WorldCapability.Drive, GrantSubject.Body(index: route.TargetBody)).IsAllowed) { continue; }
            ControlApplication? application = null;

            foreach (var candidate in Host.GrantTable.Applications(principal: route.Principal)) {
                if (candidate.Target == GrantSubject.Body(index: route.TargetBody)) { application = candidate; break; }
            }
            if (application is not { } selected) { continue; }
            var hasReach = Host.GrantTable.TryGetChannelReach(route.Principal, GrantSubject.Body(index: route.TargetBody), source.Channels, out var reach);
            var routeReach = (selected.Reach?.Project(table: source.Channels) ?? ChannelReachMask.All);
            var image = route.Image with { Channels = default };
            var remap = source.Channels.RemapTo(target: target.Channels);

            for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
                var destination = remap[ordinal];

                if ((destination < 0) || !routeReach.Contains(ordinal: ordinal) || (hasReach && !reach.Contains(ordinal: ordinal)) ||
                    (source.Channels.Shape(ordinal: ordinal) != target.Channels.Shape(ordinal: destination))) { continue; }
                image = ((source.Channels.Shape(ordinal: ordinal) == ChannelShape.Relative)
                    ? image.WithRelative(ordinal: destination, value: route.Image.Channels.Relative(ordinal: ordinal))
                    : image.WithChannel(ordinal: destination, value: target.Channels.Quantize(ordinal: destination, value: route.Image[ordinal])));
            }
            // Engagement output includes the source body's already composed channels. Keep it in the contribution
            // fold; replaying it through TryAdmit would forge duplicate edges and bind the owner to another body.
            var applied = new WorldAcceptedInput(m_deliveryTick, route.TargetBody, image, route.Principal);

            ApplyAcceptedBodyInput(applied: in applied, body: target);
        }
    }

    internal void AppendRoutedInputHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_routedInputs.Count));
        foreach (var route in m_routedInputs) {
            hash.Add(value: route.SourceBody); hash.Add(value: route.SourceGeneration); hash.Add(value: route.TargetBody); hash.Add(value: route.TargetGeneration);
            hash.Add(value: route.PopulationRevision); hash.Add(value: ((byte)route.Principal.Kind)); hash.Add(value: route.Principal.Index);
            hash.Add(value: route.Principal.Generation); hash.Add(value: route.Principal.Name); hash.Add(value: route.Revision);
            hash.Add(value: route.SourceEpoch); hash.Add(value: route.TargetEpoch); hash.Add(value: route.Mapping);
            route.Image.AppendStateHash(hash: ref hash);
        }
    }
}
