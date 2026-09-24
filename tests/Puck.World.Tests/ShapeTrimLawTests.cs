using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="ShapeTrimDocument"/> — a shape's second-material surface band against another,
/// earlier-declared shape — is refused by name wherever its own field scope has nowhere to nest (a domain-folded
/// shape, a grouped shape, a creation that already needs a scope of its own), where the reference names no earlier
/// shape, and against a non-positive width or a negative/non-finite inset; otherwise it emits, on both paths, a
/// scope of its own per trim: the host's own copy eroded by an isolated Dilate, then the reference's own copy —
/// its own pose, scale grown by width — composed by Intersection, both carrying the trim's own material.
/// </summary>
public sealed class ShapeTrimLawTests {
    private const string PrototypeId = "trimmed";

    private static readonly Vector3 PlateScale = new(
        x: 0.4f,
        y: 0.3f,
        z: 0.2f
    );
    private static readonly ShapeTrimDocument Trim = new(
        Inset: 0.003f,
        Material: 1,
        Shape: "cutter",
        Width: 0.05f
    );

    private static ShapeDocument Cutter(int id = 0, Vector3? position = null) =>
        Shape(
            SdfSolidPrimitive.Sphere,
            new Vector3(value: 0.3f),
            name: "cutter",
            position: position,
            id: id
        );
    private static CreationDocument Document(params ShapeDocument[] shapes) => CreationFixtures.Document(
        name: PrototypeId,
        shapes: shapes
    );
    private static SdfProgram EmitPool(ShapeDocument[] shapes, float bodyScale = 1f) => CreationFixtures.EmitPool(
        bodyScale: bodyScale,
        name: PrototypeId,
        palette: CreationFixtures.GreyAndBlue,
        shapes: shapes
    );
    private static (SdfProgram Program, int HostMaterial, int TrimMaterial) EmitStatic(params ShapeDocument[] shapes) {
        var builder = new SdfProgramBuilder();
        var hostMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var trimMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: (Vector3.One * 0.5f)));

        CreationStampEmitter.Emit(
            builder: builder,
            document: Document(shapes),
            transform: new CreationStampTransform(
                Origin: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: 1f,
                ReflectionNormal: null
            ),
            materialFor: s => ((s.Material == 0)
            ? hostMaterial
            : trimMaterial)
        );

        return (builder.Build(buildInstanceGrid: false), hostMaterial, trimMaterial);
    }
    // A program's field scopes, each as the instructions from its PushField through its matching PopField (no
    // nesting, so a plain stack-depth walk finds every top-level pair).
    private static IReadOnlyList<SdfInstruction[]> Scopes(SdfProgram program) {
        var instructions = program.Instructions;
        var scopes = new List<SdfInstruction[]>();
        var push = -1;

        for (var index = 0; (index < instructions.Count); index++) {
            if (
                (instructions[index].Op == SdfOp.PushField) &&
                (push < 0)
            ) {
                push = index;
            } else if (
                (instructions[index].Op == SdfOp.PopField) &&
                (push >= 0)
            ) {
                scopes.Add(item: instructions.Skip(count: push).Take(count: ((index - push) + 1)).ToArray());
                push = -1;
            }
        }

        return scopes;
    }
    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, string? name = null, Vector3? position = null, IReadOnlyList<ShapeTrimDocument>? trims = null, int? group = null, IReadOnlyList<ShapeDomainOp>? domain = null, SdfBlendOp? blend = null, int id = 0) =>
        new(
            Id: id,
            Name: ((name is null)
            ? null
            : (DocumentIdentifier)name),
            Type: type,
            Position: (position ?? Vector3.Zero),
            Rotation: Quaternion.Identity,
            Scale: scale,
            Material: 0,
            Blend: (blend ?? SdfBlendOp.Union),
            Smooth: 0f,
            Group: group,
            Domain: domain,
            Trims: trims
        );

    [Fact]
    public void ANonFiniteOrNegativeInsetIsRefusedByName() {
        CreationFixtures.AssertRefusesNaming(
            document: Document(
                Cutter(),
                Shape(
                    SdfSolidPrimitive.Box,
                    PlateScale,
                    name: "host",
                    trims: [Trim with { Inset = float.NaN }],
                    id: 1
                )
            ),
            needle: "inset"
        );
        CreationFixtures.AssertRefusesNaming(
            document: Document(
                Cutter(),
                Shape(
                    SdfSolidPrimitive.Box,
                    PlateScale,
                    name: "host",
                    trims: [Trim with { Inset = -0.01f }],
                    id: 1
                )
            ),
            needle: "inset"
        );
    }
    [Fact]
    public void ANonPositiveWidthIsRefusedByName() {
        CreationFixtures.AssertRefusesNaming(
            document: Document(
                Cutter(),
                Shape(
                    SdfSolidPrimitive.Box,
                    PlateScale,
                    name: "host",
                    trims: [Trim with { Width = 0f }],
                    id: 1
                )
            ),
            needle: "width"
        );
        CreationFixtures.AssertRefusesNaming(
            document: Document(
                Cutter(),
                Shape(
                    SdfSolidPrimitive.Box,
                    PlateScale,
                    name: "host",
                    trims: [Trim with { Width = -0.1f }],
                    id: 1
                )
            ),
            needle: "width"
        );
    }
    [Fact]
    public void ATrimEmitsOneScopePerTrimWithTwoShapesAndAnIsolatedErosion() {
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: [Trim],
            id: 1
        );

        var (program, _, _) = EmitStatic(
            Cutter(),
            host
        );
        var scopes = Scopes(program: program);

        Assert.Single(collection: scopes);

        var scope = scopes[0];
        var shapes = scope.Where(predicate: instruction => (instruction.Op == SdfOp.ShapeBlend)).ToArray();
        var dilates = scope.Count(predicate: instruction => (instruction.Op == SdfOp.Dilate));

        Assert.Equal(
            2,
            shapes.Length
        );
        Assert.Equal(
            actual: dilates,
            expected: 1
        );
        Assert.Equal(
            ((uint)SdfBlendOp.Union),
            shapes[0].Blend
        );
        Assert.Equal(
            ((uint)SdfBlendOp.Intersection),
            shapes[1].Blend
        );
        // The whole program: the cutter's own shape, the host's own shape, plus the trim's two.
        Assert.Equal(
            4,
            program.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.ShapeBlend))
        );
    }
    [Fact]
    public void ATrimNamingALaterDeclaredShapeIsRefusedByName() {
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: [Trim]
        );
        var cutter = Cutter(id: 1);

        CreationFixtures.AssertRefusesNaming(
            document: Document(
                host,
                cutter
            ),
            needle: "declared before"
        );
    }
    [Fact]
    public void ATrimNamingAnUndeclaredShapeIsRefusedByName() =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(Shape(
                SdfSolidPrimitive.Box,
                PlateScale,
                name: "host",
                trims: [Trim]
            )),
            needle: "names no shape"
        );
    [Fact]
    public void ATrimOnADomainFoldedShapeIsRefusedByName() =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(
                Cutter(),
                Shape(
                    SdfSolidPrimitive.Box,
                    PlateScale,
                    name: "host",
                    trims: [Trim],
                    domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)],
                    id: 1
                )
            ),
            needle: "domain"
        );
    [Fact]
    public void ATrimOnAGroupedShapeIsRefusedByName() =>
        CreationFixtures.AssertRefusesNaming(
            document: Document(
                Cutter(),
                Shape(
                    SdfSolidPrimitive.Box,
                    PlateScale,
                    name: "host",
                    trims: [Trim],
                    group: 1,
                    id: 1
                )
            ),
            needle: "grouped"
        );
    [Fact]
    public void ATrimOnAScopeForcedCreationIsRefusedByName() {
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: [Trim],
            id: 1
        );
        var sibling = Shape(
            SdfSolidPrimitive.Sphere,
            Vector3.One,
            blend: SdfBlendOp.Subtraction,
            id: 2
        );

        CreationFixtures.AssertRefusesNaming(
            document: Document(
                Cutter(),
                host,
                sibling
            ),
            needle: "scope"
        );
    }
    [Fact]
    public void ATrimOnAnEarlierDeclaredReferenceIsAccepted() =>
        CreationFixtures.AssertAccepts(document: Document(
            Cutter(),
            Shape(
                SdfSolidPrimitive.Box,
                PlateScale,
                name: "host",
                trims: [Trim],
                id: 1
            )
        ));
    // The reference's OWN pose is re-read: a reference offset from the origin trims a band that follows it, not a
    // band fixed at the host's local origin. Measured through the deterministic evaluator's per-point material — the
    // trim's own material appears only near the reference, never on the host's surface far from it. The near probe
    // sits off the cutter's own centre (which the cutter's OWN plain instance, present in the same creation, would
    // otherwise win outright as the nearest solid, telling nothing about the trim scope itself).
    [Fact]
    public void ATrimPaintsTheHostsSurfaceOnlyNearTheReferenceShape() {
        var cutter = Cutter(position: new Vector3(
            x: 0f,
            y: 0f,
            z: 1f
        ));
        var host = Shape(
            SdfSolidPrimitive.Box,
            Vector3.One,
            name: "host",
            trims: [Trim with { Width = 0.4f }],
            id: 1
        );

        var (program, hostMaterial, trimMaterial) = EmitStatic(
            cutter,
            host
        );
        var evaluator = new SdfFieldEvaluator(program: program);

        Assert.True(condition: evaluator.TryDistance(
            position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: new Vector3(
                x: 0.5f,
                y: 0f,
                z: 1f
            ))),
            distance: out _,
            material: out var nearMaterial
        ));
        Assert.True(condition: evaluator.TryDistance(
            position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: new Vector3(
                x: 0.95f,
                y: 0.95f,
                z: 1f
            ))),
            distance: out _,
            material: out var farMaterial
        ));

        Assert.Equal(
            actual: nearMaterial,
            expected: trimMaterial
        );
        Assert.Equal(
            actual: farMaterial,
            expected: hostMaterial
        );
    }
    // The reference is re-emitted from its own slot/pose without its fold or group chain, so a reference carrying
    // either is refused by name; the same reference without them is the control.
    [Fact]
    public void ATrimReferencingADomainFoldedShapeIsRefusedByName() {
        var foldedCutter = Shape(
            SdfSolidPrimitive.Sphere,
            new Vector3(value: 0.3f),
            name: "cutter",
            domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)]
        );

        CreationFixtures.AssertRefusesNaming(
            document: Document(
                foldedCutter,
                Shape(
                    SdfSolidPrimitive.Box,
                    PlateScale,
                    name: "host",
                    trims: [Trim],
                    id: 1
                )
            ),
            needle: "reference 'cutter' carries domain operators"
        );
        CreationFixtures.AssertAccepts(document: Document(
            Cutter(),
            Shape(
                SdfSolidPrimitive.Box,
                PlateScale,
                name: "host",
                trims: [Trim],
                id: 1
            )
        ));
    }
    [Fact]
    public void ATrimReferencingAGroupedShapeIsRefusedByName() {
        var groupedCutter = Shape(
            SdfSolidPrimitive.Sphere,
            new Vector3(value: 0.3f),
            name: "cutter",
            group: 1
        );
        var sibling = Shape(
            SdfSolidPrimitive.Sphere,
            new Vector3(value: 0.2f),
            name: "sibling",
            group: 1,
            id: 2
        );

        CreationFixtures.AssertRefusesNaming(
            document: Document(
                groupedCutter,
                sibling,
                Shape(
                    SdfSolidPrimitive.Box,
                    PlateScale,
                    name: "host",
                    trims: [Trim],
                    id: 1
                )
            ),
            needle: "reference 'cutter' is a grouped shape"
        );
    }
    [Fact]
    public void ATrimmedShapeChargesTwoPerTrimAgainstTheStampBudget() {
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: [Trim, (Trim with { Shape = "cutter" })],
            id: 1
        );
        var document = Document(
            Cutter(),
            host
        );
        var bare = Document(
            Cutter(),
            Shape(
                SdfSolidPrimitive.Box,
                PlateScale,
                name: "host",
                id: 1
            )
        );

        // cutter (1) + host (1) + 2 trims * 2 = 6.
        Assert.Equal(
            6,
            document.StampShapeCount()
        );
        Assert.Equal(
            2,
            bare.StampShapeCount()
        );
    }
    [Fact]
    public void AnInsetPastMaxInsetIsRefusedByNameWhileAtTheCeilingIsAccepted() {
        var atCeiling = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: [Trim with { Inset = ShapeTrimDocument.MaxInset }],
            id: 1
        );
        var pastCeiling = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host2",
            trims: [Trim with { Inset = (ShapeTrimDocument.MaxInset + 0.01f) }],
            id: 2
        );

        CreationFixtures.AssertAccepts(document: Document(
            Cutter(),
            atCeiling
        ));
        CreationFixtures.AssertRefusesNaming(
            document: Document(
                Cutter(),
                pastCeiling
            ),
            needle: "inset"
        );
    }
    [Fact]
    public void AtMostMaxTrimsIsAccepted() {
        var trims = Enumerable.Repeat(
            count: ShapeTrimDocument.MaxTrims,
            element: Trim
        ).ToArray();
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: trims,
            id: 1
        );

        CreationFixtures.AssertAccepts(document: Document(
            Cutter(),
            host
        ));
    }
    [Fact]
    public void BothTrimShapesCarryTheTrimsOwnMaterial() {
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: [Trim],
            id: 1
        );

        var (program, hostMaterial, trimMaterial) = EmitStatic(
            Cutter(),
            host
        );
        var scope = Scopes(program: program)[0];
        var materials = scope.Where(predicate: instruction => (instruction.Op == SdfOp.ShapeBlend)).Select(selector: instruction => instruction.Material).ToArray();

        Assert.Equal(
            2,
            materials.Length
        );
        Assert.Equal(
            ((uint)trimMaterial),
            materials[0]
        );
        Assert.Equal(
            ((uint)trimMaterial),
            materials[1]
        );
        Assert.NotEqual(
            actual: trimMaterial,
            expected: hostMaterial
        );
    }
    // The contact/collider seam: Trims never reach it, exactly like Panel.
    [Fact]
    public void ContactCopiesAreIdenticalWithAndWithoutTrims() {
        var trimmed = Document(
            Cutter(),
            Shape(
                SdfSolidPrimitive.Box,
                PlateScale,
                name: "host",
                trims: [Trim],
                id: 1
            )
        );
        var bare = Document(
            Cutter(),
            Shape(
                SdfSolidPrimitive.Box,
                PlateScale,
                name: "host",
                id: 1
            )
        );
        var transform = new FixedCreationStampTransform(
            Origin: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity,
            Scale: FixedQ4816.One,
            ReflectionNormal: null
        );
        var trimmedCopies = new List<FixedCreationStampPrimitiveCopy>();
        var bareCopies = new List<FixedCreationStampPrimitiveCopy>();

        CreationStampEmitter.VisitFixedPrimitiveCopies(
            document: trimmed,
            transform: transform,
            visitor: trimmedCopies.Add
        );
        CreationStampEmitter.VisitFixedPrimitiveCopies(
            document: bare,
            transform: transform,
            visitor: bareCopies.Add
        );

        Assert.Equal(
            bareCopies.Count,
            trimmedCopies.Count
        );
        for (var i = 0; (i < bareCopies.Count); i++) {
            Assert.Equal(
                bareCopies[i].Center,
                trimmedCopies[i].Center
            );
            Assert.Equal(
                bareCopies[i].HalfExtents,
                trimmedCopies[i].HalfExtents
            );
        }
    }
    [Fact]
    public void MoreThanMaxTrimsIsRefusedByName() {
        var trims = Enumerable.Repeat(
            count: (ShapeTrimDocument.MaxTrims + 1),
            element: Trim
        ).ToArray();
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: trims,
            id: 1
        );

        CreationFixtures.AssertRefusesNaming(
            document: Document(
                Cutter(),
                host
            ),
            needle: "exceeds"
        );
    }
    [Fact]
    public void MultipleTrimsEmitSequentialNonNestedScopes() {
        var second = Shape(
            SdfSolidPrimitive.Box,
            new Vector3(value: 0.2f),
            name: "second",
            id: 1
        );
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: [Trim, (Trim with { Shape = "second" })],
            id: 2
        );

        var (program, _, _) = EmitStatic(
            Cutter(),
            second,
            host
        );

        Assert.Equal(
            2,
            Scopes(program: program).Count
        );
        Assert.Equal(
            2,
            program.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.PushField))
        );
        Assert.Equal(
            2,
            program.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.PopField))
        );
    }
    [Fact]
    public void ThePoolEmitsOneScopeForALiveTrimmedSlot() {
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: [Trim],
            id: 1
        );
        var program = EmitPool(shapes: [Cutter(), host]);

        Assert.Equal(
            1,
            program.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.PushField))
        );
        Assert.Equal(
            1,
            program.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.PopField))
        );
    }
    // The pool's probe: every slot reserves MaxTrims worst-case forms unconditionally, so a live trimmed slot never
    // outgrows the envelope.
    [Fact]
    public void ThePoolProbeDominatesALiveTrimmedSlot() {
        var host = Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            name: "host",
            trims: [Trim],
            id: 1
        );
        var probe = CreationFixtures.PoolProbe;
        var live = EmitPool(shapes: [Cutter(), host]);

        Assert.True(
            condition: (probe.Words.Length >= live.Words.Length),
            userMessage: $"the pool probe reserves {probe.Words.Length} words; a live trimmed Box emits {live.Words.Length}."
        );
    }
}
