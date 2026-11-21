using System.Globalization;
using System.Runtime.InteropServices;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // One gated instance in the ordinal access cache: a session feed, an infinity layer, or both (nested camera owner),
    // and the route its view was last given.
    private readonly record struct AccessBinding(int Index, SessionFeed? Feed, InfinityEntry? Infinity, RouteCache Route);

    // A view's route as last built and the spans it was built from. The route is a function of those spans alone, so a
    // frame whose spans did not move reuses it and builds no label.
    private sealed class RouteCache {
        private List<DeliverySpan> m_spans = [];
        private List<DeliverySpan> m_next = [];
        private DeliveryLabel m_route;

        // The list the frame's walk fills, empty.
        public List<DeliverySpan> Next {
            get {
                m_next.Clear();
                return m_next;
            }
        }

        // The route of the spans the walk just filled into Next.
        public DeliveryLabel Settle() {
            // A span is compared by reference: it has no value equality.
            if (!m_route.IsClassified || !((ReadOnlySpan<DeliverySpan>)CollectionsMarshal.AsSpan(list: m_next)).SequenceEqual(other: CollectionsMarshal.AsSpan(list: m_spans), comparer: null)) {
                (m_spans, m_next) = (m_next, m_spans);
                m_route = DeliveryLabel.Of(spans: CollectionsMarshal.AsSpan(list: m_spans));
            }
            return m_route;
        }
    }

    private readonly RouteCache m_queryRoute = new();

    private RenderGraphInstanceSet? m_accessCacheSet;
    private int m_accessCacheVersion = -1;
    private int m_accessBindingsVersion;
    private WorldViewAccess[] m_accessBase = [];
    private AccessBinding[] m_accessGated = [];

    /// <inheritdoc/>
    /// <remarks>Reads the access each session along the view's chain settled this frame
    /// (<see cref="WorldSessionRoute.Access"/>, settled in <see cref="Publish"/>): a session view, a nested camera or
    /// child of one and an infinity layer are available only while every session they are seen through may observe,
    /// at the latest epoch among them, under a route of every one's span.</remarks>
    public WorldViewAccess AccessOf(string view) {
        if (SessionFeedOf(view) is { } feed) { return Access(feed: feed, infinity: null, route: m_queryRoute); }
        if (m_infinityOutputs.TryGetValue(view, out var infinity)) { return Access(feed: null, infinity: infinity.Entry, route: m_queryRoute); }
        if (NestedCameraLevel(view) is { } level && m_nestedOwners.TryGetValue(level, out var owner)) {
            return Access(feed: owner.Feed, infinity: owner.Infinity, route: m_queryRoute);
        }
        // Historical package entries can outlive the view roster. A retired generated observation is not a new
        // unnamed host view, and the session it showed is gone. Recognize the minted path grammar, not arbitrary
        // prefixes (for example its environment buffer is a different instance and is not an observation image).
        return (ObservationName(view) ? WorldViewAccess.Retired : WorldViewAccess.Open);
    }
    /// <inheritdoc/>
    public void ReadAccess(RenderGraphInstanceSet set, Span<WorldViewAccess> access) {
        EnsureFeeds();

        var count = set.Instances.Count;

        if (!ReferenceEquals(objA: set, objB: m_accessCacheSet) || (m_accessCacheVersion != m_accessBindingsVersion)) {
            RebuildAccessBindings(set: set);
        }

        m_accessBase.AsSpan(start: 0, length: count).CopyTo(destination: access);

        var gated = m_accessGated;

        for (var position = 0; (position < gated.Length); position++) {
            var slot = gated[position];

            access[slot.Index] = Access(feed: slot.Feed, infinity: slot.Infinity, route: slot.Route);
        }
    }
    private void RebuildAccessBindings(RenderGraphInstanceSet set) {
        var instances = set.Instances;
        var count = instances.Count;

        if (m_accessBase.Length != count) {
            m_accessBase = new WorldViewAccess[count];
        }

        List<AccessBinding>? gated = null;

        for (var index = 0; (index < count); index++) {
            var view = instances[index].Name;

            if (SessionFeedOf(name: view) is { } feed) {
                m_accessBase[index] = WorldViewAccess.Open;
                (gated ??= []).Add(item: new AccessBinding(Feed: feed, Index: index, Infinity: null, Route: new RouteCache()));
            } else if (m_infinityOutputs.TryGetValue(key: view, value: out var infinity)) {
                m_accessBase[index] = WorldViewAccess.Open;
                (gated ??= []).Add(item: new AccessBinding(Feed: null, Index: index, Infinity: infinity.Entry, Route: new RouteCache()));
            } else if ((NestedCameraLevel(name: view) is { } level) && m_nestedOwners.TryGetValue(key: level, value: out var owner)) {
                m_accessBase[index] = WorldViewAccess.Open;
                if ((owner.Feed is not null) || (owner.Infinity is not null)) {
                    (gated ??= []).Add(item: new AccessBinding(Feed: owner.Feed, Index: index, Infinity: owner.Infinity, Route: new RouteCache()));
                }
            } else {
                m_accessBase[index] = (ObservationName(name: view) ? WorldViewAccess.Retired : WorldViewAccess.Open);
            }
        }

        m_accessGated = ((gated is null) ? [] : [.. gated]);
        m_accessCacheSet = set;
        m_accessCacheVersion = m_accessBindingsVersion;
    }
    private void SetInfinityOutput(string name, InfinityOutput output) {
        if (!m_infinityOutputs.TryGetValue(key: name, value: out var existing) || !ReferenceEquals(objA: existing.Entry, objB: output.Entry)) {
            m_accessBindingsVersion++;
        }
        m_infinityOutputs[name] = output;
    }
    private void ClearInfinityOutputs() {
        if (m_infinityOutputs.Count != 0) {
            m_infinityOutputs.Clear();
            m_accessBindingsVersion++;
        }
    }
    private bool Available(string view) => AccessOf(view: view).Available;
    private static bool ObservationName(string name) {
        ReadOnlySpan<char> path;
        if (GeneratedName.TryStripHead(name, WorldViewNames.SessionHead, out var session)) {
            path = session;
            if (!TakeOrdinal(ref path)) { return false; }
        } else if (GeneratedName.TryStripHead(name, WorldViewNames.RoutedHead, out var routed)) {
            path = routed;
            var split = path.IndexOf(GeneratedName.Joiner);
            if (split != 16 || !ulong.TryParse(path[..split], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)) { return false; }
            path = path[(split + 1)..];
        } else if (GeneratedName.TryStripHead(name, WorldViewNames.SkyPart, out var sky)) {
            path = sky;
            var split = path.IndexOf(GeneratedName.Joiner);
            if (split < 0) { return !path.IsEmpty; }
            if (split == 0) { return false; }
            path = path[(split + 1)..];
        } else { return false; }
        while (!path.IsEmpty) {
            var split = path.IndexOf(GeneratedName.Joiner);
            if (split > 0 && path[..split].SequenceEqual(WorldViewNames.CameraPart)) {
                var leaf = path[(split + 1)..];
                return !leaf.IsEmpty && !leaf.Contains(GeneratedName.Joiner);
            }
            if (split > 0 && path[..split].SequenceEqual(WorldViewNames.SkyPart)) {
                path = path[(split + 1)..];
                split = path.IndexOf(GeneratedName.Joiner);
                if (split < 0) { return !path.IsEmpty; }
                if (split == 0) { return false; }
                path = path[(split + 1)..];
                continue;
            }
            if (!TakeOrdinal(ref path)) { return false; }
        }
        return true;
    }
    private static bool TakeOrdinal(ref ReadOnlySpan<char> path) {
        var split = path.IndexOf(GeneratedName.Joiner);
        var ordinal = ((split < 0) ? path : path[..split]);
        if (ordinal.IsEmpty || (ordinal.Length > 1 && ordinal[0] == '0') || !int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0) { return false; }
        path = ((split < 0) ? [] : path[(split + 1)..]);
        return split < 0 || !path.IsEmpty;
    }
    // A view's access through a session feed's chain and an infinity layer: available only while every session and layer
    // it is seen through may observe and every span on its route is still open, at the latest epoch among them, under the
    // route of every one's viewer span and the delivery span of every mirror it reads (an ended span while any may not
    // observe). A view whose route already ended is unavailable, so its node pauses rather than rendering pictures no
    // reader may be given. A view gated by neither is open.
    private static WorldViewAccess Access(SessionFeed? feed, InfinityEntry? infinity, RouteCache route) {
        var spans = route.Next;
        var available = true;
        var epoch = 0L;
        var latch = DeliveryLatch.Capture();

        WalkRoute(available: ref available, epoch: ref epoch, feed: feed, infinity: infinity, latch: in latch, spans: spans);
        if (spans.Count == 0) {
            return WorldViewAccess.Open;
        }

        return new WorldViewAccess(
            Available: available,
            Epoch: epoch,
            Route: (available ? route.Settle() : DeliveryLabel.Of(span: DeliverySpan.Ended))
        );
    }
    private static bool Available(SessionFeed feed) {
        var available = true;
        var epoch = 0L;
        var latch = DeliveryLatch.Capture();

        WalkRoute(available: ref available, epoch: ref epoch, feed: feed, infinity: null, latch: in latch, spans: null);
        return available;
    }
    // Visits every session and layer a view is seen through, from the leaf up.
    private static void WalkRoute(SessionFeed? feed, InfinityEntry? infinity, in DeliveryLatch latch, List<DeliverySpan>? spans, ref bool available, ref long epoch) {
        for (var current = feed; current is not null; current = current.ParentFeed) {
            Visit(access: current.Route.Access, available: ref available, epoch: ref epoch, latch: in latch, mirror: current.Mirror, spans: spans);
            if (current.ParentInfinity is { } parent) {
                Visit(access: parent.Access, available: ref available, epoch: ref epoch, latch: in latch, mirror: parent.Hosted?.Mirror, spans: spans);
            }
        }
        if (infinity is not null) {
            Visit(access: infinity.Access, available: ref available, epoch: ref epoch, latch: in latch, mirror: infinity.Hosted?.Mirror, spans: spans);
        }
    }
    private static void Visit(WorldViewerAccess access, WorldSessionMirror? mirror, in DeliveryLatch latch, List<DeliverySpan>? spans, ref bool available, ref long epoch) {
        var span = access.Span;
        var mirrorSpan = mirror?.DeliverySpan;

        available &= (
            (access.Level != WorldSessionAccess.None) &&
            latch.IsCurrent(span: span) &&
            ((mirrorSpan is null) || latch.IsCurrent(span: mirrorSpan))
        );
        epoch = Math.Max(val1: epoch, val2: access.Epoch);
        if (spans is null) {
            return;
        }
        spans.Add(item: span);
        if (mirrorSpan is not null) {
            spans.Add(item: mirrorSpan);
        }
    }
    // What a viewer's own residency may render: the install side of its access.
    //
    // Invariant: a residency that renders a session's mirror for its viewer holds nothing delivered under wider access
    // than the viewer has now. Mirror is the widest access the mirror's current content may have been delivered under
    // (a delivery seen between two settles is charged to the wider of the two), and Source the widest the residency's
    // frame source holds, which only grows while the source lives. The binder enforces it in two places: the settle
    // (SettleSessionRoutes) releases a source whose content is wider than an available viewer's access now, with the
    // residency and every job it submitted, so nothing they finish installs into what the viewer reads; and the one
    // door that makes a source (SessionResidencyOf, InfinityResidencyOf) makes none while the mirror holds wider content,
    // so the next one is made only from a delivery received under the narrower access. A viewer that may see nothing
    // keeps its source: nothing renders it then, and it is judged again when access returns.
    private sealed class ContentAccess(WorldSessionMirror mirror, WorldSessionAccess level) {
        private DeliveryMark m_seen = DeliveryMark.Of(mirror: mirror);
        private WorldSessionAccess m_settled = level;

        public WorldSessionAccess Mirror { get; private set; } = level;
        public WorldSessionAccess Source { get; private set; }

        // Settles the frame's access; true when the source must go.
        public bool Settle(WorldSessionMirror delivered, WorldSessionAccess access, bool holdsSource) {
            var seen = DeliveryMark.Of(mirror: delivered);

            if (seen != m_seen) {
                // A delivery inside one span may have been made under the wider of the two settles' access. A span that
                // moved (a cut, a close, a new stream) withdrew the mirror and primed it again under the stream's access
                // then, so what it holds now was delivered under the narrower access this settle reads.
                Mirror = (((m_settled > access) && !seen.MovedSpan(earlier: m_seen)) ? m_settled : access);
                m_seen = seen;
            }
            m_settled = access;
            if (!holdsSource) {
                Source = WorldSessionAccess.None;
                return false;
            }
            if (Mirror > Source) { Source = Mirror; }
            return ((access != WorldSessionAccess.None) && (access < Source));
        }
        // Whether a source may be made now for a viewer with this access.
        public bool MayMake(WorldSessionAccess access) => (Mirror <= access);
        public void Made() => Source = Mirror;
    }
    // A mirror's deliveries so far: the definitions, snapshots and state deliveries it applied, one of which moves with
    // every delivery, and the span its ingress holds.
    private readonly record struct DeliveryMark(long Definitions, long Snapshots, long States, ulong Stream, long Generation, bool Closed) {
        public static DeliveryMark Of(WorldSessionMirror mirror) {
            var span = mirror.Span;
            var applied = mirror.DeliveryWork;

            return new(Definitions: applied.Definitions, Snapshots: applied.Snapshots, States: applied.States, Stream: span.Stream, Generation: span.Generation, Closed: span.Closed);
        }
        // Whether the mirror's span moved since an earlier mark: its stream, its generation or its close.
        public bool MovedSpan(DeliveryMark earlier) => (Stream != earlier.Stream) || (Generation != earlier.Generation) || (Closed != earlier.Closed);
    }
    // A new session's route and a new infinity layer's access, settled from their observation as they are made, so a
    // view resolved before the next frame settles them reads the access they start with.
    private static WorldSessionRoute SettledRoute(WorldSessionObservation? observation) {
        var route = new WorldSessionRoute();

        _ = route.SettleAccess(access: AccessOf(observation: observation), admission: observation);
        return route;
    }
    private static WorldViewerAccess SettledAccess(WorldObservationSession? hosted) {
        var access = new WorldViewerAccess();

        _ = access.Settle(admission: hosted?.Observation, level: LayerAccessOf(hosted: hosted));
        return access;
    }
    // An infinity layer with no hosted session shows its own world's sky, which no session gates.
    private static WorldSessionAccess LayerAccessOf(WorldObservationSession? hosted) => ((hosted is null)
        ? WorldSessionAccess.Everything
        : AccessOf(observation: hosted.Observation));
    // The access an observation gives its viewer now: everything its world holds, what it discloses, or nothing.
    private static WorldSessionAccess AccessOf(WorldSessionObservation? observation) => ((observation is null)
        ? WorldSessionAccess.None
        : ((observation.SceneProof is { SharesAuthority: true })
            ? WorldSessionAccess.Everything
            : (observation.CanObserve ? WorldSessionAccess.Disclosed : WorldSessionAccess.None)));
}
