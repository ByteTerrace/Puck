using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The suite's one construction of creation documents, canonical prototypes, and the two emission paths a
/// shape law judges: the static stamper (<see cref="CreationStampEmitter"/>) and the animated pool
/// (<see cref="WorldStampPool"/>). A law states only the shape it is about.</summary>
internal static class CreationFixtures {
    // The worst-case probe reads no live registration (WorldStampPool.EmitOne emits every reserved slot with the full
    // modifier envelope from the placement-scale ceiling alone), so one probe at scale one serves every law that
    // compares against it. Building it walks every reserved slot; it is built once per run.
    private static readonly Lazy<SdfProgram> PoolProbeAtUnitScale = new(valueFactory: static () => EmitPool(
        bodyScale: 1f,
        creation: UnitSphere(id: "probe"),
        probeWorstCase: true
    ));

    /// <summary>Gets the animated pool's worst-case capacity probe at a placement-scale ceiling of one, shared and
    /// read-only.</summary>
    public static SdfProgram PoolProbe => PoolProbeAtUnitScale.Value;

    /// <summary>Gets a one-entry grey palette.</summary>
    public static IReadOnlyList<PaletteEntryDocument> Grey { get; } = [new(
        "#AAAAAA",
        null,
        null,
        null
    )];
    /// <summary>Gets a two-entry palette: grey at material 0 and blue at material 1, for a second-material facet.</summary>
    public static IReadOnlyList<PaletteEntryDocument> GreyAndBlue { get; } = [Grey[0], new(
        "#5555FF",
        null,
        null,
        null
    )];
    /// <summary>Gets the one-shape unit sphere a law places when the geometry itself is not what it judges.</summary>
    public static ShapeDocument UnitSphereShape { get; } = Shape(type: SdfSolidPrimitive.Sphere);

    /// <summary>Asserts the canonicalizer admits <paramref name="document"/>.</summary>
    /// <param name="document">The document under test.</param>
    public static void AssertAccepts(CreationDocument document) => Assert.Empty(collection: CreationCanonicalizer.Validate(document: document));
    /// <summary>Asserts the canonicalizer refuses <paramref name="document"/> with a violation whose document path
    /// contains <paramref name="path"/>.</summary>
    /// <param name="document">The document under test.</param>
    /// <param name="path">The ordinal substring of the refused member's path.</param>
    public static void AssertRefusesAt(CreationDocument document, string path) => AssertRefuses(
        document: document,
        matches: violation => violation.Path.Contains(
            comparisonType: StringComparison.Ordinal,
            value: path
        )
    );
    /// <summary>Asserts the canonicalizer refuses <paramref name="document"/> with a violation whose message contains
    /// <paramref name="needle"/>.</summary>
    /// <param name="document">The document under test.</param>
    /// <param name="needle">The ordinal substring the refusal names.</param>
    public static void AssertRefusesNaming(CreationDocument document, string needle) => AssertRefuses(
        document: document,
        matches: violation => violation.Message.Contains(
            comparisonType: StringComparison.Ordinal,
            value: needle
        )
    );
    /// <summary>Builds a current-schema creation document with no frames.</summary>
    /// <param name="name">The document name.</param>
    /// <param name="shapes">The shapes, in authored order.</param>
    /// <param name="palette">The palette, or <see langword="null"/> for none.</param>
    /// <returns>The document.</returns>
    public static CreationDocument Document(string name, IReadOnlyList<ShapeDocument> shapes, IReadOnlyList<PaletteEntryDocument>? palette = null) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: name,
        Palette: palette,
        Shapes: shapes,
        Frames: null
    );
    /// <summary>Emits <paramref name="creation"/> through the animated pool on body 0 at <paramref name="bodyScale"/>,
    /// over the flat code-built document carrying it and one look naming it.</summary>
    /// <param name="creation">The canonical prototype the body wears.</param>
    /// <param name="bodyScale">The body's look scale.</param>
    /// <param name="probeWorstCase">Whether to emit the pool's worst-case capacity probe instead of live state.</param>
    /// <param name="maxPlacementScale">The placement-scale ceiling the pool sizes its reach by, or
    /// <see langword="null"/> for <paramref name="bodyScale"/>.</param>
    /// <returns>The built program, without an instance grid.</returns>
    public static SdfProgram EmitPool(WorldPrototype creation, float bodyScale, bool probeWorstCase = false, float? maxPlacementScale = null) {
        var definition = (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(
                Name: "rig",
                Source: new WorldLookSource.Creation(PrototypeId: creation.Id),
                Scale: bodyScale,
                Motion: WorldLookMotion.Default
            )],
        });
        var pool = new WorldStampPool();

        pool.Reconcile(
            placements: [],
            creations: [creation],
            dynamics: [],
            bodyStamps: [new WorldStampPool.BodyStamp(
                BodyIndex: 0,
                Creation: creation,
                Scale: bodyScale,
                Look: WorldLook.Implicit
            )]
        );

        var builder = new SdfProgramBuilder();

        pool.Emit(
            builder: builder,
            definition: definition,
            probeWorstCase: probeWorstCase,
            maxPlacementScale: (maxPlacementScale ?? bodyScale),
            slotBase: 0
        );

        return builder.Build(buildInstanceGrid: false);
    }
    /// <summary>Emits the shapes through the animated pool as a prototype named <paramref name="name"/>.</summary>
    /// <param name="name">The prototype identifier.</param>
    /// <param name="shapes">The shapes under test, in authored order.</param>
    /// <param name="bodyScale">The body's look scale.</param>
    /// <param name="palette">The palette, or <see langword="null"/> for <see cref="Grey"/>.</param>
    /// <returns>The built program, without an instance grid.</returns>
    public static SdfProgram EmitPool(string name, IReadOnlyList<ShapeDocument> shapes, float bodyScale = 1f, IReadOnlyList<PaletteEntryDocument>? palette = null) => EmitPool(
        bodyScale: bodyScale,
        creation: Prototype(document: Document(
            name: name,
            palette: (palette ?? Grey),
            shapes: shapes
        ))
    );
    /// <summary>Emits <paramref name="document"/> through the static stamper at the origin, unrotated, every
    /// material resolving to one white albedo.</summary>
    /// <param name="document">The creation to stamp.</param>
    /// <param name="stampScale">The uniform stamp scale.</param>
    /// <returns>The built program, without an instance grid.</returns>
    public static SdfProgram EmitStatic(CreationDocument document, float stampScale = 1f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.Emit(
            builder: builder,
            document: document,
            materialFor: _ => material,
            transform: new CreationStampTransform(
                Origin: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: stampScale,
                ReflectionNormal: null
            )
        );

        return builder.Build(buildInstanceGrid: false);
    }
    /// <summary>Every primitive except <paramref name="excluded"/>, as theory rows.</summary>
    /// <param name="excluded">The primitive the law admits.</param>
    /// <returns>The remaining primitives.</returns>
    public static TheoryData<SdfSolidPrimitive> EveryPrimitiveExcept(SdfSolidPrimitive excluded) =>
        new(values: Enum.GetValues<SdfSolidPrimitive>().Where(predicate: type => (type != excluded)));
    /// <summary>Canonicalizes <paramref name="document"/> into the prototype a world row carries, its hash computed
    /// through the same pipeline the validator re-derives, never hand-pinned.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="id">The prototype identifier, or <see langword="null"/> for the document's own name.</param>
    /// <returns>The prototype.</returns>
    public static WorldPrototype Prototype(CreationDocument document, string? id = null) {
        id ??= document.Name!.Value;

        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: id
        );

        return new WorldPrototype(
            Id: id,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    /// <summary>Builds an unrotated shape at the origin with material 0, a plain union, and no group.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The shape scale, or <see langword="null"/> for one.</param>
    /// <returns>The shape.</returns>
    public static ShapeDocument Shape(SdfSolidPrimitive type, Vector3? scale = null) => new(
        Id: 0,
        Name: null,
        Type: type,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: (scale ?? Vector3.One),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );
    /// <summary>Builds the rig a driven-limb law wears: a stride driver on planar travel at cadence one, gated by
    /// <paramref name="token"/>, swinging one limb about Z.</summary>
    /// <param name="token">The driver's one <c>when</c> token.</param>
    /// <returns>The creation document.</returns>
    public static CreationDocument GatedStrideRig(string token) => Rig(
        drivers: [new CreationDriverDocument(
            Name: "stride",
            Signal: CreationDriverDocument.SignalPlanarTravel,
            Cadence: 1f,
            When: [token]
        )],
        SwingingLimb(swing: new ShapeSwingDocument(
            Driver: "stride",
            Pivot: Vector3.Zero,
            Axis: Vector3.UnitZ,
            Amplitude: 1f
        ))
    );
    /// <summary>Builds a creation named <c>rig</c> over <paramref name="shapes"/> driven by <paramref name="drivers"/>.</summary>
    /// <param name="drivers">The creation's drivers.</param>
    /// <param name="shapes">The shapes, in authored order.</param>
    /// <returns>The creation document.</returns>
    public static CreationDocument Rig(IReadOnlyList<CreationDriverDocument> drivers, params ShapeDocument[] shapes) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: "rig",
        Palette: null,
        Shapes: shapes,
        Frames: null,
        Drivers: drivers
    );
    /// <summary>Builds the flat code-built world carrying <paramref name="creation"/> as prototype <c>rig</c> beside
    /// the fixture's own, with any extra state and curve rows.</summary>
    /// <param name="creation">The rig creation, uncanonicalized.</param>
    /// <param name="state">World-scope state rows appended to the fixture's own, or <see langword="null"/>.</param>
    /// <param name="curves">The document's curve rows, or <see langword="null"/>.</param>
    /// <returns>The document.</returns>
    public static WorldDefinition RigWorld(CreationDocument creation, IReadOnlyList<WorldStateRow>? state = null, IReadOnlyList<WorldCurveRow>? curves = null) {
        var basis = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var section = (basis.StateRaw ?? new WorldStateSection());

        return basis with {
            CreationsRaw = [.. basis.Creations, new WorldPrototype(
                Id: new DocumentIdentifier(value: "rig"),
                Document: creation
            )],
            StateRaw = (section with { World = [.. (section.World ?? []), .. (state ?? [])] }),
            CurvesRaw = curves,
        };
    }
    /// <summary>Builds a thin capsule hanging one unit below its pivot, carrying <paramref name="swing"/> — the limb
    /// a rig law swings.</summary>
    /// <param name="swing">The limb's one swing.</param>
    /// <returns>The shape.</returns>
    public static ShapeDocument SwingingLimb(ShapeSwingDocument swing) => new(
        Id: 0,
        Name: "limb",
        Type: SdfSolidPrimitive.Capsule,
        Position: new Vector3(
            x: 0f,
            y: -1f,
            z: 0f
        ),
        Rotation: Quaternion.Identity,
        Scale: new Vector3(
            x: 0.05f,
            y: 1f,
            z: 0.05f
        ),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Swings: [swing]
    );
    /// <summary>The one-sphere prototype named <paramref name="id"/>, of uniform scale <paramref name="scale"/>.</summary>
    /// <param name="id">The prototype identifier and document name.</param>
    /// <param name="scale">The sphere's uniform shape scale, in creation units.</param>
    /// <returns>The prototype.</returns>
    public static WorldPrototype Sphere(string id, float scale) => Prototype(document: Document(
        name: id,
        shapes: [Shape(
            scale: new Vector3(value: scale),
            type: SdfSolidPrimitive.Sphere
        )]
    ));
    /// <summary>The unit-sphere prototype named <paramref name="id"/>.</summary>
    /// <param name="id">The prototype identifier and document name.</param>
    /// <returns>The prototype.</returns>
    public static WorldPrototype UnitSphere(string id) => Sphere(
        id: id,
        scale: 1f
    );

    private static void AssertRefuses(CreationDocument document, Predicate<DocumentValidationError> matches) {
        var violations = CreationCanonicalizer.Validate(document: document);

        Assert.NotEmpty(collection: violations);
        Assert.Contains(
            collection: violations,
            filter: matches
        );
    }
}
