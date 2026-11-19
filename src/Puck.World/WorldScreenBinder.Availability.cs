using System.Globalization;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    /// <inheritdoc/>
    /// <remarks>Reads the access each session along the view's chain settled this frame
    /// (<see cref="WorldSessionRoute.Access"/>, settled in <see cref="Publish"/>): a session view, a nested camera or
    /// child of one and an infinity layer are available only while every session they are seen through may observe,
    /// at the latest epoch among them.</remarks>
    public WorldViewAccess AccessOf(string view) {
        if (SessionFeedOf(view) is { } feed) { return Access(feed); }
        if (m_infinityOutputs.TryGetValue(view, out var infinity)) { return Access(infinity.Entry); }
        if (NestedCameraLevel(view) is { } level && m_nestedOwners.TryGetValue(level, out var owner)) {
            var access = WorldViewAccess.Open;
            if (owner.Feed is { } nested) { access = access.Join(Access(nested)); }
            if (owner.Infinity is { } layer) { access = access.Join(Access(layer)); }
            return access;
        }
        // Historical package entries can outlive the view roster. A retired generated observation is not a new
        // unnamed host view, and the session it showed is gone. Recognize the minted path grammar, not arbitrary
        // prefixes (for example its environment buffer is a different instance and is not an observation image).
        return (ObservationName(view) ? WorldViewAccess.Retired : WorldViewAccess.Open);
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
    private static WorldViewAccess Access(InfinityEntry infinity) => new(
        Available: (infinity.Access.Level != WorldSessionAccess.None),
        Epoch: infinity.Access.Epoch
    );
    private static WorldViewAccess Access(SessionFeed feed) {
        var access = WorldViewAccess.Open;

        for (var current = feed; current is not null; current = current.ParentFeed) {
            access = access.Join(other: new WorldViewAccess(
                Available: (current.Route.Access.Level != WorldSessionAccess.None),
                Epoch: current.Route.Access.Epoch
            ));
            if (current.ParentInfinity is { } infinity) { access = access.Join(other: Access(infinity: infinity)); }
        }
        return access;
    }
    private static bool Available(SessionFeed feed) => Access(feed: feed).Available;
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
                m_seen = seen;
                Mirror = ((m_settled > access) ? m_settled : access);
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
    // A mirror's deliveries so far: its definition and snapshot revisions, either of which moves with a delivery.
    private readonly record struct DeliveryMark(int Definition, int Snapshot) {
        public static DeliveryMark Of(WorldSessionMirror mirror) => new(Definition: mirror.DefinitionRevision, Snapshot: mirror.SnapshotRevision);
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
        : (observation.DisclosesEverything
            ? WorldSessionAccess.Everything
            : (observation.CanObserve ? WorldSessionAccess.Disclosed : WorldSessionAccess.None)));
}
