using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

// The world.rates bodies lever (SdfUpdateRates.Bodies): a body-rooted registration whose pixels per metre in every view
// that may see it fall to a tier (SdfUpdateRateTiers.BodyHertz) evaluates its pose, drivers, effectors and part followers
// at that tier's rate (SdfUpdateRateTiers.BodyTiers). Between those packs its root still follows the body every frame and its other slots ride the root
// rigidly, so the body moves smoothly while its limbs hold; the next full pack advances by every second the holds
// carried past. A body nothing may show or read (no camera, no shadow cascade, no consumer of its parts) holds until
// something may (HeldHertz), and its next full pack is a continuity cut, as a teleport's is, rather than an advance over
// everything it held. A placed creation (a row-rooted registration) decides through the same function (RateOf): it
// holds wherever nothing may show or read it, and takes the reduced tiers only where its creation's author allows them
// (CreationBehaviorDocument.ReducedRateAtDistance), sized for its measured part speed rather than a body's assumed one,
// updating every frame otherwise. The registrations that take a tier together alternate their phase by half its period, so their packs
// spread over the period's frames. The levers and the step flags are this frame's (PresentAt, before the pack); the
// views and shadow cascades are the frame just dressed, a frame's lag the tiers do not notice, so a camera cut shows a
// held body's held pose for one frame.
public sealed partial class WorldCreationInstances {
    // The packs this pool has run, the clock NotePoseRead stamps a registration's last read with.
    private long m_packCount;
    // The views the last frame rendered the pool's world through: each camera and its image's height in pixels.
    private readonly List<(CameraSnapshot Camera, float Pixels)> m_rateViews = [];
    // The shadow cascades of those views, each the renderer's own caster cull (SdfMeshCull) and the view it shadows.
    private readonly List<(int View, SdfMeshCull Cull)> m_rateShadows = [];
    private bool m_rateShadowsKnown;
    // Whether the next body to choose its phase within a tier takes the later half of the period.
    private bool m_rateLateHalf;

    private SdfUpdateRates m_rates;
    private WorkCounterSet? m_rateCounts;
    private SdfUpdateRateIntervals? m_rateIntervals;
    private SdfUpdateRateReadings? m_rateReadings;
    private PresentationRate m_presentationRate;
    // Whether the next pack writes its bodies' tiers into m_slotRateCodes, and the codes it wrote, by slot.
    private bool m_writesRateCodes;
    private byte[] m_slotRateCodes = [];

    // The latest pack's rated registrations' readings, published once it ends.
    private readonly SdfUpdateRateReadings.Buffer m_bodyReadings = new();

    /// <summary>Gets each dynamic-transform slot's <see cref="SdfUpdateRateCode"/> as the latest
    /// <see cref="PackTransforms"/> wrote it, indexed by slot, while <see cref="RateBy"/> asked for codes: a body-rooted
    /// registration's slots carry its tier and whether it posed that frame, and every other slot
    /// <see cref="SdfUpdateRateCode.None"/>. The pool reuses the array on its next pack.</summary>
    public ReadOnlyMemory<byte> SlotRateCodes => m_slotRateCodes;

    /// <summary>Sets the levers and the presentation rate this frame's <see cref="PackTransforms"/> rates its bodies
    /// under. Called each frame before the pack, so the pack reads this frame's step flags
    /// (<see cref="PresentationRate.Steps"/>, <see cref="PresentationRate.StepsNext"/>) and a settled capture's levers
    /// (none), never the previous frame's.</summary>
    /// <param name="rates">The frame's levers (<see cref="SdfFrame.UpdateRates"/>): none for a frame held for a settled
    /// capture, so every body poses in full before the capture freezes it.</param>
    /// <param name="rate">The presentation rate a held pose's floor is judged against; an unpaced rate holds none.</param>
    public void PresentAt(SdfUpdateRates rates, PresentationRate rate) {
        m_rates = rates;
        m_presentationRate = rate;
    }
    /// <summary>Records the views the frame source rendered the pool's world through, which the next
    /// <see cref="PackTransforms"/> rates its bodies by.</summary>
    /// <param name="counts">The counters the pool's rated bodies count into, or <see langword="null"/> for none.</param>
    /// <param name="intervals">The window the pool's rated bodies record their intervals between poses into, or
    /// <see langword="null"/> for none.</param>
    /// <param name="views">The frame's views.</param>
    /// <param name="height">The pixels down the image the views' regions divide.</param>
    /// <param name="readings">The readings the pool publishes its rated bodies' sizes and tiers into each pack, or
    /// <see langword="null"/> for none.</param>
    /// <param name="codes">Whether each pack writes its bodies' tiers into <see cref="SlotRateCodes"/>.</param>
    /// <param name="pixels">The display pixels each view is shown over, by view, or <see langword="null"/> for each
    /// view's region of <paramref name="height"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public void RateBy(WorkCounterSet? counts, SdfUpdateRateIntervals? intervals, IReadOnlyList<SdfViewSnapshot> views, uint height, SdfUpdateRateReadings? readings = null, bool codes = false, IReadOnlyList<float>? pixels = null) {
        ArgumentNullException.ThrowIfNull(argument: views);

        m_rateCounts = counts;
        m_rateIntervals = intervals;
        m_rateReadings = readings;
        m_writesRateCodes = codes;
        m_rateViews.Clear();

        for (var index = 0; (index < views.Count); index++) {
            m_rateViews.Add(item: (views[index].Camera, (((pixels is not null) && (index < pixels.Count)) ? pixels[index] : (views[index].Region.Height * height))));
        }
    }
    /// <summary>Records the shadow cascades of the views <see cref="RateBy"/> recorded, fitted on the frame just dressed
    /// with its own lights, which the next <see cref="PackTransforms"/> reads: a body no view may see but one of these may
    /// draw takes the slowest tier rather than holding.</summary>
    /// <param name="shadows">Each cascade's caster cull with the index of the view whose receivers it shadows, or
    /// <see langword="null"/> when the cascades are not known, which holds no body.</param>
    public void ShadowBy(IReadOnlyList<(int View, SdfMeshCull Cull)>? shadows) {
        m_rateShadows.Clear();
        m_rateShadowsKnown = (shadows is not null);

        if (shadows is not null) {
            m_rateShadows.AddRange(collection: shadows);
        }
    }

    // The one rate decision, made once a pack per registration: the tier it updates at and the pixels per metre it was
    // rated by. A registration takes its largest pixels per metre over the views that may see the sphere it reaches, is
    // held when nothing may show or read its pose (Shown), and updates every frame while the lever is off or no view is
    // known yet. A body's tiers are sized for SdfUpdateRateTiers.BodyPartSpeed. A row-rooted registration (a placed
    // creation) takes the reduced tiers only where its creation's author allows them
    // (CreationBehaviorDocument.ReducedRateAtDistance), sized for its own measured part speed (MeasurePartSpeed), and
    // every frame until that speed is measured; otherwise only the held tier, updating every frame wherever anything may
    // show or read it, so the lever changes how visible scenery animates only by the author's leave.
    private (int Hertz, float PixelsPerMetre) RateOf(Registration live, Vector3 center, float radius, float placementScale) {
        if (live.RatePack == m_packCount) {
            return (live.RateHertz, live.RatePixelsPerMetre);
        }

        var (hertz, pixelsPerMetre) = (SdfUpdateRateTiers.EveryFrameHertz, float.PositiveInfinity);

        if ((m_rates & SdfUpdateRates.Bodies) != 0) {
            pixelsPerMetre = BodyPixelsPerMetre(center: center, radius: radius);

            var shownElsewhere = ((pixelsPerMetre <= 0f) && Shown(center: center, live: live, radius: radius));

            hertz = ((live.BodyIndex is not null)
                ? SdfUpdateRateTiers.BodyHertz(pixelsPerMetre: pixelsPerMetre, shownElsewhere: shownElsewhere)
                : (ReducesAtDistance(live: live)
                    ? SdfUpdateRateTiers.BodyHertz(partSpeed: (live.PartSpeed * placementScale), pixelsPerMetre: pixelsPerMetre, shownElsewhere: shownElsewhere)
                    : (((pixelsPerMetre > 0f) || shownElsewhere) ? SdfUpdateRateTiers.EveryFrameHertz : SdfUpdateRateTiers.HeldHertz)));
        }

        live.RatePack = m_packCount;
        live.RateHertz = hertz;
        live.RatePixelsPerMetre = pixelsPerMetre;

        return (hertz, pixelsPerMetre);
    }
    // Whether a row-rooted registration's author lets it take the reduced tiers.
    private static bool ReducesAtDistance(Registration live) => (live.Creation.EngineDocument.Behavior?.ReducedRateAtDistance == true);
    // Measures a reducing placed creation's part speed after a full pack (the bound RateOf sizes its tiers for): each
    // part's travel relative to the root since the previous full pack, its centre's displacement plus the chord its
    // reach sweeps by its turn, over the seconds between the packs. The bound keeps the fastest any such interval
    // showed, in creation units a second, so a held pose never differs from the full-rate pose by more than the tier's
    // step for any speed already seen. An interval across a cut (a new registration, a reseat, a resume after an unseen
    // hold, a scale change) measures nothing and only restarts the comparison.
    private void MeasurePartSpeed(Registration live, in DynamicTransform root, int shapeCount, float elapsedSeconds, bool continuous) {
        if ((live.BodyIndex is not null) || !ReducesAtDistance(live: live) || (root.Scale <= 0f)) {
            return;
        }
        if (live.PartLocalPositions.Length != shapeCount) {
            live.PartLocalPositions = new Vector3[shapeCount];
            live.PartLocalOrientations = new Quaternion[shapeCount];
            continuous = false;
        }

        var reaches = ShapeReachesOf(live: live);
        var unturn = Quaternion.Conjugate(value: root.Orientation);
        var inverseScale = (1f / root.Scale);
        var measures = (continuous && (elapsedSeconds > 0f));
        var fastest = 0f;

        for (var index = 0; (index < shapeCount); index++) {
            var posed = live.ShapeTransforms[index];
            var position = (Vector3.Transform(rotation: unturn, value: (posed.Position - root.Position)) * inverseScale);
            var orientation = (unturn * posed.Orientation);

            if (measures && (reaches[index] >= 0f)) {
                var cosine = MathF.Min(x: 1f, y: MathF.Abs(x: Quaternion.Dot(quaternion1: orientation, quaternion2: live.PartLocalOrientations[index])));
                var travel = (Vector3.Distance(value1: position, value2: live.PartLocalPositions[index]) + ((2f * MathF.Sqrt(x: (1f - (cosine * cosine)))) * reaches[index]));

                fastest = MathF.Max(x: fastest, y: travel);
            }

            live.PartLocalPositions[index] = position;
            live.PartLocalOrientations[index] = orientation;
        }

        if (measures) {
            var speed = (fastest / elapsedSeconds);

            live.PartSpeed = (float.IsPositiveInfinity(f: live.PartSpeed) ? speed : MathF.Max(x: live.PartSpeed, y: speed));
        }
    }
    // Whether anything but a displayed view's image may show a body's pose or read it, so it may not hold: something
    // read one of its parts since the previous pack (NotePoseRead, the one record of a pose's consumers); a camera may
    // see the sphere it reaches though the display showed its view none last frame (a window about to open); or a shadow
    // cascade of any view may draw it where that view shows its shadow (the renderer's own caster cull, every body while
    // the cascades are not known).
    private bool Shown(Registration live, Vector3 center, float radius) {
        if ((live.PoseReadPack >= (m_packCount - 1L)) || !m_rateShadowsKnown) {
            return true;
        }

        var half = new Vector3(value: radius);

        foreach (var (camera, _) in m_rateViews) {
            if (camera.MaySee(center: center, halfX: (Vector3.UnitX * half.X), halfY: (Vector3.UnitY * half.Y), halfZ: (Vector3.UnitZ * half.Z), margin: WorldPortalVisibility.Margin)) {
                return true;
            }
        }

        var box = new SdfMeshBox(Center: center, HalfX: (Vector3.UnitX * radius), HalfY: (Vector3.UnitY * radius), HalfZ: (Vector3.UnitZ * radius));

        foreach (var (_, cull) in m_rateShadows) {
            if (cull.Keeps(box: box)) {
                return true;
            }
        }

        return false;
    }
    // Records that something outside the pack read a body registration's part pose, slot or latch this frame. Every
    // accessor that hands one out calls this, and it is the only record of a pose's consumers the bodies lever holds by.
    private void NotePoseRead(Registration live) => live.PoseReadPack = m_packCount;
    // A body's reach for rating, in metres: the larger of its creation's rest reach and the farthest any pack has posed
    // a part of it from its root (MotionReach), so a part a driver slides past the rest bound still counts.
    private static float RateReach(Registration live, float placementScale) => (MathF.Max(x: ReachOf(live: live), y: live.MotionReach) * placementScale);
    // A body's largest pixels per metre over the views that may see the sphere it reaches, at its nearest point: infinity
    // when no view is known yet or one holds it at or inside its near plane, zero when none may see it.
    private float BodyPixelsPerMetre(Vector3 center, float radius) {
        if (m_rateViews.Count == 0) {
            return float.PositiveInfinity;
        }

        var largest = 0f;
        var half = new Vector3(value: radius);

        foreach (var (camera, pixels) in m_rateViews) {
            // A view the display shows none of (a window out of every view's sight) sees nothing a body shows.
            if (pixels <= 0f) {
                continue;
            }
            if (!camera.MaySee(center: center, halfX: (Vector3.UnitX * half.X), halfY: (Vector3.UnitY * half.Y), halfZ: (Vector3.UnitZ * half.Z), margin: WorldPortalVisibility.Margin)) {
                continue;
            }

            var depth = (Vector3.Dot(vector1: (center - camera.Position), vector2: camera.Forward) - radius);

            if (depth <= camera.Near) {
                return float.PositiveInfinity;
            }

            largest = MathF.Max(x: largest, y: (pixels / ((2f * depth) * camera.TanHalfFieldOfView)));
        }

        return largest;
    }
    // Starts a pack's tiers: sizes the codes to every slot the pool packs and clears them, and clears the readings.
    private void BeginRateCodes(int slotBase) {
        m_bodyReadings.Clear();

        if (!m_writesRateCodes) {
            return;
        }

        var length = (slotBase + DynamicSlotCount);

        if (m_slotRateCodes.Length != length) {
            m_slotRateCodes = new byte[length];
        }

        Array.Clear(array: m_slotRateCodes);
    }
    // Notes a registration's tier after its pack (a body's, or a row-rooted placement's: an animated or attached one):
    // its reading while the bodies lever rates, and its slots' code while the pool writes codes.
    private void NoteRate(Registration live, int rootSlot, int packedSlots, Vector3 center, float radius, float placementScale, bool posed) {
        if ((m_rateReadings is null) && !m_writesRateCodes) {
            return;
        }

        var (hertz, pixelsPerMetre) = RateOf(center: center, live: live, placementScale: placementScale, radius: radius);

        if (((m_rates & SdfUpdateRates.Bodies) != 0) && (m_rateReadings is not null)) {
            live.RateName ??= ((live.BodyIndex is { } body) ? $"body:{body}" : $"placement:{live.Row?.Id}");
            m_bodyReadings.Add(item: new SdfUpdateRateReadings.Reading(Hertz: hertz, Name: live.RateName, Size: pixelsPerMetre));
        }
        if (m_writesRateCodes) {
            m_slotRateCodes.AsSpan(length: packedSlots, start: rootSlot).Fill(value: ((byte)SdfUpdateRateTiers.CodeOf(hertz: hertz, updated: posed)));
        }
    }
    // Ends a pack's tiers: publishes the bodies' readings.
    private void EndRateCodes() {
        if (m_rateReadings is { } readings) {
            m_bodyReadings.PublishTo(readings: readings, lever: SdfUpdateRates.Bodies);
        }
    }
    // Whether a woken body-rooted registration holds its pose this frame rather than packing it in full: the floor at its
    // tier still holds over the seconds since its last full pack with this frame's (SdfUpdateRateTiers.Holds), counted
    // ahead by its phase's lead. The slowest tier since that pack is the one its next interval is recorded under.
    private bool HoldsPose(Registration live, float deltaSeconds, Vector3 center, float radius, float placementScale) {
        var (hertz, _) = RateOf(center: center, live: live, placementScale: placementScale, radius: radius);

        live.RateSlowest = Math.Min(val1: live.RateSlowest, val2: hertz);
        ChoosePhase(hertz: hertz, live: live);

        return SdfUpdateRateTiers.Holds(
            elapsedSeconds: ((live.RateSeconds + deltaSeconds) + live.RateLead),
            frameSeconds: deltaSeconds,
            hertz: hertz,
            rate: m_presentationRate
        );
    }
    // Chooses a body's phase within the period of a tier it has just taken: every other body to do so packs half a period
    // early once, then keeps the period, so bodies that took the tier on one frame pack on alternate frames of it, not
    // all on one. The lead only moves the pack earlier: the floor still holds.
    private void ChoosePhase(Registration live, int hertz) {
        if (live.RatePhaseHertz == hertz) {
            return;
        }

        live.RatePhaseHertz = hertz;

        if ((hertz == SdfUpdateRateTiers.HeldHertz) || (hertz >= SdfUpdateRateTiers.EveryFrameHertz) || !m_presentationRate.IsPaced) {
            return;
        }

        live.RateLead = (m_rateLateHalf ? ((float)(m_presentationRate.PeriodSeconds(hertz: hertz) * 0.5d)) : 0f);
        m_rateLateHalf = !m_rateLateHalf;
    }
    // Forgets a body's phase, for a full pack its tier did not ask for: the body chooses again when it next holds.
    private static void ForgetPhase(Registration live) {
        live.RatePhaseHertz = SdfUpdateRateTiers.EveryFrameHertz;
        live.RateLead = 0f;
    }
    // Records a full pack's interval since the previous one under the slowest tier it was rated at between them, and
    // starts the next interval. A pack after a hold no view could see (HeldHertz) records none.
    private void RecordPose(Registration live, float deltaSeconds) {
        if ((live.RateSlowest != SdfUpdateRateTiers.HeldHertz) && (live.RateSlowest < SdfUpdateRateTiers.EveryFrameHertz)) {
            m_rateIntervals?.Record(hertz: live.RateSlowest, lever: SdfUpdateRates.Bodies, seconds: (live.RateSeconds + deltaSeconds));
        }

        live.RateSlowest = SdfUpdateRateTiers.EveryFrameHertz;
        live.RateLead = 0f;
    }
    // Advances a held registration's root as a full pack would: the root follows the body, through its dynamics when it
    // declares them. Returns the root pose the held slots ride.
    private static (Vector3 Position, Quaternion Rotation) FollowHeldRoot(Registration live, float deltaSeconds, Vector3 rootPosition, Quaternion rootRotation) {
        if (live.HasRootDynamics) {
            StepRootFollower(
                deltaSeconds: deltaSeconds,
                live: live,
                targetPosition: rootPosition,
                targetRotation: rootRotation
            );
        } else {
            live.FollowedPosition = rootPosition;
            live.FollowedOrientation = rootRotation;
        }

        return (live.FollowedPosition, live.FollowedOrientation);
    }
    // Whether a held root stands exactly where its slot holds it. Carrying by that root is the identity, and computing
    // the identity in float is not: a slot re-expressed through an unmoved root changes in its last bits on a quarter of
    // slots every frame, so the carry is skipped rather than computed.
    private static bool HeldRootStill(in DynamicTransform root, Vector3 rootPosition, Quaternion rootRotation) =>
        (root.Position.Equals(other: rootPosition) && root.Orientation.Equals(other: rootRotation));
    // Carries a held registration's slots with its root (FollowHeldRoot's pose): every other slot it packed rides the
    // root's motion since the last frame rigidly. Returns whether it changed a slot.
    private static bool CarryPose(Span<DynamicTransform> transforms, Registration live, int rootSlot, int packedSlots, Vector3 rootPosition, Quaternion rootRotation) {
        var root = transforms[rootSlot];
        var turn = Quaternion.Normalize(value: (rootRotation * Quaternion.Conjugate(value: root.Orientation)));
        var moved = false;

        for (var slot = (rootSlot + 1); (slot < (rootSlot + packedSlots)); slot++) {
            var held = transforms[slot];

            moved |= SdfMovedTransforms.Write(slot: slot, table: transforms, value: (held with {
                Orientation = Quaternion.Normalize(value: (turn * held.Orientation)),
                Position = (rootPosition + Vector3.Transform(rotation: turn, value: (held.Position - root.Position))),
            }));
        }

        for (var shape = 0; (shape < live.ShapeTransforms.Length); shape++) {
            var held = live.ShapeTransforms[shape];

            live.ShapeTransforms[shape] = held with { Orientation = Quaternion.Normalize((turn * held.Orientation)), Position = (rootPosition + Vector3.Transform((held.Position - root.Position), turn)) };
        }
        return (SdfMovedTransforms.Write(slot: rootSlot, table: transforms, value: (root with {
            Orientation = rootRotation,
            Position = rootPosition,
        })) | moved);
    }
}
