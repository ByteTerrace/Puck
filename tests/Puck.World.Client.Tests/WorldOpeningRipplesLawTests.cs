using System.Numerics;

using Puck.SdfVm;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// CONTRACT UNDER TEST: an opening that ripples remembers the contacts bodies make with its glass. A body that comes within
/// reach of the glass inside its outline starts rings at the point it touched, once however long it stays; a body that
/// passes through starts stronger ones; a body beside the glass, behind it out of reach or outside its outline starts
/// none; a contact lasts the ripple lifetime and then shows as an empty slot; the four slots hold the newest contacts; and
/// the clock wraps at the ripple period.
/// </summary>
public sealed class WorldOpeningRipplesLawTests {
    private const int Capacity = 4;

    // A 2 x 3 m glass facing +Z: its right is +X and its up +Y, the unit axes a screen row stores.
    private static readonly Vector3 Right = Vector3.UnitX;
    private static readonly Vector3 Up = Vector3.UnitY;

    private sealed class Bodies : ISdfAnchorSource {
        public Dictionary<int, Vector3> Positions { get; } = [];

        public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
            anchor = (Positions.TryGetValue(
                key: anchorId,
                value: out var position
            )
                ? new SdfAnchor(Position: position, Orientation: Quaternion.Identity)
                : default
            );

            return Positions.ContainsKey(key: anchorId);
        }
    }

    private static SdfScreenRipple Advance(WorldOpeningRipples ripples, Bodies bodies, double seconds, Vector3? origin = null) => ripples.Advance(
        bodies: bodies,
        capacity: Capacity,
        halfHeight: 1.5f,
        halfWidth: 1f,
        origin: (origin ?? new Vector3(x: 0f, y: 1.5f, z: 0f)),
        right: Right,
        screen: 7,
        seconds: seconds,
        up: Up
    );
    private static int Showing(SdfScreenRipple ripple) => new[] { ripple.Contact0, ripple.Contact1, ripple.Contact2, ripple.Contact3 }.Count(predicate: static contact => (contact.W > 0f));

    [Fact]
    public void ABodyThatComesWithinReachStartsRingsOnceAtThePointItTouched() {
        var ripples = new WorldOpeningRipples();
        var bodies = new Bodies();

        bodies.Positions[0] = new Vector3(x: 0.5f, y: 0.5f, z: 2f);
        Assert.Equal(actual: Showing(ripple: Advance(ripples: ripples, bodies: bodies, seconds: 1.0)), expected: 0);

        // The body's middle (a metre over its origin) is 0.2 m from the glass, right of centre and half a metre low.
        bodies.Positions[0] = new Vector3(x: 0.5f, y: 0.5f, z: 0.2f);

        var touched = Advance(ripples: ripples, bodies: bodies, seconds: 1.1);

        Assert.Equal(actual: Showing(ripple: touched), expected: 1);
        Assert.Equal(
            actual: touched.Contact0,
            expected: SdfScreenRipple.Contact(age: 0f, strength: WorldOpeningRipples.TouchStrength, u: 0.75f, v: 0.5f)
        );

        // Standing there starts nothing more; the first contact ages.
        var later = Advance(ripples: ripples, bodies: bodies, seconds: 1.6);

        Assert.Equal(actual: Showing(ripple: later), expected: 1);
        Assert.Equal(actual: later.Contact0.Z, expected: 0.5f, precision: 4);
    }
    [Fact]
    public void ABodyThatPassesThroughStartsStrongerRings() {
        var ripples = new WorldOpeningRipples();
        var bodies = new Bodies();

        bodies.Positions[0] = new Vector3(x: 0f, y: 0.5f, z: 0.3f);
        _ = Advance(ripples: ripples, bodies: bodies, seconds: 1.0);
        bodies.Positions[0] = new Vector3(x: 0f, y: 0.5f, z: -0.3f);

        var crossed = Advance(ripples: ripples, bodies: bodies, seconds: 1.1);

        Assert.Equal(actual: Showing(ripple: crossed), expected: 2);
        Assert.Contains(
            collection: new[] { crossed.Contact0, crossed.Contact1, crossed.Contact2, crossed.Contact3 },
            filter: static contact => (contact.W == WorldOpeningRipples.CrossStrength)
        );
    }
    [Fact]
    public void ABodyOutOfReachBehindOrBesideTheGlassStartsNone() {
        var ripples = new WorldOpeningRipples();
        var bodies = new Bodies();

        bodies.Positions[0] = new Vector3(x: 0f, y: 0.5f, z: 1.5f);
        bodies.Positions[1] = new Vector3(x: 3f, y: 0.5f, z: 0.2f);
        bodies.Positions[2] = new Vector3(x: 0f, y: 5f, z: 0.2f);
        _ = Advance(ripples: ripples, bodies: bodies, seconds: 1.0);
        bodies.Positions[0] = new Vector3(x: 0f, y: 0.5f, z: 1.4f);

        Assert.Equal(actual: Showing(ripple: Advance(ripples: ripples, bodies: bodies, seconds: 1.1)), expected: 0);

        // A body that leaves and comes back touches again.
        bodies.Positions.Remove(key: 1);
        bodies.Positions[0] = new Vector3(x: 0f, y: 0.5f, z: 0.2f);
        Assert.Equal(actual: Showing(ripple: Advance(ripples: ripples, bodies: bodies, seconds: 1.2)), expected: 1);
        bodies.Positions.Remove(key: 0);
        _ = Advance(ripples: ripples, bodies: bodies, seconds: 1.3);
        bodies.Positions[0] = new Vector3(x: 0f, y: 0.5f, z: 0.2f);
        Assert.Equal(actual: Showing(ripple: Advance(ripples: ripples, bodies: bodies, seconds: 1.4)), expected: 2);
    }
    [Fact]
    public void AContactLastsTheLifetimeAndTheFourNewestSlotsAreKept() {
        var ripples = new WorldOpeningRipples();
        var bodies = new Bodies();

        bodies.Positions[0] = new Vector3(x: 0f, y: 0.5f, z: 0.2f);
        _ = Advance(ripples: ripples, bodies: bodies, seconds: 1.0);

        Assert.Equal(actual: Showing(ripple: Advance(ripples: ripples, bodies: bodies, seconds: (1.0 + SdfScreenRipple.Lifetime - 0.05))), expected: 1);
        Assert.Equal(actual: Showing(ripple: Advance(ripples: ripples, bodies: bodies, seconds: (1.0 + SdfScreenRipple.Lifetime + 0.05))), expected: 0);

        // Five touches in turn leave the newest four.
        var time = 10.0;

        for (var touch = 0; (touch < 5); touch++) {
            bodies.Positions[0] = new Vector3(x: 0f, y: 0.5f, z: 3f);
            time += 0.01;
            _ = Advance(ripples: ripples, bodies: bodies, seconds: time);
            bodies.Positions[0] = new Vector3(x: (0.1f * touch), y: 0.5f, z: 0.2f);
            time += 0.01;
            _ = Advance(ripples: ripples, bodies: bodies, seconds: time);
        }

        var ripple = Advance(ripples: ripples, bodies: bodies, seconds: (time + 0.01));

        Assert.Equal(actual: Showing(ripple: ripple), expected: 4);
        Assert.DoesNotContain(
            collection: new[] { ripple.Contact0, ripple.Contact1, ripple.Contact2, ripple.Contact3 },
            filter: static contact => (contact.X == 0.5f)
        );
    }
    [Fact]
    public void ANonAdvancingClockMakesNoContactAndTheClockWraps() {
        var ripples = new WorldOpeningRipples();
        var bodies = new Bodies();

        bodies.Positions[0] = new Vector3(x: 0f, y: 0.5f, z: 0.2f);

        var first = Advance(ripples: ripples, bodies: bodies, seconds: 2.0);
        var again = Advance(ripples: ripples, bodies: bodies, seconds: 2.0);

        Assert.Equal(actual: again, expected: first);
        Assert.Equal(
            actual: Advance(ripples: ripples, bodies: bodies, seconds: (SdfScreenRipple.ClockPeriod + 3.0)).Clock,
            expected: 3f,
            precision: 3
        );
        Assert.Throws<ArgumentNullException>(testCode: () => ripples.Advance(
            bodies: null!,
            capacity: Capacity,
            halfHeight: 1f,
            halfWidth: 1f,
            origin: Vector3.Zero,
            right: Right,
            screen: 7,
            seconds: 1.0,
            up: Up
        ));
    }
    [Fact]
    public void AFloorGlassIsTouchedAtABodysFeet() {
        var ripples = new WorldOpeningRipples();
        var bodies = new Bodies();

        // A glass facing up: its right is +X and its up -Z.
        bodies.Positions[0] = new Vector3(x: 0.4f, y: 0.2f, z: 0f);

        var ripple = ripples.Advance(
            bodies: bodies,
            capacity: Capacity,
            halfHeight: 1f,
            halfWidth: 1f,
            origin: Vector3.Zero,
            right: Vector3.UnitX,
            screen: 9,
            seconds: 1.0,
            up: -Vector3.UnitZ
        );

        Assert.Equal(actual: Showing(ripple: ripple), expected: 1);
    }
}
