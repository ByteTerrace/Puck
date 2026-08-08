using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets;
using Puck.Authoring;
using Puck.Demo.World;
using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>
/// One sculpted creation living in the room as a presentation-only companion — the seam the three flagship avatars
/// (lantern-fish-with-camera-lure, CRT-faced robot, RPG humanoid) will inhabit. Owns a loaded, NORMALIZED
/// <see cref="CreationDocument"/>, render-clock wander steering, a timeline playback cursor (hold-style replay of the
/// document's <see cref="CreationDocument.Frames"/> at the SAME 8-tick cadence <see cref="CreatorScene.TickPlayback"/>
/// uses), and — for the robot archetype — a face channel state machine. Everything here is PURE PRESENTATION: the
/// deterministic sim is never read-for-write or written, exactly like the creator ghost. All motion rides the RENDER
/// clock (<c>deltaSeconds</c>), never the sim tick.
/// </summary>
public sealed class CompanionState {
    /// <summary>The playback hold per frame, in seconds — identical to <see cref="CreatorScene"/>'s default
    /// (<c>8f / 60f</c>, the settled 8-tick cadence at 60 Hz) so a companion's replay reads exactly like the
    /// authoring workbench's own preview.</summary>
    public const float SecondsPerFrame = (8f / 60f);
    // Wander tuning: a gentle amble, never a chase — the companion ambles toward/orbits the nearest player, it never
    // paces them like an escort. Kept slow so the room reads calm with several companions milling about.
    private const float WanderSpeed = 0.85f;
    private const float OrbitRadiansPerSecond = 0.35f;
    private const float OrbitRadius = 1.6f;
    private const float TurnRadiansPerSecond = 2.4f;
    // Hover-bob tuning (the swimmer heuristic's presentation): a slow vertical sinusoid plus a lazier yaw drift, read
    // as "finning in place" rather than the walker's forward amble.
    private const float HoverBobAmplitude = 0.12f;
    private const float HoverBobRadiansPerSecond = 1.1f;
    private const float HoverYawRadiansPerSecond = 0.5f;
    // The face auto-tune's dwell timer: a short dwell before committing to the preferred REMOTE feed (so a player
    // merely passing through the hail radius's edge doesn't flicker the face) — the snap back to the default (greeting)
    // feed on approach is instant (a player arriving should never wait to be greeted).
    private const float RemoteFeedDwellSeconds = 1.5f;
    /// <summary>The default (greeting) face feed's name — the feed a screen-faced creation shows when a player is
    /// present, and the always-present head of its face-feed list. A creation's behavior manifest may declare a
    /// different default, but this is the fallback when none is declared. Pure content string — the architecture
    /// names no specific feed (see [[abstractions-not-specifics]]).</summary>
    public const string DefaultFaceFeed = "emotes";
    /// <summary>The hail radius (world units): a player within this distance of the companion counts as "present" for
    /// the face auto-tune.</summary>
    public const float HailRadius = 3.0f;
    /// <summary>The hard cap on simultaneously loaded companions — the renderer's probe sizes its emission envelope
    /// against exactly this many.</summary>
    public const int MaxCompanions = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() {
        Converters = { new JsonStringEnumConverter() },
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly WorkbenchRegion m_bounds;
    private readonly CreationDocument m_document;
    private readonly float m_reach;
    private Vector3 m_position;
    private Quaternion m_rotation = Quaternion.Identity;
    private float m_orbitAngle;
    private float m_hoverPhase;
    // The swimmer's UN-BOBBED baseline height: TickHoverBob drifts THIS, then derives m_position.Y as an offset from
    // it each tick — keeping the sinusoid's amplitude constant rather than compounding onto the previous tick's
    // already-bobbed Y (which would random-walk the height until it pinned against the bounds).
    private float m_hoverBaseY;
    private int m_frameCursor;
    private float m_playClock;
    private readonly bool m_isSwimmer;
    // The ordered face-feed NAMES this companion can show (index 0 = the default/greeting feed; later entries are the
    // "remote" feeds the auto-tune drifts to when idle — e.g. a creation's own camera feed). Derived from the behavior
    // manifest's faces at load; always begins with the default feed so a screen-faced creation with no manifest still
    // has one feed to show. Pure names — no feed-specific TYPE exists (see [[abstractions-not-specifics]]).
    private readonly List<string> m_faceFeeds;
    // The currently resolved face feed name (what the host's named-feed registry should sample this frame).
    private string m_currentFaceFeed;
    // The pinned face feed name (an explicit companion.face verb) — null means the channel is free to auto-tune.
    private string? m_facePinned;
    private float m_remoteFeedDwellClock;

    /// <summary>Loads a companion from a normalized <see cref="CreationDocument"/> at a spawn position, clamped to
    /// steer inside <paramref name="bounds"/>.</summary>
    /// <param name="document">The normalized document (see <see cref="ResolveDocument"/>).</param>
    /// <param name="spawnPosition">Where the companion starts (clamped into <paramref name="bounds"/>).</param>
    /// <param name="bounds">The room region the companion's wander steering stays inside.</param>
    /// <param name="isSwimmer">Forces the swimmer (hover-bob) locomotion regardless of the document's behavior
    /// manifest — the console <c>swim</c> token's override. Null (the default) DEFERS to the manifest: a creation whose
    /// <see cref="CreationBehaviorDocument.Locomotion"/> is <c>swim</c> or <c>hover</c> swims, else it walks. See
    /// <see cref="IsSwimmer"/>'s remarks.</param>
    /// <param name="sourcePlacementId">The world document placement id this companion was dispatched from (see
    /// <see cref="SourcePlacementId"/>), or null for a session-only companion (<c>companion.add</c>).</param>
    public CompanionState(CreationDocument document, Vector3 spawnPosition, WorkbenchRegion bounds, bool? isSwimmer = null, int? sourcePlacementId = null) {
        ArgumentNullException.ThrowIfNull(document);

        m_document = document;
        m_bounds = bounds;
        m_position = bounds.Clamp(position: spawnPosition);
        m_hoverBaseY = m_position.Y;
        // Locomotion is a per-creation behavioral FACT (the manifest): swim/hover hover-bob, walk ambles. The explicit
        // flag, when supplied, overrides the manifest (the console verb's assist), else the manifest decides.
        m_isSwimmer = (isSwimmer ?? LocomotionSwims(behavior: document.Behavior));
        // The face-feed list: the default (greeting) feed always leads; the manifest's declared faces contribute their
        // named-feed default sources after it (deduped), so a creation that declares a camera face drifts to it when idle.
        m_faceFeeds = BuildFaceFeeds(behavior: document.Behavior);
        m_currentFaceFeed = m_faceFeeds[0];
        m_reach = ComputeReach(document: document);
        SourcePlacementId = sourcePlacementId;
    }

    /// <summary>Resolves a companion's document CAS-first: <paramref name="nameOrHash"/> is tried as a content-
    /// addressed store hash (either <c>sha256/&lt;hex64&gt;</c> or a bare hex64) before falling back to
    /// <see cref="CreationStore.Load"/> (a save handle under <c>./creations/</c> or an explicit file path) — the
    /// order <c>companion.add</c> wants, since a placed-in-the-world companion is more likely named by its CAS
    /// petname/hash than by the authoring save handle. The result is ALWAYS run through the same normalize path
    /// <see cref="CreationStore.Load"/> uses (never a hand-parsed record) so a companion sees exactly the same
    /// clamped/defaulted shape the editor would load.</summary>
    /// <param name="store">The content-addressed store to resolve a hash against.</param>
    /// <param name="nameOrHash">A CAS hash, save handle, or file path.</param>
    /// <returns>The normalized document, or null when nothing resolved either way.</returns>
    public static CreationDocument? ResolveDocument(ContentAddressedStore store, string nameOrHash) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(nameOrHash);

        if (LooksLikeHash(candidate: nameOrHash) && store.TryGet(hash: nameOrHash, content: out var bytes)) {
            var parsed = JsonSerializer.Deserialize<CreationDocument>(json: System.Text.Encoding.UTF8.GetString(bytes: bytes), options: JsonOptions);

            return ((parsed is null) ? null : Normalize(document: parsed));
        }

        return CreationStore.Load(nameOrPath: nameOrHash, creationsRoot: CreationStore.DefaultFolder);
    }

    /// <summary>The companion's currently loaded document (immutable once loaded; re-loading replaces the whole
    /// state via a fresh <see cref="CompanionState"/> rather than mutating in place).</summary>
    public CreationDocument Document => m_document;
    /// <summary>The world document placement id (<c>Role == "companion"</c>) this companion was dispatched from, or
    /// null for a session-only companion added via <c>companion.add</c>. <see cref="CompanionRoster.SpawnFromWorld"/>
    /// keys its dedupe on this so a re-applied world commit (a save's own hot-reload, or a repeat load) never spawns
    /// the same resident twice.</summary>
    public int? SourcePlacementId { get; }
    /// <summary>The creation's reach × scale — the travelling bound the renderer anchors its group instance on (see
    /// <see cref="CompanionRenderer"/>'s remarks: unlike the workbench's STATIC bound, this one travels with the
    /// companion's root).</summary>
    public float Reach => m_reach;
    /// <summary>The companion's current render-relative position (the root dynamic-transform slot's position).</summary>
    public Vector3 Position => m_position;
    /// <summary>The companion's current orientation (the root dynamic-transform slot's orientation).</summary>
    public Quaternion Rotation => m_rotation;
    /// <summary>The timeline cursor: 0 = rest (the document's authored pose when there are no frames), 1..N indexes
    /// <see cref="CreationDocument.Frames"/> (1-based, matching <see cref="CreatorScene.CurrentFrame"/>'s convention).
    /// This is the frame being blended AWAY from — pair it with <see cref="NextFrameCursor"/> and <see cref="FrameBlend"/>.</summary>
    public int FrameCursor => m_frameCursor;
    /// <summary>The frame the timeline is blending TOWARD (the loop-wrapped successor of <see cref="FrameCursor"/>):
    /// 0 when the document has no frames, else <c>(FrameCursor % Count) + 1</c>. <see cref="CompanionRenderer"/> lerps
    /// each shape's pose from <see cref="FrameCursor"/>'s snapshot to this one by <see cref="FrameBlend"/>.</summary>
    public int NextFrameCursor {
        get {
            var count = (m_document.Frames?.Count ?? 0);

            return ((count == 0) ? 0 : ((m_frameCursor % count) + 1));
        }
    }
    /// <summary>How far (0→1) the render clock has travelled from <see cref="FrameCursor"/> toward
    /// <see cref="NextFrameCursor"/> — the interpolation factor the renderer feeds to lerp/slerp. 0 at a frame's start,
    /// approaching 1 just before it flips to the next; always 0 for a rest-only (no-frames) document.</summary>
    public float FrameBlend => (((m_document.Frames?.Count ?? 0) == 0) ? 0f : Math.Clamp(value: (m_playClock / SecondsPerFrame), max: 1f, min: 0f));
    /// <summary>Whether this companion hover-bobs in place (a swimmer) instead of ambling on the floor plane — driven
    /// by the creation's behavior-manifest <see cref="CreationBehaviorDocument.Locomotion"/> (<c>swim</c>/<c>hover</c>
    /// hover-bob, <c>walk</c> ambles), or forced by the loading verb's explicit <c>swim</c> token. Never inferred from
    /// geometry — a per-creation behavioral FACT, either declared in the manifest or supplied at the call site.</summary>
    public bool IsSwimmer => m_isSwimmer;
    /// <summary>Whether this companion CAN show a screen face at all — a static capability derived from its behavior
    /// manifest (it declares at least one face), NOT a per-frame slot fact. Whether a face slab actually renders this
    /// frame is the host's business: the <see cref="Overworld.ScreenSlotLedger"/> is the SOLE owner of the resolved
    /// screen-surface slot (there is no mirror of it here — F-STATE-2), and the companion renderer reads that slot
    /// through the host per pass. Use this only for capability display (e.g. the <c>companion</c> status line), never
    /// to decide whether a slab emits.</summary>
    public bool HasFace => (m_document.Behavior?.Faces is { Count: > 0 });
    /// <summary>The CURRENT resolved face feed NAME (what the host's named-feed registry should sample onto this
    /// companion's face this frame) — meaningful only for a companion that <see cref="HasFace"/>. Always one of
    /// <see cref="FaceFeeds"/>.</summary>
    public string CurrentFaceFeed => m_currentFaceFeed;
    /// <summary>The ordered face-feed names this companion can show (index 0 = the default/greeting feed). A screen
    /// face wired to one of these names shows that feed; the auto-tune drifts among them.</summary>
    public IReadOnlyList<string> FaceFeeds => m_faceFeeds;
    /// <summary>The pinned face feed name, or null when the channel is free to auto-tune (see
    /// <see cref="SetFaceFeed"/>).</summary>
    public string? PinnedFaceFeed => m_facePinned;

    /// <summary>Pins the face to a specific feed NAME, or resumes auto-tune (the <c>companion.face</c> verb). A feed
    /// name PINS the face to it immediately (the channel snaps and never auto-switches); <c>auto</c> (a null name)
    /// resumes the hail-radius tune-in on the NEXT <see cref="TickFace"/>. A name not in <see cref="FaceFeeds"/> is
    /// still honored (the host registry decides what it resolves to — an unknown feed simply shows the flat fallback),
    /// so a face can be wired to another creation's feed or a world camera by name.</summary>
    /// <param name="feedName">The feed name to pin to, or null to resume auto-tune.</param>
    public void SetFaceFeed(string? feedName) {
        m_facePinned = feedName;

        if (feedName is { Length: > 0 }) {
            m_currentFaceFeed = feedName;
            m_remoteFeedDwellClock = 0f;
        }
    }

    /// <summary>Advances the timeline on the RENDER clock (never the sim tick): each frame plays for
    /// <see cref="SecondsPerFrame"/>, looping 1..<c>Frames.Count</c>. UNLIKE the workbench's hold-style
    /// <see cref="CreatorScene.TickPlayback"/>, a companion INTERPOLATES between frame snapshots — <see cref="FrameCursor"/>
    /// and <see cref="NextFrameCursor"/> straddle the current instant and <see cref="FrameBlend"/> (0→1) is how far
    /// between them the render clock has travelled, so <see cref="CompanionRenderer"/> lerps positions and slerps
    /// rotations. That single change is what turns a jerky N-pose flip-book into a smooth cycle. A document with no
    /// frames stays at cursor 0 (rest pose only) and this is a no-op past resetting the cursor.</summary>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void TickTimeline(float deltaSeconds) {
        var frames = (m_document.Frames ?? []);

        if (frames.Count == 0) {
            m_frameCursor = 0;
            m_playClock = 0f;

            return;
        }

        // The play clock stays a continuous fraction of ONE frame's dwell: it accumulates, and every time it crosses
        // a full frame the cursor advances (looping 1..Count) and the whole frames it crossed are subtracted — so a
        // large delta (a hitch, a slow producer) still lands on the right frame with the right leftover blend rather
        // than desyncing. FrameBlend then reads straight off the leftover.
        m_playClock += deltaSeconds;

        while (m_playClock >= SecondsPerFrame) {
            m_playClock -= SecondsPerFrame;
            m_frameCursor = ((m_frameCursor % frames.Count) + 1);
        }
    }

    /// <summary>Steers the companion on the RENDER clock: ambles toward/orbits <paramref name="nearestPlayer"/> when
    /// one is supplied, clamped inside <see cref="WorkbenchRegion"/> bounds; a swimmer (see <see cref="IsSwimmer"/>)
    /// hover-bobs in place instead of ambling on the floor. Presentation-only steering — never reads or writes the
    /// deterministic sim.</summary>
    /// <param name="nearestPlayer">The nearest active player's render-relative position, or null when no player is
    /// active (the companion idles at its last position).</param>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void TickWander(Vector3? nearestPlayer, float deltaSeconds) {
        if (m_isSwimmer) {
            TickHoverBob(nearestPlayer: nearestPlayer, deltaSeconds: deltaSeconds);

            return;
        }

        if (nearestPlayer is not { } target) {
            return;
        }

        var toTarget = (target - m_position);
        var planarDistance = new Vector2(x: toTarget.X, y: toTarget.Z).Length();
        Vector3 step;

        if (planarDistance > OrbitRadius) {
            // Outside the orbit ring: amble straight toward the player.
            var direction = ((planarDistance > 0.0001f) ? (toTarget / planarDistance) : Vector3.Zero);

            step = (direction * (WanderSpeed * deltaSeconds));
        } else {
            // Inside the ring: hold station on a slow orbit around the player rather than crowding them.
            m_orbitAngle += (OrbitRadiansPerSecond * deltaSeconds);

            var desired = (target + new Vector3(x: (MathF.Cos(x: m_orbitAngle) * OrbitRadius), y: 0f, z: (MathF.Sin(x: m_orbitAngle) * OrbitRadius)));

            step = ((desired - m_position) * MathF.Min(x: 1f, y: (WanderSpeed * deltaSeconds)));
        }

        var next = m_bounds.Clamp(position: (m_position + new Vector3(x: step.X, y: 0f, z: step.Z)));
        var moved = (next - m_position);

        m_position = next;

        if (new Vector2(x: moved.X, y: moved.Z).LengthSquared() > 0.0000001f) {
            var facing = MathF.Atan2(x: moved.Z, y: moved.X);

            m_rotation = TurnToward(current: m_rotation, targetYaw: facing, maxRadians: (TurnRadiansPerSecond * deltaSeconds));
        }
    }

    /// <summary>Advances the face-feed auto-tune state machine on the render clock (a no-op while a feed is pinned —
    /// see <see cref="SetFaceFeed"/>): a player within <see cref="HailRadius"/> snaps the face to the DEFAULT
    /// (greeting) feed instantly; with no player present AND the companion carries a preferred REMOTE feed (any
    /// face-feed past the default — e.g. its own camera feed), the face tunes to that remote feed after a short dwell
    /// (so a player merely grazing the hail radius's edge does not flicker the face); with no player and no remote feed
    /// to drift to, the face holds at the default. Generalized past any one creature's feed: "preferred remote feed"
    /// is the last entry in <see cref="FaceFeeds"/>, not a fish/lure-named channel.</summary>
    /// <param name="nearestPlayerDistance">The distance to the nearest active player, or null when none is active.</param>
    /// <param name="remoteFeedAvailable">Whether the preferred remote feed is actually live this frame (the host's
    /// registry resolved it) — a companion never drifts to a feed that is not producing pixels.</param>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void TickFace(float? nearestPlayerDistance, bool remoteFeedAvailable, float deltaSeconds) {
        if (m_facePinned is { Length: > 0 }) {
            return;
        }

        var defaultFeed = m_faceFeeds[0];
        var remoteFeed = m_faceFeeds[^1];
        var playerPresent = ((nearestPlayerDistance is { } distance) && (distance <= HailRadius));

        if (playerPresent) {
            m_currentFaceFeed = defaultFeed;
            m_remoteFeedDwellClock = 0f;

            return;
        }

        // No remote feed to drift to (a single-feed face, or the preferred remote feed isn't live this frame): hold at
        // the default and reset the dwell.
        if (string.Equals(a: remoteFeed, b: defaultFeed, comparisonType: StringComparison.Ordinal) || !remoteFeedAvailable) {
            m_remoteFeedDwellClock = 0f;

            return;
        }

        m_remoteFeedDwellClock += deltaSeconds;

        if (m_remoteFeedDwellClock >= RemoteFeedDwellSeconds) {
            m_currentFaceFeed = remoteFeed;
        }
    }

    private void TickHoverBob(Vector3? nearestPlayer, float deltaSeconds) {
        m_hoverPhase += (HoverBobRadiansPerSecond * deltaSeconds);
        m_orbitAngle += (HoverYawRadiansPerSecond * deltaSeconds);

        // A gentle drift toward the player's general direction (not a hard chase) — the bob still dominates the
        // read; a swimmer never fully stops drifting toward company, but never ambles like a walker either. The
        // drift moves the UN-BOBBED baseline (m_hoverBaseY / X/Z), never the already-bobbed m_position — otherwise
        // each tick's sinusoid offset would compound onto the last tick's offset and random-walk the height.
        if (nearestPlayer is { } target) {
            var toTarget = (target - new Vector3(x: m_position.X, y: m_hoverBaseY, z: m_position.Z));
            var planarDistance = new Vector2(x: toTarget.X, y: toTarget.Z).Length();

            if (planarDistance > OrbitRadius) {
                var direction = ((planarDistance > 0.0001f) ? (toTarget / planarDistance) : Vector3.Zero);
                var drift = (direction * ((WanderSpeed * 0.35f) * deltaSeconds));
                var driftedPlanar = m_bounds.Clamp(position: new Vector3(x: (m_position.X + drift.X), y: m_hoverBaseY, z: (m_position.Z + drift.Z)));

                m_position = new Vector3(x: driftedPlanar.X, y: m_position.Y, z: driftedPlanar.Z);
                m_hoverBaseY = driftedPlanar.Y;
            }
        }

        var bobbedY = Math.Clamp(value: (m_hoverBaseY + (MathF.Sin(x: m_hoverPhase) * HoverBobAmplitude)), max: m_bounds.MaxY, min: m_bounds.MinY);

        m_position = new Vector3(x: m_position.X, y: bobbedY, z: m_position.Z);
        m_rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: m_orbitAngle);
    }
    private static Quaternion TurnToward(Quaternion current, float targetYaw, float maxRadians) {
        if (maxRadians <= 0f) {
            return current;
        }

        var target = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: targetYaw);
        // A simple clamped Slerp step: close enough for presentation-only facing (never feeds determinism).
        var t = Math.Clamp(value: (maxRadians / MathF.PI), max: 1f, min: 0f);

        return Quaternion.Normalize(value: Quaternion.Slerp(quaternion1: current, quaternion2: target, amount: t));
    }

    // Whether the creation's behavior manifest asks it to swim/hover (hover-bob) rather than walk. A manifest-less
    // creation (or an explicit "walk") ambles.
    private static bool LocomotionSwims(CreationBehaviorDocument? behavior) =>
        ((behavior?.Locomotion is { } locomotion) &&
        (string.Equals(a: locomotion, b: "swim", comparisonType: StringComparison.OrdinalIgnoreCase) ||
         string.Equals(a: locomotion, b: "hover", comparisonType: StringComparison.OrdinalIgnoreCase)));

    // Builds the ordered face-feed name list: the default (greeting) feed always leads, then each behavior-manifest
    // face's declared default source resolved to a feed NAME (a "named:X" source contributes X; a "feed:N"/"brick:N"
    // source is an index-keyed wire the world's own table owns, not a companion face feed, so it is skipped here),
    // deduped, in manifest order. A manifest-less creation gets just the default feed. The FIRST manifest face whose
    // default source names a feed also SETS the default (greeting) feed — a creation can declare its own greeting feed
    // — while later faces contribute the remote feeds the auto-tune drifts to.
    private static List<string> BuildFaceFeeds(CreationBehaviorDocument? behavior) {
        var feeds = new List<string> { DefaultFaceFeed };

        foreach (var face in (behavior?.Faces ?? [])) {
            if (FeedNameFromSource(source: face.DefaultSource) is not { Length: > 0 } feedName) {
                continue;
            }

            // The first declared face is the greeting face — its named source becomes the default (index 0). Later
            // faces (or a repeat) append as remote feeds, deduped.
            if ((feeds.Count == 1) && string.Equals(a: feeds[0], b: DefaultFaceFeed, comparisonType: StringComparison.Ordinal)) {
                feeds[0] = feedName;
            } else if (!Enumerable.Contains(source: feeds, comparer: StringComparer.Ordinal, value: feedName)) {
                feeds.Add(item: feedName);
            }
        }

        return feeds;
    }

    // Resolves a wiring-grammar source token ("named:emotes", "feed:0", …) to a face-feed NAME, or null when the token
    // is not a named-feed source (a feed/brick index is the world wiring table's business, not a companion face feed).
    private static string? FeedNameFromSource(string? source) {
        if (source is not { Length: > 0 }) {
            return null;
        }

        const string namedPrefix = "named:";

        return ((source.StartsWith(value: namedPrefix, comparisonType: StringComparison.OrdinalIgnoreCase) && (source.Length > namedPrefix.Length))
            ? source[namedPrefix.Length..]
            : null);
    }
    private static float ComputeReach(CreationDocument document) {
        var reach = 0.5f;

        foreach (var shape in (document.Shapes ?? [])) {
            var scale = MathF.Max(x: shape.Scale.X, y: MathF.Max(x: shape.Scale.Y, y: shape.Scale.Z));
            var localReach = ((shape.Position.Length() + scale) + 0.9f);

            reach = MathF.Max(x: reach, y: localReach);
        }

        return reach;
    }

    // Mirrors CreationStore's private Normalize exactly (same clamps/defaults) so a CAS-resolved document sees the
    // identical shape a file-loaded one would — kept here rather than exposing CreationStore's private normalizer,
    // since CompanionState.cs is the only owned file with a reason to parse a document's raw JSON.
    private static CreationDocument Normalize(CreationDocument document) {
        var shapes = new List<ShapeDocument>(capacity: (document.Shapes?.Count ?? 0));

        foreach (var shape in (document.Shapes ?? [])) {
            shapes.Add(item: shape with {
                Bend = Math.Clamp(value: (shape.Bend ?? 0f), max: CreatorScene.MaxBend, min: -CreatorScene.MaxBend),
                Blend = (shape.Blend ?? SdfBlendOp.Union),
                Dilate = Math.Clamp(value: (shape.Dilate ?? 0f), max: CreatorScene.MaxDilate, min: 0f),
                Group = Math.Max(val1: (shape.Group ?? 0), val2: 0),
                Material = Math.Clamp(value: (shape.Material ?? 0), max: (CreatorScene.PaletteSize - 1), min: 0),
                Mirror = (shape.Mirror ?? false),
                Onion = Math.Clamp(value: (shape.Onion ?? 0f), max: CreatorScene.MaxOnion, min: 0f),
                Rotation = ((shape.Rotation == default) ? Quaternion.Identity : Quaternion.Normalize(value: shape.Rotation)),
                Scale = ((shape.Scale == default) ? Vector3.One : shape.Scale),
                Smooth = Math.Clamp(value: (shape.Smooth ?? 0f), max: CreatorScene.MaxSmooth, min: 0f),
                Twist = Math.Clamp(value: (shape.Twist ?? 0f), max: CreatorScene.MaxTwist, min: -CreatorScene.MaxTwist),
            });
        }

        return (document with {
            BakeStyle = (string.Equals(a: document.BakeStyle, b: "bold", comparisonType: StringComparison.OrdinalIgnoreCase) ? "bold" : "classic"),
            Intent = (document.Intent ?? CreatorIntent.Object),
            Name = (document.Name ?? "creation"),
            Schema = CreationDocument.CurrentSchema,
            Shapes = shapes,
        });
    }
    private static bool LooksLikeHash(string candidate) =>
        (candidate.StartsWith(value: "sha256/", comparisonType: StringComparison.Ordinal)
            ? (candidate.Length == ("sha256/".Length + 64))
            : ((candidate.Length == 64) && candidate.All(predicate: Uri.IsHexDigit)));
}

/// <summary>
/// The room's small roster of live companions — owns the <see cref="CompanionState.MaxCompanions"/> cap, add/remove,
/// and the per-frame tick pass (timeline + wander + face) every companion shares. The render node's frame source
/// composes ONE instance of this (mirroring how it composes <see cref="CreatorScene"/>), which
/// <see cref="CompanionRenderer"/> draws from.
/// </summary>
public sealed class CompanionRoster {
    private readonly List<CompanionState> m_companions = [];

    /// <summary>The live companions, in add order (index i backs the renderer's slot i and the command module's
    /// 1-based list index).</summary>
    public IReadOnlyList<CompanionState> Companions => m_companions;

    /// <summary>Adds a companion (a no-op returning false when the roster is already at
    /// <see cref="CompanionState.MaxCompanions"/>).</summary>
    /// <param name="companion">The loaded companion.</param>
    /// <returns>Whether it was added.</returns>
    public bool Add(CompanionState companion) {
        ArgumentNullException.ThrowIfNull(companion);

        if (m_companions.Count >= CompanionState.MaxCompanions) {
            return false;
        }

        m_companions.Add(item: companion);

        return true;
    }

    /// <summary>Removes the companion at <paramref name="index"/> (0-based).</summary>
    /// <param name="index">The companion's index.</param>
    /// <returns>Whether one was removed.</returns>
    public bool RemoveAt(int index) {
        if ((index < 0) || (index >= m_companions.Count)) {
            return false;
        }

        m_companions.RemoveAt(index: index);

        return true;
    }

    /// <summary>Removes every companion.</summary>
    /// <returns>How many were removed.</returns>
    public int Clear() {
        var count = m_companions.Count;

        m_companions.Clear();

        return count;
    }

    /// <summary>Dispatches <paramref name="document"/>'s <c>companion</c>-role placements into this roster —
    /// "inhabitants as data": a room declares its residents in the same document that declares its buildings,
    /// mirroring how <c>OverworldRoom.FromWorld</c> recognizes <c>cabinet:&lt;n&gt;</c> roles (a companion placement
    /// needs no index — the roster is order-independent, hard-capped at <see cref="CompanionState.MaxCompanions"/>,
    /// so "first N win" is the whole policy). Idempotent across repeated calls with the SAME or a re-saved document:
    /// a placement already represented (tracked via <see cref="CompanionState.SourcePlacementId"/>) is skipped, so a
    /// world.save's own hot-reload (which re-applies the document it just wrote) never duplicates a resident. Kept
    /// HERE (an instance method on the already-composed roster) rather than a new standalone type, so the frame
    /// source's world-load path — already at its analyzer coupling ceiling — names no new type to call it; only
    /// this file gains the <c>Puck.Demo.World</c> document types.</summary>
    /// <param name="document">The committed world document (null or placement-less = nothing to spawn).</param>
    /// <param name="store">The content-addressed store companion sources resolve against.</param>
    /// <param name="bounds">The room region the spawned companions steer inside.</param>
    public void SpawnFromWorld(WorldDocument? document, ContentAddressedStore store, WorkbenchRegion bounds) {
        ArgumentNullException.ThrowIfNull(store);

        if (document?.Placements is not { Count: > 0 } placements) {
            return;
        }

        foreach (var placement in placements) {
            if (m_companions.Count >= CompanionState.MaxCompanions) {
                return;
            }

            if (!string.Equals(a: placement.Role, b: "companion", comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            if (AlreadySpawnedFrom(placementId: placement.Id)) {
                continue;
            }

            if ((placement.Source is not { Length: > 0 } source) || (CompanionState.ResolveDocument(nameOrHash: source, store: store) is not { } creationDocument)) {
                continue;
            }

            _ = Add(companion: new CompanionState(bounds: bounds, document: creationDocument, sourcePlacementId: placement.Id, spawnPosition: placement.Position));
        }
    }

    // Whether a companion dispatched from this exact placement id is already in the roster — the dedupe a
    // re-applied world commit (a save's own hot-reload, or a repeat world.load) needs so it never spawns twice.
    private bool AlreadySpawnedFrom(int placementId) {
        for (var index = 0; (index < m_companions.Count); index++) {
            if (m_companions[index].SourcePlacementId == placementId) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Advances every companion's timeline + wander steering on the render clock, then resolves the face
    /// auto-tune (which needs to know whether each companion's preferred remote feed is live) — one pass, called once
    /// per produced frame by the host.</summary>
    /// <param name="nearestPlayerProvider">Resolves the nearest active player's render-relative position and its
    /// distance for a companion at a given position; null (or a null-returning provider) means no player is active.</param>
    /// <param name="remoteFeedProbe">Whether a companion's preferred remote face feed (see
    /// <see cref="CompanionState.FaceFeeds"/>) is actually producing pixels this frame — the host's named-feed registry
    /// answers it. Null (or a null result) means no remote feed is live, so the auto-tune holds at the default face.</param>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void Tick(Func<Vector3, (Vector3 Position, float Distance)?>? nearestPlayerProvider, Func<CompanionState, bool>? remoteFeedProbe, float deltaSeconds) {
        // remoteFeedProbe reads the companion object itself (never another companion's position), so it never
        // depends on the FIRST loop's per-companion TickWander having already run — both passes fold into one,
        // halving the nearestPlayerProvider calls (it was invoked twice per companion per tick).
        for (var index = 0; (index < m_companions.Count); index++) {
            var companion = m_companions[index];
            var nearest = nearestPlayerProvider?.Invoke(arg: companion.Position);

            companion.TickTimeline(deltaSeconds: deltaSeconds);
            companion.TickWander(nearestPlayer: (nearest?.Position), deltaSeconds: deltaSeconds);
            companion.TickFace(deltaSeconds: deltaSeconds, nearestPlayerDistance: (nearest?.Distance), remoteFeedAvailable: (remoteFeedProbe?.Invoke(arg: companion) ?? false));
        }
    }
}
