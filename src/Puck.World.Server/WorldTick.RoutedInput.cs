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
        if (Host.Body(contribution.SourceBody) is not { } source || Host.Body(contribution.TargetBody) is not { } target) { return; }
        var revision = Host.Input.TryGet(contribution.Principal, out var owner) ? owner!.Revision : 0;
        m_routedInputs.Enqueue(new(contribution.SourceBody, Host.Population.Generation(contribution.SourceBody),
            contribution.TargetBody, Host.Population.Generation(contribution.TargetBody), Host.Population.Revision,
            contribution.Principal, revision, source.InputEpoch, target.InputEpoch, source.InputMappingId, contribution.Intent));
    }

    private void ApplyRoutedInputs() {
        while (m_routedInputs.TryDequeue(out var route)) {
            if (Host.Body(route.SourceBody) is not { } source || Host.Body(route.TargetBody) is not { } target ||
                Host.Population.Generation(route.SourceBody) != route.SourceGeneration ||
                Host.Population.Generation(route.TargetBody) != route.TargetGeneration ||
                Host.Population.Revision != route.PopulationRevision || source.InputEpoch != route.SourceEpoch ||
                source.InputMappingId != route.Mapping || target.InputEpoch != route.TargetEpoch ||
                (Host.Input.TryGet(route.Principal, out var owner) ? owner!.Revision : 0) != route.Revision ||
                TryDriveGateVerdict(route.TargetBody, out _) ||
                !Host.GrantTable.Allows(route.Principal, WorldCapability.Drive, GrantSubject.Body(route.TargetBody)).IsAllowed) { continue; }
            ControlApplication? application = null;
            foreach (var candidate in Host.GrantTable.Applications(route.Principal)) {
                if (candidate.Target == GrantSubject.Body(route.TargetBody)) { application = candidate; break; }
            }
            if (application is not { } selected) { continue; }
            var hasReach = Host.GrantTable.TryGetInputReach(route.Principal, GrantSubject.Body(route.TargetBody), out var reach);
            var image = route.Image with { Channels = default };
            for (var ordinal = 0; ordinal < ChannelLimits.MaxChannels; ordinal++) {
                if (!source.Channels.IsDeclared(ordinal)) { continue; }
                var name = source.Channels.Name(ordinal)!;
                if (selected.Reach is { } routeReach && !routeReach.Contains(name) || hasReach && !reach.Contains(name) ||
                    !target.Channels.TryGetOrdinal(name, out var destination) ||
                    source.Channels.Shape(ordinal) != target.Channels.Shape(destination)) { continue; }
                image = source.Channels.Shape(ordinal) == ChannelShape.Relative
                    ? image.WithRelative(destination, route.Image.Channels.Relative(ordinal))
                    : image.WithChannel(destination, target.Channels.Quantize(destination, route.Image[ordinal]));
            }
            // Engagement output includes the source body's already composed channels. Keep it in the contribution
            // fold; replaying it through TryAdmit would forge duplicate edges and bind the owner to another body.
            var applied = new WorldAcceptedInput(m_deliveryTick, route.TargetBody, image, route.Principal);
            ApplyAcceptedBodyInput(target, in applied);
        }
    }

    internal void AppendRoutedInputHash(ref Fnv1aHash hash) {
        hash.Add((uint)m_routedInputs.Count);
        foreach (var route in m_routedInputs) {
            hash.Add(route.SourceBody); hash.Add(route.SourceGeneration); hash.Add(route.TargetBody); hash.Add(route.TargetGeneration);
            hash.Add(route.PopulationRevision); hash.Add((byte)route.Principal.Kind); hash.Add(route.Principal.Index);
            hash.Add(route.Principal.Generation); hash.Add(route.Principal.Name); hash.Add(route.Revision);
            hash.Add(route.SourceEpoch); hash.Add(route.TargetEpoch); hash.Add(route.Mapping);
            for (var ordinal = 0; ordinal < ChannelLimits.MaxAxes; ordinal++) { hash.Add(route.Image.Channels.Axes[ordinal]); }
            hash.Add(route.Image.Channels.Buttons); hash.Add((byte)(route.Image.SourceRay.HasValue ? 1 : 0));
            if (route.Image.SourceRay is { } ray) {
                hash.Add(ray.Origin.X.Value); hash.Add(ray.Origin.Y.Value); hash.Add(ray.Origin.Z.Value);
                hash.Add(ray.Direction.X.Value); hash.Add(ray.Direction.Y.Value); hash.Add(ray.Direction.Z.Value);
            }
        }
    }
}
