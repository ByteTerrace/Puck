using System.Numerics;
using Puck.Assets.Documents;
using System.Text.Json.Serialization;
using Puck.World.Authoring;
using Puck.Abstractions.Documents;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>A row's solidity facet — it participates in contact resolution using its own declared shape. Presence is
/// the whole switch; <see langword="null"/> means decoration — the row is drawn but bodies pass through it.</summary>
/// <param name="Margin">The signed skin added to the shape for contact purposes. Positive fattens the collider past the
/// drawn surface; negative lets a body sink in. Compensates the smooth-union blend.</param>
public sealed record WorldSolid(float Margin);
/// <summary>A kit's closed body-volume vocabulary. A kit with no collider is not solved against the contact field.</summary>
[JsonDerivedType(typeof(WorldCollider.Sphere), typeDiscriminator: "sphere")]
[JsonDerivedType(typeof(WorldCollider.Capsule), typeDiscriminator: "capsule")]
[JsonDerivedType(typeof(WorldCollider.Box), typeDiscriminator: "box")]
[JsonDerivedType(typeof(WorldCollider.FromCreation), typeDiscriminator: "fromCreation")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldCollider {
    /// <summary>The largest number of convex volumes one body collider may compile into. This bounds the field
    /// provider's per-body sample cost, which scales linearly with the volume count.</summary>
    public const int MaxVolumes = 16;

    private WorldCollider() {
    }

    /// <summary>A sphere resting on the body root.</summary>
    /// <param name="Radius">The sphere radius.</param>
    public sealed record Sphere(float Radius) : WorldCollider;
    /// <summary>A capsule whose lower sphere rests on the body root.</summary>
    /// <param name="Endpoint">The body-local vector from the lower sphere center to the upper sphere center.</param>
    /// <param name="Radius">The capsule radius.</param>
    public sealed record Capsule(DocumentVector3 Endpoint, float Radius) : WorldCollider;
    /// <summary>An oriented box resting on the body root before its local rotation is applied.</summary>
    /// <param name="HalfExtents">The positive half-extents.</param>
    /// <param name="Rotation">The body-local orientation.</param>
    public sealed record Box(DocumentVector3 HalfExtents, DocumentQuaternion Rotation) : WorldCollider;
    /// <summary>The finite primitive bounds emitted by a creation, composed into one compound body collider.</summary>
    /// <param name="PrototypeId">The referenced <see cref="WorldPrototype.Id"/>.</param>
    public sealed record FromCreation(string PrototypeId) : WorldCollider;
}
/// <summary>The contact solver's world-scale tuning.</summary>
/// <param name="Requirements">The contact qualities the world requires. An empty list permits analytic primitive
/// contact; any declared requirement selects the SDF field.</param>
/// <param name="ContactSkin">The signed skin the solver keeps between a body and every surface (world units).</param>
/// <param name="MaxIterations">The relaxation iteration count per tick (above 8 is a solver pathology, not a choice).</param>
/// <param name="MaxSlopeDegrees">The steepest surface a body still counts as standing on. A contact whose normal leans
/// further from the body's up axis than this pushes the body but never grounds it — the walkable-slope limit.</param>
/// <param name="GradientProbe">The finite-difference step field contact samples the surface normal with, in world
/// units; 0 takes the evaluator's own default. Meaningful only when a requirement selects field contact.</param>
/// <param name="DefaultHold">Whether a body's surface hold may take any solid surface by default. A placement's own
/// <see cref="WorldPlacementGrip"/> overrides this for the colliders it compiles; the field lattice's own terrain,
/// which no placement row owns, has only this. <see langword="false"/> (the default) holds nothing.</param>
/// <param name="EventsRaw">The bounded body-overlap event policy. ABSENT takes
/// <see cref="WorldCollisionEvents.Default"/>; author <c>maxPairsPerBody: 0</c> to disable body-pair events while
/// retaining ordinary world contact.</param>
/// <param name="BodyContactsRaw">The bounded dynamic-body depenetration policy. ABSENT takes
/// <see cref="WorldBodyContactPolicy.Default"/>. This is independent of overlap events.</param>
public sealed record WorldCollision(IReadOnlyList<WorldContactRequirement> Requirements, float ContactSkin,
    int MaxIterations, float MaxSlopeDegrees, float GradientProbe, bool DefaultHold = false,
    [property: JsonPropertyName("events"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCollisionEvents? EventsRaw = null,
    [property: JsonPropertyName("bodyContacts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBodyContactPolicy? BodyContactsRaw = null) {
    /// <summary>Gets the effective bounded body-overlap event policy.</summary>
    [JsonIgnore]
    public WorldCollisionEvents Events => (EventsRaw ?? WorldCollisionEvents.Default);
    /// <summary>Gets the effective bounded dynamic-body contact policy.</summary>
    [JsonIgnore]
    public WorldBodyContactPolicy BodyContacts => (BodyContactsRaw ?? WorldBodyContactPolicy.Default);
    /// <summary>Gets the inert absence — no requirements, zero skin, zero iterations, a solver that never relaxes.
    /// The engine holds no contact tuning of its own: the standard tuning is AUTHORED, in
    /// <c>Assets/worlds/standard.world.json</c>, and a world inherits it by naming that document as its basis. A
    /// document whose census implies a body is refused for authoring no <c>collision</c>, so only a bodyless world
    /// ever reads this.</summary>
    public static WorldCollision Absent { get; } = new(
        ContactSkin: 0f,
        DefaultHold: false,
        EventsRaw: null,
        GradientProbe: 0f,
        MaxIterations: 0,
        MaxSlopeDegrees: 0f,
        Requirements: []
    );
}
/// <summary>Bounds body-pair overlap sensing independently of physical contact. The event feed retains established
/// overlaps first, then considers at most <see cref="CandidateBudget"/> broadphase candidates per body, starts at
/// most <see cref="BeginBudget"/> relationships per tick, and admits at most <see cref="MaxPairsPerBody"/>
/// simultaneous pairs incident to any body. All choices are deterministic; a
/// saturated crowd therefore degrades by omitting lower-priority new pairs rather than by missing its frame budget.</summary>
/// <param name="CandidateBudget">The most sweep-and-prune candidates inspected for one body while discovering new
/// overlaps. Must be at least <paramref name="MaxPairsPerBody"/>.</param>
/// <param name="MaxPairsPerBody">The maximum retained overlap-event degree of one body; zero disables collision
/// begin/end sensing.</param>
/// <param name="BeginBudget">The maximum collision-begin relationships admitted in one authority tick. Existing
/// relationships and their ends are not delayed.</param>
public sealed record WorldCollisionEvents(int CandidateBudget = 32, int MaxPairsPerBody = 8, int BeginBudget = 1024) {
    /// <summary>The largest accepted per-body candidate budget.</summary>
    public const int MaximumCandidateBudget = 256;
    /// <summary>The largest accepted retained overlap degree per body.</summary>
    public const int MaximumPairsPerBody = 64;
    /// <summary>The largest accepted per-tick begin budget.</summary>
    public const int MaximumBeginBudget = 8192;
    /// <summary>The policy used when <c>collision.events</c> is absent.</summary>
    public static WorldCollisionEvents Default { get; } = new();
}
/// <summary>Bounds physical depenetration between kits that both author <see cref="WorldBodyContactMode.Solid"/>.
/// The sweep inspects at most <see cref="CandidateBudget"/> x-overlapping candidates for each solid body and resolves
/// at most <see cref="MaxPairsPerBody"/> contacts incident to one body in a tick. Choices are stable population order;
/// a saturated crowd omits lower-priority pairs instead of turning one authority tick into quadratic work.</summary>
/// <param name="CandidateBudget">The most sweep candidates inspected for one solid body. Must be at least
/// <paramref name="MaxPairsPerBody"/>.</param>
/// <param name="MaxPairsPerBody">The most physical pair corrections incident to one body in a tick.</param>
/// <param name="RigidSubstepCeiling">The most substeps one rigid body's static-contact integration may take in a
/// tick — the derived-count's own ceiling: the actual count is derived per body per tick from its speed and collider
/// size (a fast ball takes more, a resting one takes one), never authored directly, but the ceiling bounds the
/// worst-case per-tick cost and is echoed in <c>world.budget</c>.</param>
public sealed record WorldBodyContactPolicy(int CandidateBudget = 16, int MaxPairsPerBody = 8, int RigidSubstepCeiling = 8) {
    /// <summary>The largest accepted candidate budget per solid body.</summary>
    public const int MaximumCandidateBudget = 32;
    /// <summary>The largest accepted resolved-contact degree per solid body.</summary>
    public const int MaximumPairsPerBody = 16;
    /// <summary>The largest accepted rigid-body substep ceiling.</summary>
    public const int MaximumRigidSubstepCeiling = 32;
    /// <summary>The policy used when <c>collision.bodyContacts</c> is absent.</summary>
    public static WorldBodyContactPolicy Default { get; } = new();
}
/// <summary>A contact quality authored by the world, independent of the engine implementation that supplies it.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldContactRequirement>))]
public enum WorldContactRequirement : byte {
    /// <summary>Blended creation surfaces remain solid across their smooth-union seams.</summary>
    SmoothUnionContact,

    /// <summary>A body's up direction follows the contacted field gradient rather than world <c>+Y</c>.</summary>
    GradientDerivedUp,
}
/// <summary>Selects the contact implementation implied by authored requirements.</summary>
public static class WorldContactSelection {
    /// <summary>Returns whether any authored requirement needs the SDF contact field.</summary>
    /// <param name="collision">The authored contact requirements and solver tuning.</param>
    /// <returns><see langword="true"/> when field contact is required; otherwise <see langword="false"/>.</returns>
    public static bool RequiresField(WorldCollision collision) => (collision.Requirements is { Count: > 0 });
}
/// <summary>The one-time fixed-point compilation of a kit's compound body volume.</summary>
public readonly record struct FixedWorldCollider(FixedBodyColliderVolume[] Volumes) {
    private static FixedBodyColliderVolume Box(FixedVector3 center, FixedVector3 halfExtents, FixedQuaternion rotation) =>
        new(
            Kind: FixedBodyColliderKind.Box,
            Center: center,
            Endpoint: FixedVector3.Zero,
            HalfExtents: halfExtents,
            Rotation: rotation,
            Radius: FixedQ4816.Zero
        );
    private static FixedBodyColliderVolume Capsule(FixedVector3 lower, FixedVector3 upper, FixedQ4816 radius) =>
        new(
            Kind: FixedBodyColliderKind.Capsule,
            Center: lower,
            Endpoint: upper,
            HalfExtents: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity,
            Radius: radius
        );
    private static FixedBodyColliderVolume Sphere(FixedVector3 center, FixedQ4816 radius) =>
        new(
            Kind: FixedBodyColliderKind.Sphere,
            Center: center,
            Endpoint: FixedVector3.Zero,
            HalfExtents: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity,
            Radius: radius
        );
    private static FixedQuaternion ToFixed(Quaternion value) => new FixedQuaternion(
        X: FixedQ4816.FromDouble(value: value.X),
        Y: FixedQ4816.FromDouble(value: value.Y),
        Z: FixedQ4816.FromDouble(value: value.Z),
        W: FixedQ4816.FromDouble(value: value.W)
    ).Normalize();

    /// <summary>Compiles authored collider floats and creation primitive copies to fixed point.</summary>
    public static FixedWorldCollider? Compile(WorldCollider? collider, IReadOnlyList<WorldPrototype> creations) {
        if (collider is null) {
            return null;
        }

        var volumes = new List<FixedBodyColliderVolume>(capacity: WorldCollider.MaxVolumes);

        switch (collider) {
            case WorldCollider.Sphere sphere: {
                    var radius = FixedQ4816.FromDouble(value: sphere.Radius);

                    volumes.Add(item: Sphere(
                        center: new FixedVector3(
                            X: FixedQ4816.Zero,
                            Y: radius,
                            Z: FixedQ4816.Zero
                        ),
                        radius: radius
                    ));
                    break;
                }
            case WorldCollider.Capsule capsule: {
                    var radius = FixedQ4816.FromDouble(value: capsule.Radius);
                    var lower = new FixedVector3(
                        X: FixedQ4816.Zero,
                        Y: radius,
                        Z: FixedQ4816.Zero
                    );

                    volumes.Add(item: Capsule(
                        lower: lower,
                        upper: (lower + FixedVector3.FromVector3(value: capsule.Endpoint)),
                        radius: radius
                    ));
                    break;
                }
            case WorldCollider.Box box: {
                    var halfExtents = FixedVector3.FromVector3(value: box.HalfExtents);

                    volumes.Add(item: Box(
                        center: new FixedVector3(
                            X: FixedQ4816.Zero,
                            Y: halfExtents.Y,
                            Z: FixedQ4816.Zero
                        ),
                        halfExtents: halfExtents,
                        rotation: ToFixed(value: box.Rotation)
                    ));
                    break;
                }
            case WorldCollider.FromCreation fromCreation: {
                    var creation = (WorldDefinitionRows.FindCreation(
                        creations: creations,
                        id: fromCreation.PrototypeId
                    )
                        ?? throw new InvalidOperationException(message: $"Body collider creation '{fromCreation.PrototypeId}' is not defined."));

                    // The fixed-point enumeration, not a single-precision one: every value below lands in a collider
                    // volume, and a body collider decides where a body stops.
                    CreationStampEmitter.VisitFixedPrimitiveCopies(
                        document: creation.EngineDocument,
                        transform: new FixedCreationStampTransform(
                            Origin: FixedVector3.Zero,
                            Rotation: FixedQuaternion.Identity,
                            Scale: FixedQ4816.One,
                            ReflectionNormal: null
                        ),
                        visitor: copy => {
                            if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
                                throw new InvalidOperationException(message: $"Body collider creation '{fromCreation.PrototypeId}' contains an unbounded plane.");
                            }

                            if (
                                (copy.Shape.Type == SdfSolidPrimitive.Sphere) &&
                                (copy.UniformScale > FixedQ4816.Zero)
                            ) {
                                var sphere = SdfSolidGeometry.GetLocalBounds(type: SdfSolidPrimitive.Sphere);

                                volumes.Add(item: Sphere(
                                    center: copy.Center,
                                    radius: (FixedQ4816.FromDouble(value: sphere.HalfExtents.X) * copy.UniformScale)
                                ));
                            } else {
                                volumes.Add(item: Box(
                                    center: copy.Center,
                                    halfExtents: copy.HalfExtents,
                                    rotation: FixedQuaternion.Identity
                                ));
                            }
                        }
                    );
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(collider),
                    actualValue: collider,
                    message: "The body collider kind is not defined."
                );
        }

        return new FixedWorldCollider(Volumes: volumes.ToArray());
    }
}
/// <summary>The one-time fixed-point compilation of the world's contact tuning — read by the analytic contact field
/// and the grounded integrator. <see cref="GroundedThreshold"/> is the compiled <c>cos(maxSlopeDegrees)</c> a contact
/// normal's up-alignment must clear to ground a body (the same test both providers use). <see cref="GradientUp"/> is
/// the compiled <see cref="WorldContactRequirement.GradientDerivedUp"/> requirement: it lets field gradients and
/// measured support normals supply surface-relative up; without it, the caller's ambient up owns the walkable
/// contact test.</summary>
public readonly record struct FixedWorldCollision(
    FixedQ4816 ContactSkin,
    int MaxIterations,
    FixedQ4816 GroundedThreshold,
    FixedQ4816 GradientProbe,
    bool GradientUp,
    bool DefaultHold
) {
    /// <summary>Compiles the authored contact tuning to fixed point.</summary>
    public static FixedWorldCollision Compile(WorldCollision collision) => new(
        ContactSkin: FixedQ4816.FromDouble(value: collision.ContactSkin),
        MaxIterations: collision.MaxIterations,
        GroundedThreshold: FixedQ4816.Cos(angle: FixedQ4816.FromDouble(value: (collision.MaxSlopeDegrees * (Math.PI / 180.0)))),
        GradientProbe: FixedQ4816.FromDouble(value: collision.GradientProbe),
        GradientUp: ((collision.Requirements?.Contains(value: WorldContactRequirement.GradientDerivedUp)) ?? false),
        DefaultHold: collision.DefaultHold
    );
}
