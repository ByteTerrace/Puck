using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

// The world.rates bodies lever (SdfUpdateRates.Bodies): a body-rooted registration whose pixels per metre in every view
// that may see it fall to a tier (SdfUpdateRateTiers.BodyHertz) evaluates its pose, drivers, effectors and part followers
// at 30 or 15 Hz. Between those packs its root still follows the body every frame and its other slots ride the root
// rigidly, so the body moves smoothly while its limbs hold; the next full pack advances by every second the holds
// carried past. A body no view may see, nor the shadow a cascade draws of it, holds until one may (HeldHertz), and its
// next full pack is a continuity cut, as a teleport's is, rather than an advance over everything it held. The bodies that
// take a tier together alternate their phase by half its period, so their packs spread over the period's frames. The
// views are the frame source's previous frame's, a frame's lag the tiers do not notice.
public sealed partial class WorldCreationInstances {
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

    // The latest pack's bodies' readings, published once it ends, and each body's name in them, by body index.
    private readonly List<SdfUpdateRateReadings.Reading> m_bodyReadings = [];
    private readonly Dictionary<int, string> m_bodyNames = [];

    /// <summary>Gets each dynamic-transform slot's <see cref="SdfUpdateRateCode"/> as the latest
    /// <see cref="PackTransforms"/> wrote it, indexed by slot, while <see cref="RateBy"/> asked for codes: a body-rooted
    /// registration's slots carry its tier and whether it posed that frame, and every other slot
    /// <see cref="SdfUpdateRateCode.None"/>. The pool reuses the array on its next pack.</summary>
    public ReadOnlyMemory<byte> SlotRateCodes => m_slotRateCodes;

    /// <summary>Records the views the frame source rendered the pool's world through and the levers it rendered under,
    /// which the next <see cref="PackTransforms"/> rates its bodies by.</summary>
    /// <param name="rates">The frame's levers (<see cref="SdfFrame.UpdateRates"/>).</param>
    /// <param name="rate">The presentation rate a held pose's floor is judged against; an unpaced rate holds none.</param>
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
    /// <param name="shadows">The shadow cascades' caster culls, each with the index of the view in
    /// <paramref name="views"/> whose receivers it shadows: a body no view may see but one of these may draw takes the
    /// slowest tier rather than holding. <see langword="null"/> when the cascades are not known, which holds no body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public void RateBy(SdfUpdateRates rates, PresentationRate rate, WorkCounterSet? counts, SdfUpdateRateIntervals? intervals, IReadOnlyList<SdfViewSnapshot> views, uint height, SdfUpdateRateReadings? readings = null, bool codes = false, IReadOnlyList<float>? pixels = null, IReadOnlyList<(int View, SdfMeshCull Cull)>? shadows = null) {
        ArgumentNullException.ThrowIfNull(argument: views);

        m_rates = rates;
        m_presentationRate = rate;
        m_rateCounts = counts;
        m_rateIntervals = intervals;
        m_rateReadings = readings;
        m_writesRateCodes = codes;
        m_rateViews.Clear();
        m_rateShadows.Clear();
        m_rateShadowsKnown = (shadows is not null);

        for (var index = 0; (index < views.Count); index++) {
            m_rateViews.Add(item: (views[index].Camera, (((pixels is not null) && (index < pixels.Count)) ? pixels[index] : (views[index].Region.Height * height))));
        }
        if (shadows is not null) {
            m_rateShadows.AddRange(collection: shadows);
        }
    }

    // The rate a body updates at: its largest pixels per metre over the views that may see the sphere it reaches, held
    // when neither a view nor a shadow it casts into one may show it, and every frame while the lever is off or no view
    // is known yet.
    private int BodyHertz(Vector3 center, float radius) => (((m_rates & SdfUpdateRates.Bodies) == 0)
        ? SdfUpdateRateTiers.EveryFrameHertz
        : TierOf(center: center, pixelsPerMetre: BodyPixelsPerMetre(center: center, radius: radius), radius: radius));
    private int TierOf(Vector3 center, float radius, float pixelsPerMetre) => SdfUpdateRateTiers.BodyHertz(
        pixelsPerMetre: pixelsPerMetre,
        shadowShown: ((pixelsPerMetre <= 0f) && ShadowShown(center: center, radius: radius))
    );
    // Whether a shadow cascade of a view the display shows may draw the sphere a body reaches where that view shows its
    // shadow: the renderer's own caster cull over the sphere's box; every body while the cascades are not known.
    private bool ShadowShown(Vector3 center, float radius) {
        if (!m_rateShadowsKnown) {
            return true;
        }

        var box = new SdfMeshBox(Center: center, HalfX: (Vector3.UnitX * radius), HalfY: (Vector3.UnitY * radius), HalfZ: (Vector3.UnitZ * radius));

        foreach (var (view, cull) in m_rateShadows) {
            if ((view < m_rateViews.Count) && (m_rateViews[view].Pixels > 0f) && cull.Keeps(box: box)) {
                return true;
            }
        }

        return false;
    }
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
    // Notes a body-rooted registration's tier after its pack: its reading while the bodies lever rates, and its slots'
    // code while the pool writes codes.
    private void NoteRate(Registration live, int rootSlot, int packedSlots, Vector3 center, float radius, bool posed) {
        if ((live.BodyIndex is not { } body) || ((m_rateReadings is null) && !m_writesRateCodes)) {
            return;
        }

        var rated = ((m_rates & SdfUpdateRates.Bodies) != 0);
        var pixelsPerMetre = (rated ? BodyPixelsPerMetre(center: center, radius: radius) : float.PositiveInfinity);
        var hertz = (rated ? TierOf(center: center, pixelsPerMetre: pixelsPerMetre, radius: radius) : SdfUpdateRateTiers.EveryFrameHertz);

        if (rated && (m_rateReadings is not null)) {
            if (!m_bodyNames.TryGetValue(key: body, value: out var name)) {
                name = $"body:{body}";
                m_bodyNames[body] = name;
            }

            m_bodyReadings.Add(item: new SdfUpdateRateReadings.Reading(Hertz: hertz, Name: name, Size: pixelsPerMetre));
        }
        if (m_writesRateCodes) {
            m_slotRateCodes.AsSpan(length: packedSlots, start: rootSlot).Fill(value: ((byte)SdfUpdateRateTiers.CodeOf(hertz: hertz, updated: posed)));
        }
    }
    // Ends a pack's tiers: publishes the bodies' readings.
    private void EndRateCodes() => m_rateReadings?.Publish(lever: SdfUpdateRates.Bodies, readings: m_bodyReadings);
    // Whether a woken body-rooted registration holds its pose this frame rather than packing it in full: the floor at its
    // tier still holds over the seconds since its last full pack with this frame's (SdfUpdateRateTiers.Holds), counted
    // ahead by its phase's lead. The slowest tier since that pack is the one its next interval is recorded under.
    private bool HoldsPose(Registration live, float deltaSeconds, Vector3 center, float radius) {
        var hertz = BodyHertz(center: center, radius: radius);

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
    // Carries a held registration's slots with its root: the root follows the body as a full pack would, and every
    // other slot it packed rides the root's motion since the last frame rigidly.
    private static void CarryPose(Span<DynamicTransform> transforms, Registration live, int rootSlot, int packedSlots, float deltaSeconds, Vector3 rootPosition, Quaternion rootRotation) {
        if (live.HasRootDynamics) {
            StepRootFollower(
                deltaSeconds: deltaSeconds,
                live: live,
                targetPosition: rootPosition,
                targetRotation: rootRotation
            );
            (rootPosition, rootRotation) = (live.FollowedPosition, live.FollowedOrientation);
        } else {
            live.FollowedPosition = rootPosition;
            live.FollowedOrientation = rootRotation;
        }

        var root = transforms[rootSlot];
        var turn = Quaternion.Normalize(value: (rootRotation * Quaternion.Conjugate(value: root.Orientation)));

        for (var slot = (rootSlot + 1); (slot < (rootSlot + packedSlots)); slot++) {
            var held = transforms[slot];

            transforms[slot] = (held with {
                Orientation = Quaternion.Normalize(value: (turn * held.Orientation)),
                Position = (rootPosition + Vector3.Transform(rotation: turn, value: (held.Position - root.Position))),
            });
        }

        for (var shape = 0; (shape < live.ShapeTransforms.Length); shape++) {
            var held = live.ShapeTransforms[shape];

            live.ShapeTransforms[shape] = held with { Orientation = Quaternion.Normalize((turn * held.Orientation)), Position = (rootPosition + Vector3.Transform((held.Position - root.Position), turn)) };
        }
        transforms[rootSlot] = (root with {
            Orientation = rootRotation,
            Position = rootPosition,
        });
    }
}
