using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>Laws for <see cref="FixedRigidWorld"/> fed by <see cref="FixedRigidContacts"/>: a box resting flat holds four
/// manifold slots whose feature ids stay put from step to step, a stack stands, friction stops a slide, and the solve
/// is independent of the order the candidates were generated in.</summary>
public sealed class FixedRigidWorldLawTests {
    private const int FloorId = -1;
    private static readonly FixedQ4816 Half = FixedQ4816.FromDouble(value: 0.1d);
    private static readonly FixedVector3 Up = FixedVector3.UnitY;
    private static readonly FixedVector3 Gravity = new(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: -9.81d), Z: FixedQ4816.Zero);

    // A box world over a floor at y = 0: absolute poses here, solver views in the world, one contact query a pair a step.
    private sealed class BoxWorld {
        private readonly FixedRigidWorld m_world = new(options: new FixedRigidSolverOptions { RateHz = 60, SubstepCount = 4 });
        private readonly FixedRigidBody m_floor = new();

        internal readonly List<FixedVector3> Positions = [];
        internal readonly List<FixedQuaternion> Orientations = [];
        internal readonly List<FixedRigidBody> Bodies = [];
        internal readonly List<FixedRigidManifold> FloorManifolds = [];
        internal readonly Dictionary<(int, int), FixedRigidManifold> PairManifolds = [];
        internal FixedQ4816 Friction = FixedQ4816.FromDouble(value: 0.6d);

        internal static FixedBodyColliderVolume Volume => new(
            Kind: FixedBodyColliderKind.Box,
            Center: FixedVector3.Zero,
            Endpoint: FixedVector3.Zero,
            HalfExtents: new FixedVector3(X: Half, Y: Half, Z: Half),
            Rotation: FixedQuaternion.Identity,
            Radius: FixedQ4816.Zero
        );

        internal int RefusalCount => m_world.RefusalCount;

        internal int Add(FixedVector3 position) {
            // A uniform 1 kg cube of half extent h: I = m (2h)^2 / 6 about each axis.
            var inertia = ((4d * 0.1d * 0.1d) / 6d);

            Positions.Add(item: position);
            Orientations.Add(item: FixedQuaternion.Identity);
            Bodies.Add(item: new FixedRigidBody {
                Acceleration = Gravity,
                InverseMassRaw = Raw(value: 1d, bits: 40),
                InverseInertiaXX = Raw(value: (1d / inertia), bits: 40),
                InverseInertiaYY = Raw(value: (1d / inertia), bits: 40),
                InverseInertiaZZ = Raw(value: (1d / inertia), bits: 40),
            });
            FloorManifolds.Add(item: new FixedRigidManifold());

            return (Bodies.Count - 1);
        }
        internal FixedRigidContactShape Shape(int id) => new(
            CenterOffset: FixedVector3.Zero,
            Id: id,
            Orientation: Orientations[id],
            Root: Positions[id],
            Volume: Volume
        );
        // One step: one floor query and one overlap query a pair, the candidates handed over in the order `order` names.
        internal void Step(Func<List<FixedTwoBodyContact>, List<FixedTwoBodyContact>>? order = null) {
            var active = new List<FixedRigidManifold>();
            var candidates = new List<FixedTwoBodyContact>();
            Span<FixedVector3> anchors = stackalloc FixedVector3[4];
            Span<int> features = stackalloc int[4];

            for (var id = 0; (id < Bodies.Count); id++) {
                Bodies[id].Orientation = Orientations[id];
                candidates.Clear();

                var count = FixedRigidWitness.SupportManifold(anchors: anchors, centerOffset: FixedVector3.Zero, features: features,
                    normal: Up, orientation: Orientations[id], volume: Volume);
                var lowest = FixedQ4816.MaxValue;

                for (var point = 0; (point < count); point++) {
                    lowest = FixedQ4816.Min(x: lowest, y: (Positions[id].Y + anchors[point].Y));
                }

                if (lowest <= FixedQ4816.FromDouble(value: 0.02d)) {
                    _ = FixedRigidContacts.AppendSurface(body: Shape(id: id), candidates: candidates, normal: Up, separation: lowest,
                        sourceId: 0, surfaceId: FloorId);
                }

                var manifold = FloorManifolds[id];

                manifold.BodyA = m_floor;
                manifold.BodyB = Bodies[id];
                manifold.Friction = Friction;
                manifold.Associate(candidates: (order?.Invoke(arg: candidates) ?? candidates));

                if (manifold.Slots.ActiveCount > 0) {
                    active.Add(item: manifold);
                }
            }

            for (var lower = 0; (lower < Bodies.Count); lower++) {
                for (var upper = (lower + 1); (upper < Bodies.Count); upper++) {
                    if (!FixedDynamicBodyContacts.TryCorrection(leftPosition: Positions[upper], leftOrientation: Orientations[upper],
                        leftVolumes: [Volume], rightPosition: Positions[lower], rightOrientation: Orientations[lower],
                        rightVolumes: [Volume], tieBreaker: (lower ^ upper), correction: out var correction)) {
                        continue;
                    }

                    candidates.Clear();
                    _ = FixedRigidContacts.AppendPair(a: Shape(id: lower), b: Shape(id: upper), candidates: candidates,
                        clipMargin: FixedQ4816.FromDouble(value: 0.005d), depth: correction.Length, normal: correction.Normalize(), sourceId: 0);

                    if (!PairManifolds.TryGetValue(key: (lower, upper), value: out var manifold)) {
                        PairManifolds[(lower, upper)] = manifold = new FixedRigidManifold();
                    }

                    manifold.BodyA = Bodies[lower];
                    manifold.BodyB = Bodies[upper];
                    manifold.Friction = Friction;
                    manifold.Associate(candidates: (order?.Invoke(arg: candidates) ?? candidates));
                    active.Add(item: manifold);
                }
            }

            m_world.Step(bodies: Bodies.ToArray(), manifolds: active.ToArray());

            for (var id = 0; (id < Bodies.Count); id++) {
                Positions[id] += Bodies[id].DeltaPosition;
                Orientations[id] = (Bodies[id].DeltaRotation * Orientations[id]).Normalize();
            }
        }
    }

    private static long Raw(double value, int bits) => ((long)Math.Round(a: (value * Math.Pow(x: 2d, y: bits))));
    private static FixedVector3 At(double x, double y, double z) => new(X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.FromDouble(value: y), Z: FixedQ4816.FromDouble(value: z));

    [Fact]
    public void ABoxRestingFlatHoldsFourSlotsWithStableFeatureIds() {
        var world = new BoxWorld();
        var box = world.Add(position: At(x: 0d, y: 0.1d, z: 0d));
        int[]? first = null;

        for (var step = 0; (step < 120); step++) {
            world.Step();

            var slots = world.FloorManifolds[box].Slots;
            var features = new int[FixedPairManifoldSlotTable.Capacity];

            for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); index++) {
                features[index] = (slots[index].Occupied ? slots[index].FeatureId : -1);
            }

            Assert.Equal(expected: 4, actual: slots.ActiveCount);
            first ??= features;
            Assert.Equal(expected: first, actual: features);
        }

        // The four bottom corners of a cube resting on its -Y face: the corner ids with the Y bit clear.
        Assert.Equal(expected: [0, 1, 4, 5], actual: first!.Where(predicate: static feature => (feature >= 0)).Order());
        Assert.Equal(expected: 0, actual: world.RefusalCount);
        Assert.True(condition: (FixedQ4816.Abs(value: (world.Positions[box].Y - Half)) < FixedQ4816.FromDouble(value: 0.005d)),
            userMessage: $"the box rests at y={(double)world.Positions[box].Y}");
        Assert.True(condition: (world.Bodies[box].LinearVelocity.Length < FixedQ4816.FromDouble(value: 0.01d)));
    }
    [Fact]
    public void AStackOfThreeBoxesStandsForSixHundredSteps() {
        var world = new BoxWorld();

        for (var level = 0; (level < 3); level++) {
            _ = world.Add(position: At(x: (0.01d * level), y: (0.1d + (0.2d * level)), z: 0d));
        }

        for (var step = 0; (step < 600); step++) {
            world.Step();
        }

        Assert.Equal(expected: 0, actual: world.RefusalCount);

        for (var level = 0; (level < 3); level++) {
            var position = world.Positions[level];

            Assert.True(condition: (FixedQ4816.Abs(value: (position.Y - FixedQ4816.FromDouble(value: (0.1d + (0.2d * level))))) < FixedQ4816.FromDouble(value: 0.01d)),
                userMessage: $"box {level} stands at y={(double)position.Y}");
            Assert.True(condition: (FixedQ4816.Abs(value: (position.X - FixedQ4816.FromDouble(value: (0.01d * level)))) < FixedQ4816.FromDouble(value: 0.01d)),
                userMessage: $"box {level} drifted to x={(double)position.X}");
            Assert.True(condition: (world.Orientations[level].W > FixedQ4816.FromDouble(value: 0.999d)), userMessage: $"box {level} tipped");
        }
    }
    [Fact]
    public void FrictionStopsASlideAndControlWithoutFrictionKeepsSliding() {
        static FixedQ4816 SlideFor(FixedQ4816 friction) {
            var world = new BoxWorld { Friction = friction };
            var box = world.Add(position: At(x: 0d, y: 0.1d, z: 0d));

            world.Bodies[box].LinearVelocity = At(x: 2d, y: 0d, z: 0d);

            for (var step = 0; (step < 120); step++) {
                world.Step();
            }

            return world.Bodies[box].LinearVelocity.X;
        }

        Assert.True(condition: (SlideFor(friction: FixedQ4816.FromDouble(value: 0.6d)) < FixedQ4816.FromDouble(value: 0.01d)));
        Assert.True(condition: (SlideFor(friction: FixedQ4816.Zero) > FixedQ4816.FromDouble(value: 1.9d)));
    }
    [Fact]
    public void TheSolveIsIndependentOfCandidateOrder() {
        static (FixedVector3[] Positions, FixedQuaternion[] Orientations) Run(bool reverse) {
            var world = new BoxWorld();

            _ = world.Add(position: At(x: 0d, y: 0.1d, z: 0d));
            _ = world.Add(position: At(x: 0.03d, y: 0.29d, z: -0.02d));
            world.Bodies[1].AngularVelocity = At(x: 0.5d, y: 0.2d, z: -0.4d);

            for (var step = 0; (step < 90); step++) {
                world.Step(order: reverse ? (static candidates => { candidates.Reverse(); return candidates; }) : null);
            }

            return ([.. world.Positions], [.. world.Orientations]);
        }

        var forward = Run(reverse: false);
        var backward = Run(reverse: true);

        Assert.Equal(expected: forward.Positions, actual: backward.Positions);
        Assert.Equal(expected: forward.Orientations, actual: backward.Orientations);
    }
}
