using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets;
using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>The robot archetype's face channel — which content the companion's screen-faced slab shows. Meaningful
/// only for a companion the host marks as the screen-faced one (<see cref="CompanionState.ScreenFaced"/>); harmless
/// (read but never acted on) for every other archetype.</summary>
public enum CompanionFaceChannel {
    /// <summary>The procedural expression feed (<see cref="Overworld.EmoteFeed"/>) — the default, and where a pinned
    /// "emote" mode always sits.</summary>
    Emotes,
    /// <summary>The lantern-fish companion's lure-lens camera feed (a <see cref="Overworld.CameraFeedEngine"/> feed) —
    /// where a pinned "lure" mode always sits, and where the auto-tune settles when no player is around to greet.</summary>
    LureCam,
}

/// <summary>Whether <see cref="CompanionState.FaceChannel"/> is free to auto-tune (the settled hail-radius behavior)
/// or pinned to one channel by an explicit <c>companion.face</c> verb.</summary>
public enum CompanionFacePin {
    /// <summary>Auto-tunes: <see cref="CompanionFaceChannel.LureCam"/> when no player is within the hail radius AND
    /// another companion exists to film, <see cref="CompanionFaceChannel.Emotes"/> the instant a player approaches.</summary>
    Auto,
    /// <summary>Pinned to <see cref="CompanionFaceChannel.Emotes"/> — never auto-switches.</summary>
    Emotes,
    /// <summary>Pinned to <see cref="CompanionFaceChannel.LureCam"/> — never auto-switches.</summary>
    LureCam,
}

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
    private const float OrbitRadius = 1.6f;
    private const float OrbitRadiansPerSecond = 0.35f;
    private const float TurnRadiansPerSecond = 2.4f;
    // Hover-bob tuning (the swimmer heuristic's presentation): a slow vertical sinusoid plus a lazier yaw drift, read
    // as "finning in place" rather than the walker's forward amble.
    private const float HoverBobAmplitude = 0.12f;
    private const float HoverBobRadiansPerSecond = 1.1f;
    private const float HoverYawRadiansPerSecond = 0.5f;
    // The face channel's auto-tune timers: a short dwell before committing to LureCam (so a player merely passing
    // through the hail radius's edge doesn't flicker the face) — the snap back to Emotes on approach is instant (a
    // player arriving should never wait to be greeted).
    private const float LureCamDwellSeconds = 1.5f;
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
    private bool m_screenFaced;
    private int m_screenIndex = -1;
    private CompanionFaceChannel m_faceChannel = CompanionFaceChannel.Emotes;
    private CompanionFacePin m_facePin = CompanionFacePin.Auto;
    private float m_lureCamDwellClock;

    /// <summary>Loads a companion from a normalized <see cref="CreationDocument"/> at a spawn position, clamped to
    /// steer inside <paramref name="bounds"/>.</summary>
    /// <param name="document">The normalized document (see <see cref="ResolveDocument"/>).</param>
    /// <param name="spawnPosition">Where the companion starts (clamped into <paramref name="bounds"/>).</param>
    /// <param name="bounds">The room region the companion's wander steering stays inside.</param>
    /// <param name="isSwimmer">Whether this companion hover-bobs (a swimmer) instead of walking — an explicit flag
    /// set by the loading verb, defaulting to false (walk); see <see cref="IsSwimmer"/>'s remarks for the heuristic.</param>
    public CompanionState(CreationDocument document, Vector3 spawnPosition, WorkbenchRegion bounds, bool isSwimmer = false) {
        ArgumentNullException.ThrowIfNull(document);

        m_document = document;
        m_bounds = bounds;
        m_position = bounds.Clamp(position: spawnPosition);
        m_hoverBaseY = m_position.Y;
        m_isSwimmer = isSwimmer;
        m_reach = ComputeReach(document: document);
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

        return CreationStore.Load(nameOrPath: nameOrHash);
    }

    /// <summary>The companion's currently loaded document (immutable once loaded; re-loading replaces the whole
    /// state via a fresh <see cref="CompanionState"/> rather than mutating in place).</summary>
    public CreationDocument Document => m_document;
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
    /// <summary>Whether this companion hover-bobs in place (a swimmer) instead of ambling on the floor plane — an
    /// explicit per-companion flag (never inferred from geometry): the loading verb sets it from an optional "swim"
    /// token, keeping the heuristic simple and visible at the call site rather than sniffing the creation's
    /// name/shapes for intent.</summary>
    public bool IsSwimmer => m_isSwimmer;
    /// <summary>Whether the host has marked this companion as the screen-faced one (the robot archetype) — set via
    /// <see cref="SetScreenFaced"/>. When true and a screen index is assigned (see <see cref="ScreenIndex"/>), the
    /// renderer emits the face slab.</summary>
    public bool ScreenFaced => m_screenFaced;
    /// <summary>The screen-surface slot the host's <see cref="Overworld.ScreenSlotLedger"/> assigned this companion's
    /// face, or -1 when none is assigned (the renderer then degrades to the flat lit material — the same
    /// no-index-no-diegetic-screen story cabinets follow).</summary>
    public int ScreenIndex => m_screenIndex;
    /// <summary>The face channel's CURRENT resolved value (what the host's screen-source mux should sample this
    /// frame) — meaningful only when <see cref="ScreenFaced"/>.</summary>
    public CompanionFaceChannel FaceChannel => m_faceChannel;
    /// <summary>Whether the face channel is free to auto-tune or pinned (see <see cref="SetFacePin"/>).</summary>
    public CompanionFacePin FacePin => m_facePin;

    /// <summary>Assigns (or clears, with a negative index) the companion's screen-faced role and its ledger-granted
    /// slot. Called by the host every frame/rebuild (mirrors <see cref="Overworld.ScreenSlotLedger"/>'s per-pass
    /// re-claim contract) — never persisted, never inferred.</summary>
    /// <param name="screenFaced">Whether this companion is the screen-faced (robot) one.</param>
    /// <param name="screenIndex">The granted screen-surface slot, or -1 when none was granted this pass.</param>
    public void SetScreenFaced(bool screenFaced, int screenIndex) {
        m_screenFaced = screenFaced;
        m_screenIndex = (screenFaced ? screenIndex : -1);
    }

    /// <summary>Pins or unpins the face channel (the <c>companion.face</c> verb's <c>emote</c>/<c>lure</c>/<c>auto</c>
    /// arguments). A pin takes effect immediately (the channel snaps to the pinned value); <see cref="CompanionFacePin.Auto"/>
    /// resumes the hail-radius tune-in on the NEXT <see cref="TickFace"/>.</summary>
    /// <param name="pin">The desired pin state.</param>
    public void SetFacePin(CompanionFacePin pin) {
        m_facePin = pin;

        if (pin != CompanionFacePin.Auto) {
            m_faceChannel = ((pin == CompanionFacePin.LureCam) ? CompanionFaceChannel.LureCam : CompanionFaceChannel.Emotes);
            m_lureCamDwellClock = 0f;
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
        var planarDistance = new Vector2(toTarget.X, toTarget.Z).Length();
        Vector3 step;

        if (planarDistance > OrbitRadius) {
            // Outside the orbit ring: amble straight toward the player.
            var direction = ((planarDistance > 0.0001f) ? (toTarget / planarDistance) : Vector3.Zero);

            step = (direction * (WanderSpeed * deltaSeconds));
        } else {
            // Inside the ring: hold station on a slow orbit around the player rather than crowding them.
            m_orbitAngle += (OrbitRadiansPerSecond * deltaSeconds);

            var desired = (target + new Vector3((MathF.Cos(m_orbitAngle) * OrbitRadius), 0f, (MathF.Sin(m_orbitAngle) * OrbitRadius)));

            step = ((desired - m_position) * MathF.Min(1f, (WanderSpeed * deltaSeconds)));
        }

        var next = m_bounds.Clamp(position: (m_position + new Vector3(step.X, 0f, step.Z)));
        var moved = (next - m_position);

        m_position = next;

        if (new Vector2(moved.X, moved.Z).LengthSquared() > 0.0000001f) {
            var facing = MathF.Atan2(moved.X, moved.Z);

            m_rotation = TurnToward(current: m_rotation, targetYaw: facing, maxRadians: (TurnRadiansPerSecond * deltaSeconds));
        }
    }

    /// <summary>Advances the face channel's auto-tune state machine on the render clock (a no-op while
    /// <see cref="FacePin"/> is not <see cref="CompanionFacePin.Auto"/>): a player within <see cref="HailRadius"/>
    /// snaps the channel to <see cref="CompanionFaceChannel.Emotes"/> instantly; with no player present AND another
    /// companion to film (<paramref name="anotherCompanionPresent"/>), the channel switches to
    /// <see cref="CompanionFaceChannel.LureCam"/> after a short dwell (so a player merely grazing the hail radius's
    /// edge does not flicker the face); with no player and nothing to film, the channel holds at Emotes.</summary>
    /// <param name="nearestPlayerDistance">The distance to the nearest active player, or null when none is active.</param>
    /// <param name="anotherCompanionPresent">Whether another companion exists in the room (the fish filming).</param>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void TickFace(float? nearestPlayerDistance, bool anotherCompanionPresent, float deltaSeconds) {
        if (m_facePin != CompanionFacePin.Auto) {
            return;
        }

        var playerPresent = (nearestPlayerDistance is { } distance && (distance <= HailRadius));

        if (playerPresent) {
            m_faceChannel = CompanionFaceChannel.Emotes;
            m_lureCamDwellClock = 0f;

            return;
        }

        if (!anotherCompanionPresent) {
            m_lureCamDwellClock = 0f;

            return;
        }

        m_lureCamDwellClock += deltaSeconds;

        if (m_lureCamDwellClock >= LureCamDwellSeconds) {
            m_faceChannel = CompanionFaceChannel.LureCam;
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
            var toTarget = (target - new Vector3(m_position.X, m_hoverBaseY, m_position.Z));
            var planarDistance = new Vector2(toTarget.X, toTarget.Z).Length();

            if (planarDistance > OrbitRadius) {
                var direction = ((planarDistance > 0.0001f) ? (toTarget / planarDistance) : Vector3.Zero);
                var drift = (direction * (WanderSpeed * 0.35f * deltaSeconds));
                var driftedPlanar = m_bounds.Clamp(position: new Vector3((m_position.X + drift.X), m_hoverBaseY, (m_position.Z + drift.Z)));

                m_position = new Vector3(driftedPlanar.X, m_position.Y, driftedPlanar.Z);
                m_hoverBaseY = driftedPlanar.Y;
            }
        }

        var bobbedY = Math.Clamp(value: (m_hoverBaseY + (MathF.Sin(m_hoverPhase) * HoverBobAmplitude)), max: m_bounds.MaxY, min: m_bounds.MinY);

        m_position = new Vector3(m_position.X, bobbedY, m_position.Z);
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

    private static float ComputeReach(CreationDocument document) {
        var reach = 0.5f;

        foreach (var shape in (document.Shapes ?? [])) {
            var scale = MathF.Max(shape.Scale.X, MathF.Max(shape.Scale.Y, shape.Scale.Z));
            var localReach = ((shape.Position.Length() + scale) + 0.9f);

            reach = MathF.Max(reach, localReach);
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
                Blend = (shape.Blend ?? SdfBlendOp.Union),
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
/// composes ONE instance of this (mirroring how it composes <see cref="CreatorScene"/>), so
/// <see cref="CompanionRenderer"/> and <see cref="CompanionCommandModule"/> both drive the SAME roster.
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

    /// <summary>Advances every companion's timeline + wander steering on the render clock, then resolves the face
    /// auto-tune (which needs to know whether ANOTHER companion exists to film) — one pass, called once per produced
    /// frame by the host.</summary>
    /// <param name="nearestPlayerProvider">Resolves the nearest active player's render-relative position and its
    /// distance for a companion at a given position; null (or a null-returning provider) means no player is active.</param>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void Tick(Func<Vector3, (Vector3 Position, float Distance)?>? nearestPlayerProvider, float deltaSeconds) {
        for (var index = 0; (index < m_companions.Count); index++) {
            var companion = m_companions[index];
            var nearest = nearestPlayerProvider?.Invoke(arg: companion.Position);

            companion.TickTimeline(deltaSeconds: deltaSeconds);
            companion.TickWander(nearestPlayer: (nearest?.Position), deltaSeconds: deltaSeconds);
        }

        var anotherPresent = (m_companions.Count > 1);

        for (var index = 0; (index < m_companions.Count); index++) {
            var companion = m_companions[index];
            var nearest = nearestPlayerProvider?.Invoke(arg: companion.Position);

            companion.TickFace(anotherCompanionPresent: anotherPresent, deltaSeconds: deltaSeconds, nearestPlayerDistance: (nearest?.Distance));
        }
    }
}
