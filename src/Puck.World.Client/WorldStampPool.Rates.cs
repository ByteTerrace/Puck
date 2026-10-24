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
// carried past. The views are the frame source's previous frame's, a frame's lag the tiers do not notice.
public sealed partial class WorldStampPool {
    // The views the last frame rendered the pool's world through: each camera and its image's height in pixels.
    private readonly List<(CameraSnapshot Camera, float Pixels)> m_rateViews = [];
    private SdfUpdateRates m_rates;
    private WorkCounterSet? m_rateCounts;
    private SdfUpdateRateIntervals? m_rateIntervals;
    private PresentationRate m_presentationRate;

    /// <summary>Records the views the frame source rendered the pool's world through and the levers it rendered under,
    /// which the next <see cref="PackTransforms"/> rates its bodies by.</summary>
    /// <param name="rates">The frame's levers (<see cref="SdfFrame.UpdateRates"/>).</param>
    /// <param name="rate">The presentation rate a held pose's floor is judged against; an unpaced rate holds none.</param>
    /// <param name="counts">The counters the pool's rated bodies count into, or <see langword="null"/> for none.</param>
    /// <param name="intervals">The window the pool's rated bodies record their intervals between poses into, or
    /// <see langword="null"/> for none.</param>
    /// <param name="views">The frame's views.</param>
    /// <param name="height">The pixels down the image the views' regions divide.</param>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public void RateBy(SdfUpdateRates rates, PresentationRate rate, WorkCounterSet? counts, SdfUpdateRateIntervals? intervals, IReadOnlyList<SdfViewSnapshot> views, uint height) {
        ArgumentNullException.ThrowIfNull(argument: views);

        m_rates = rates;
        m_presentationRate = rate;
        m_rateCounts = counts;
        m_rateIntervals = intervals;
        m_rateViews.Clear();

        for (var index = 0; (index < views.Count); index++) {
            m_rateViews.Add(item: (views[index].Camera, (views[index].Region.Height * height)));
        }
    }
    // The rate a body updates at: its largest pixels per metre over the views that may see the sphere it reaches, every
    // frame while the lever is off or no view is known yet.
    private int BodyHertz(Vector3 center, float radius) {
        if (((m_rates & SdfUpdateRates.Bodies) == 0) || (m_rateViews.Count == 0)) {
            return SdfUpdateRateTiers.EveryFrameHertz;
        }

        var largest = 0f;
        var half = new Vector3(value: radius);

        foreach (var (camera, pixels) in m_rateViews) {
            if (!camera.MaySee(center: center, halfX: (Vector3.UnitX * half.X), halfY: (Vector3.UnitY * half.Y), halfZ: (Vector3.UnitZ * half.Z), margin: WorldPortalVisibility.Margin)) {
                continue;
            }

            var depth = (Vector3.Dot(vector1: (center - camera.Position), vector2: camera.Forward) - radius);

            if (depth <= camera.Near) {
                return SdfUpdateRateTiers.EveryFrameHertz;
            }

            largest = MathF.Max(x: largest, y: (pixels / ((2f * depth) * camera.TanHalfFieldOfView)));
        }

        return SdfUpdateRateTiers.BodyHertz(pixelsPerMetre: largest);
    }
    // Whether a woken body-rooted registration holds its pose this frame rather than packing it in full: the floor at its
    // tier still holds over the seconds since its last full pack with this frame's (SdfUpdateRateTiers.Holds). The
    // slowest tier since that pack is the one its next interval is recorded under.
    private bool HoldsPose(Registration live, float deltaSeconds, Vector3 center, float radius) {
        var hertz = BodyHertz(center: center, radius: radius);

        live.RateSlowest = Math.Min(val1: live.RateSlowest, val2: hertz);

        return SdfUpdateRateTiers.Holds(
            elapsedSeconds: (live.RateSeconds + deltaSeconds),
            frameSeconds: deltaSeconds,
            hertz: hertz,
            rate: m_presentationRate
        );
    }
    // Records a full pack's interval since the previous one under the slowest tier it was rated at between them, and
    // starts the next interval.
    private void RecordPose(Registration live, float deltaSeconds) {
        if (live.RateSlowest < SdfUpdateRateTiers.EveryFrameHertz) {
            m_rateIntervals?.Record(hertz: live.RateSlowest, lever: SdfUpdateRates.Bodies, seconds: (live.RateSeconds + deltaSeconds));
        }

        live.RateSlowest = SdfUpdateRateTiers.EveryFrameHertz;
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

        transforms[rootSlot] = (root with {
            Orientation = rootRotation,
            Position = rootPosition,
        });
    }
}
