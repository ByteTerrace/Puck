using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    /// <summary>The most the squared length of a lane the contract classes as unit (<see cref="SdfLaneContract.Unit"/>)
    /// may differ from one. The fixed-point encoding boundary (<see cref="SdfProgramBuilder.Rotate(Puck.Maths.FixedQuaternion)"/>)
    /// normalizes on the 2⁻¹⁶ grid, so its squared length lies within about 2⁻¹⁴ of one; this admits it, and stretches
    /// a field the step analysis counts as isometric by at most one part in sixteen thousand.</summary>
    public const float UnitLengthTolerance = (1f / 8192f);

    /// <summary>Returns how the program's admission door classes each lane of <paramref name="instruction"/>, checking
    /// the instruction against that contract as the door does. Every operation and shape type has one row; a path's
    /// derived lanes are checked against its edge table only inside a program.</summary>
    /// <param name="instruction">The instruction.</param>
    /// <returns>The lane classes.</returns>
    /// <exception cref="ArgumentException">A lane breaks the contract: a derived lane differs from its derivation, a unit
    /// lane is not of unit length, a zero lane is not zero, or an authored lane lies outside its domain.</exception>
    public static SdfLaneContract LaneContractOf(SdfInstruction instruction) => RequireLaneContract(
        index: 0,
        instruction: instruction,
        paramName: nameof(instruction)
    );

    // The lane contract (step 1 of docs/plans/terrain-pass/relief-mask-design.md): one row per operation and per shape
    // type, written as switches with no default row that admits, so an operation or shape added later is refused until
    // it has one. Every lane the builder computes from others is recomputed here by the function the builder calls
    // (SdfDerivedLanes, SdfNoiseDisplacement) and compared bit for bit, so the kernels and the step analysis, which trust
    // those lanes, read only what the builder would have written. Runs after RequireFiniteOperands.
    private static SdfLaneContract RequireLaneContract(SdfInstruction instruction, int index, string paramName) {
        var audit = new LaneAudit(index: index, instruction: instruction, paramName: paramName);

        switch (instruction.Op) {
            case SdfOp.ResetPoint:
                audit.Zero(lanes: SdfOperandLanes.All);
                break;
            case SdfOp.Translate:
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data0W | SdfOperandLanes.Data1));
                break;
            case SdfOp.Rotate:
                audit.Unit(lanes: SdfOperandLanes.Data0, subject: "rotation quaternion");
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data1));
                break;
            case SdfOp.Scale:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z), minimum: SdfSolidGeometry.MinimumScale, magnitude: true);
                audit.Derived(lane: SdfOperandLanes.Data0W, expected: SdfDerivedLanes.ScaleCorrection(scale: audit.Data0Xyz), derivation: "distance correction, its smallest axis");
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data1));
                break;
            case SdfOp.TransformDynamic:
                _ = DecodeDynamicSlot(value: instruction.Data0.X, paramName: paramName);
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z | SdfOperandLanes.Data0W | SdfOperandLanes.Data1));
                break;
            case SdfOp.RotatePlane:
                audit.Require(condition: Enum.IsDefined(value: ((SdfPlane)instruction.Shape)), lane: SdfOperandLanes.Shape, requirement: "it names the rotated plane, a defined SdfPlane");
                audit.Require(condition: Enum.IsDefined(value: ((SdfAxis)instruction.Blend)), lane: SdfOperandLanes.Blend, requirement: "it names the driving axis, a defined SdfAxis");
                audit.Zero(lanes: (SdfOperandLanes.Material | SdfOperandLanes.Data0Z | SdfOperandLanes.Data0W | SdfOperandLanes.Data1));
                break;
            case SdfOp.Elongate:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z), minimum: 0f);
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data0W | SdfOperandLanes.Data1));
                break;
            case SdfOp.ShapeBlend:
                RequireShapeLanes(audit: ref audit);
                break;
            case SdfOp.Repeat:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z), minimum: SdfDerivedLanes.MinRepeatSpacing);
                audit.Derived(lanes: (SdfOperandLanes.Data1X | SdfOperandLanes.Data1Y | SdfOperandLanes.Data1Z), expected: SdfDerivedLanes.Reciprocal(extents: audit.Data0Xyz), derivation: "reciprocal spacing");
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data0W | SdfOperandLanes.Data1W));
                break;
            case SdfOp.RepeatLimited:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z), minimum: SdfDerivedLanes.MinRepeatSpacing);
                audit.AtLeast(lanes: (SdfOperandLanes.Data1X | SdfOperandLanes.Data1Y | SdfOperandLanes.Data1Z), minimum: 0f);
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data0W | SdfOperandLanes.Data1W));
                break;
            case SdfOp.Onion:
                audit.AtLeast(lanes: SdfOperandLanes.Data0X, minimum: 0f);
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z | SdfOperandLanes.Data0W | SdfOperandLanes.Data1));
                break;
            case SdfOp.Dilate:
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z | SdfOperandLanes.Data0W | SdfOperandLanes.Data1));
                break;
            case SdfOp.WallpaperFold:
                RequireContinuousWallpaper(index: index, instruction: instruction, paramName: paramName);
                audit.MarkDerived(lanes: (SdfOperandLanes.Data0Z | SdfOperandLanes.Data0W));
                audit.Zero(lanes: (SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W));
                break;
            case SdfOp.LogSphere:
                audit.AtLeast(lanes: SdfOperandLanes.Data0X, minimum: MathF.Log(x: SdfDerivedLanes.MinLogSphereRatio));
                audit.Derived(lane: SdfOperandLanes.Data0Z, expected: SdfDerivedLanes.LogSphereInverseCell(logCell: instruction.Data0.X), derivation: "reciprocal log cell");
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data0W | SdfOperandLanes.Data1));
                break;
            case SdfOp.CellJitter: {
                    audit.Require(condition: Enum.IsDefined(value: ((SdfNoiseFlavor)instruction.Blend)), lane: SdfOperandLanes.Blend, requirement: "it names the offset's distribution, a defined SdfNoiseFlavor");
                    audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z), minimum: SdfDerivedLanes.MinRepeatSpacing);

                    var spacing = audit.Data0Xyz;

                    audit.Require(condition: ((MathF.Abs(x: instruction.Data0.W) * 0.5f) < (0.5f * MathF.Min(x: spacing.X, y: MathF.Min(x: spacing.Y, y: spacing.Z)))), lane: SdfOperandLanes.Data0W, requirement: "half the jitter must stay under half the smallest spacing, or jittered content crosses a cell boundary");
                    audit.Derived(lanes: (SdfOperandLanes.Data1X | SdfOperandLanes.Data1Y | SdfOperandLanes.Data1Z), expected: SdfDerivedLanes.Reciprocal(extents: spacing), derivation: "reciprocal spacing");
                    audit.Require(condition: ((instruction.Data1.W >= 0f) && (instruction.Data1.W <= 1f)), lane: SdfOperandLanes.Data1W, requirement: "a tumble lies in [0, 1]");
                    break;
                }
            case SdfOp.RepeatPolar: {
                    audit.Require(condition: Enum.IsDefined(value: ((SdfAxis)instruction.Shape)), lane: SdfOperandLanes.Shape, requirement: "it names the rotation axis, a defined SdfAxis");
                    audit.Require(condition: (instruction.Blend <= 1u), lane: SdfOperandLanes.Blend, requirement: "the mirror flag is 0 or 1");

                    var count = instruction.Data0.Z;

                    audit.Require(condition: ((count >= 1f) && (count <= SdfProgramBuilder.MaxExactFloatSectorCount) && (count == MathF.Floor(x: count))), lane: SdfOperandLanes.Data0Z, requirement: $"a sector count is a whole number in [1, {SdfProgramBuilder.MaxExactFloatSectorCount}]");

                    var (angle, inverseAngle, inverseCount) = SdfDerivedLanes.PolarSectors(sectors: ((int)count));

                    audit.Derived(lane: SdfOperandLanes.Data0X, expected: angle, derivation: "sector angle");
                    audit.Derived(lane: SdfOperandLanes.Data0Y, expected: inverseAngle, derivation: "reciprocal sector angle");
                    audit.Derived(lane: SdfOperandLanes.Data0W, expected: inverseCount, derivation: "reciprocal sector count");
                    audit.Zero(lanes: SdfOperandLanes.Data1);
                    break;
                }
            case SdfOp.Displace:
            case SdfOp.DomainWarp:
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data1));
                break;
            case SdfOp.SymmetryPlane:
                audit.Unit(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z), subject: "plane normal");
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data1));
                break;
            case SdfOp.PushField:
                audit.Zero(lanes: SdfOperandLanes.All);
                break;
            case SdfOp.PopField:
                RequirePopLanes(audit: ref audit);
                break;
            case SdfOp.NoiseDisplace:
                // Material counts the mask rows before the relief, an authored lane the program's door checks against
                // the run (DecodeReliefRuns).
                _ = SdfNoiseDisplacement.Of(instruction: instruction);
                audit.MarkDerived(lanes: SdfOperandLanes.Data1X);
                audit.Zero(lanes: (SdfOperandLanes.Data1Y | SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W));
                break;
            case SdfOp.AxialProfile:
                audit.Require(condition: Enum.IsDefined(value: ((SdfAxis)instruction.Shape)), lane: SdfOperandLanes.Shape, requirement: "it names the profile axis, a defined SdfAxis");
                audit.Require(condition: (instruction.Data0.W > 0f), lane: SdfOperandLanes.Data0W, requirement: "an inverse span is above zero");
                audit.Require(condition: (instruction.Data1.Y > 0f), lane: SdfOperandLanes.Data1Y, requirement: "a start scale is above zero");
                audit.Derived(lane: SdfOperandLanes.Data1X, expected: SdfDerivedLanes.AxialProfileCorrection(amount: instruction.Data0.X, bulge: instruction.Data0.Y, startScale: instruction.Data1.Y), derivation: "distance correction, one over its largest scale");
                audit.Zero(lanes: (SdfOperandLanes.Blend | SdfOperandLanes.Material | SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W));
                break;
            case SdfOp.Shear:
                audit.Require(condition: Enum.IsDefined(value: ((SdfAxis)instruction.Shape)), lane: SdfOperandLanes.Shape, requirement: "it names the sheared axis, a defined SdfAxis");
                audit.Require(condition: (Enum.IsDefined(value: ((SdfAxis)instruction.Blend)) && (instruction.Blend != instruction.Shape)), lane: SdfOperandLanes.Blend, requirement: "it names the driving axis, a defined SdfAxis other than the sheared one");
                audit.Zero(lanes: (SdfOperandLanes.Material | SdfOperandLanes.Data0W | SdfOperandLanes.Data1));
                break;
            case SdfOp.GaussianPush:
                audit.Require(condition: float.IsFinite(f: BitConverter.UInt32BitsToSingle(value: instruction.Shape)), lane: SdfOperandLanes.Shape, requirement: "it carries the push's z component as a finite float's bits");
                audit.AtLeast(lanes: (SdfOperandLanes.Data1X | SdfOperandLanes.Data1Y | SdfOperandLanes.Data1Z), minimum: SdfProgramBuilder.GaussianPushMinRadius);
                audit.Zero(lanes: (SdfOperandLanes.Blend | SdfOperandLanes.Material));
                break;
            case SdfOp.LaneErode:
                RequireLaneErodeLaneIndex(index: index, instruction: instruction, paramName: paramName);
                audit.Require(condition: (instruction.Data0.Y != instruction.Data0.Z), lane: SdfOperandLanes.Data0Z, requirement: "an erosion's from and to differ");
                audit.AtLeast(lanes: SdfOperandLanes.Data1X, minimum: 0f);
                audit.Zero(lanes: (SdfOperandLanes.Header | SdfOperandLanes.Data1Y | SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W));
                break;
            case SdfOp.CellDisplace:
                new SdfCellDisplacement(instruction.Data0.X, instruction.Data0.Y, instruction.Shape, ((SdfCellMode)instruction.Blend), instruction.Data0.Z).Validate();
                audit.Zero(lanes: (SdfOperandLanes.Material | SdfOperandLanes.Data0W | SdfOperandLanes.Data1));
                break;
            case SdfOp.ReliefRow:
                RequireReliefRowLanes(audit: ref audit);
                break;
            default:
                throw new ArgumentException(message: $"Instruction {index} is {instruction.Op}, which has no row in the program's lane contract.", paramName: paramName);
        }

        return audit.Contract;
    }
    private static void RequirePopLanes(ref LaneAudit audit) {
        var instruction = audit.Instruction;

        RequirePackedBlend(blend: instruction.Blend, index: audit.Index, paramName: audit.ParamName);
        audit.MarkWritten(lanes: SdfOperandLanes.Data1Y);
        audit.Zero(lanes: (SdfOperandLanes.Shape | SdfOperandLanes.Material | SdfOperandLanes.Data1W));

        switch ((SdfBlendOp)instruction.Blend) {
            case SdfBlendOp.Morph: {
                    var lane = instruction.Data0.X;

                    audit.Require(condition: ((lane >= 0f) && (lane <= 3f) && (lane == MathF.Floor(x: lane))), lane: SdfOperandLanes.Data0X, requirement: "a morph's lane index is a whole number in [0, 3]");
                    audit.Require(condition: (instruction.Data0.Y != instruction.Data0.Z), lane: SdfOperandLanes.Data0Z, requirement: "a morph's from and to thresholds differ");
                    audit.Zero(lanes: (SdfOperandLanes.Data0W | SdfOperandLanes.Data1X | SdfOperandLanes.Data1Z));
                    break;
                }
            case SdfBlendOp.StairsUnion:
            case SdfBlendOp.StairsSubtraction: {
                    var steps = instruction.Data1.Z;

                    audit.Require(condition: (instruction.Data1.X > 0f), lane: SdfOperandLanes.Data1X, requirement: "a stairs radius is above zero (a zero radius is a plain Union or Subtraction pop)");
                    audit.Require(condition: ((steps >= 1f) && (steps == MathF.Floor(x: steps))), lane: SdfOperandLanes.Data1Z, requirement: "a stairs step count is a whole number of at least one");
                    audit.Zero(lanes: SdfOperandLanes.Data0);
                    break;
                }
            default:
                audit.AtLeast(lanes: SdfOperandLanes.Data1X, minimum: 0f);
                audit.Zero(lanes: (SdfOperandLanes.Data0 | SdfOperandLanes.Data1Z));
                break;
        }
    }
    // A relief mask's row, by its kind (SdfReliefRowKind, the Blend lane). Every lane of every kind is classed, and each
    // magnitude stays within what both carriers hold (SdfReliefRegion.MaxMagnitude), so no unused lane reaches the
    // geometry key and no value the kernel reads verbatim saturates in the fixed-point evaluator. The door
    // (DecodeReliefRuns) checks the run's order, owner and caps.
    private static void RequireReliefRowLanes(ref LaneAudit audit) {
        var instruction = audit.Instruction;

        audit.Zero(lanes: SdfOperandLanes.Material);
        audit.Within(lanes: (SdfOperandLanes.Data0 | SdfOperandLanes.Data1), limit: SdfReliefRegion.MaxMagnitude);

        switch ((SdfReliefRowKind)instruction.Blend) {
            case SdfReliefRowKind.Band: {
                    var refusal = SdfReliefBand.TryRead(band: out _, row: instruction);

                    audit.Zero(lanes: (SdfOperandLanes.Shape | SdfOperandLanes.Data1));
                    audit.Require(condition: (refusal is null), lane: SdfOperandLanes.Data0X, requirement: (refusal ?? string.Empty));
                    break;
                }
            case SdfReliefRowKind.Pose:
                audit.Zero(lanes: (SdfOperandLanes.Shape | SdfOperandLanes.Data0W));
                audit.Unit(lanes: SdfOperandLanes.Data1, subject: "region pose quaternion");
                break;
            case SdfReliefRowKind.Primitive:
                audit.Require(condition: SdfReliefRegion.IsListed(shape: ((SdfShapeType)instruction.Shape)), lane: SdfOperandLanes.Shape, requirement: "a region's primitive is a Box, a Superellipsoid, a Capsule or a Plane");
                audit.Zero(lanes: SdfOperandLanes.Data1X);
                RequireShapeLanes(audit: ref audit);
                break;
            default:
                audit.Require(condition: false, lane: SdfOperandLanes.Blend, requirement: "it names the row's kind, a defined SdfReliefRowKind");
                break;
        }
    }
    private static void RequireShapeLanes(ref LaneAudit audit) {
        var instruction = audit.Instruction;
        var shape = ((SdfShapeType)instruction.Shape);

        if (!Enum.IsDefined(value: shape)) {
            throw new ArgumentException(
                message: $"The SDF instruction set refuses undeclared shape {instruction.Shape} at instruction {audit.Index}.",
                paramName: audit.ParamName
            );
        }

        // Every shape but a sampled region carries its blend's smooth radius in Data1.x, any finite value (the kernels
        // clamp its sign); the blend and the material lanes are checked by the packed contract itself.
        const SdfOperandLanes Dimensions = (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z);
        const SdfOperandLanes Data1Tail = (SdfOperandLanes.Data1Y | SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W);

        switch (shape) {
            case SdfShapeType.Box:
            case SdfShapeType.ScreenSlab:
                audit.AtLeast(lanes: SdfOperandLanes.Data0, minimum: 0f);
                audit.Zero(lanes: Data1Tail);
                break;
            case SdfShapeType.Capsule:
                // Both readers project onto the endpoint by its own squared length, which must stay finite in float.
                audit.AtLeast(lanes: SdfOperandLanes.Data0W, minimum: 0f);
                audit.Require(condition: float.IsFinite(f: Vector3.Dot(vector1: audit.Data0Xyz, vector2: audit.Data0Xyz)), lane: SdfOperandLanes.Data0X, requirement: "a capsule's endpoint has a finite squared length");
                audit.Zero(lanes: Data1Tail);
                break;
            case SdfShapeType.Sphere:
                audit.AtLeast(lanes: SdfOperandLanes.Data0X, minimum: 0f);
                audit.Zero(lanes: (SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z | SdfOperandLanes.Data0W | Data1Tail));
                break;
            case SdfShapeType.Torus:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y), minimum: 0f);
                audit.Zero(lanes: (SdfOperandLanes.Data0Z | SdfOperandLanes.Data0W | Data1Tail));
                break;
            case SdfShapeType.Cylinder:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y | SdfOperandLanes.Data1W), minimum: 0f);
                audit.Zero(lanes: (SdfOperandLanes.Data0Z | SdfOperandLanes.Data0W | SdfOperandLanes.Data1Y | SdfOperandLanes.Data1Z));
                break;
            case SdfShapeType.Plane:
                audit.Unit(lanes: Dimensions, subject: "plane normal");
                audit.Zero(lanes: Data1Tail);
                break;
            case SdfShapeType.Vesica: {
                    var radius = instruction.Data0.X;

                    audit.AtLeast(lanes: SdfOperandLanes.Data0X, minimum: 0f);
                    audit.Require(condition: ((instruction.Data0.Y >= 0f) && (instruction.Data0.Y <= (radius * SdfDerivedLanes.MaxVesicaSeparationShare))), lane: SdfOperandLanes.Data0Y, requirement: $"a half-separation lies in [0, {SdfDerivedLanes.MaxVesicaSeparationShare} times the radius]");
                    audit.Derived(lane: SdfOperandLanes.Data0Z, expected: SdfDerivedLanes.VesicaHalfHeight(radius: radius, halfSeparation: instruction.Data0.Y), derivation: "tip half-height");
                    audit.Zero(lanes: (SdfOperandLanes.Data0W | Data1Tail));
                    break;
                }
            case SdfShapeType.RoundedRectangle:
            case SdfShapeType.ChamferedRectangle:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0 | SdfOperandLanes.Data1W), minimum: 0f);
                audit.Lift();

                if (shape == SdfShapeType.ChamferedRectangle) {
                    audit.Zero(lanes: SdfOperandLanes.Data1Z);
                } else {
                    audit.AtLeast(lanes: SdfOperandLanes.Data1Z, minimum: 0f);
                }

                break;
            case SdfShapeType.RegularPolygon:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0W | SdfOperandLanes.Data1W), minimum: 0f);
                audit.HalfSector(lane: SdfOperandLanes.Data0Y, minimumSides: 3);
                audit.Derived(lane: SdfOperandLanes.Data1Z, expected: 1f, derivation: "regular-polygon sharpness constant");
                audit.Lift();
                audit.Zero(lanes: SdfOperandLanes.Data0Z);
                break;
            case SdfShapeType.Star: {
                    audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0W), minimum: 0f);

                    var sides = audit.HalfSector(lane: SdfOperandLanes.Data0Y, minimumSides: 2);

                    // The sharpness pair is (cos, sin) of π/m for m in [2, n]: a unit pair at an angle in [π/n, π/2].
                    audit.Unit(lanes: (SdfOperandLanes.Data0Z | SdfOperandLanes.Data1Z), subject: "sharpness pair");

                    var angle = MathF.Atan2(y: instruction.Data1.Z, x: instruction.Data0.Z);

                    audit.Require(condition: ((angle >= (SdfDerivedLanes.HalfSector(sides: sides) - 1e-5f)) && (angle <= ((0.5f * MathF.PI) + 1e-5f))), lane: SdfOperandLanes.Data1Z, requirement: "a star's sharpness angle π/m lies in [π/n, π/2]");
                    audit.Lift();
                    audit.Zero(lanes: SdfOperandLanes.Data1W);
                    break;
                }
            case SdfShapeType.RoundCone: {
                    audit.AtLeast(lanes: Dimensions, minimum: 0f);

                    var (slope, cosine) = SdfDerivedLanes.RoundConeSlope(lowerRadius: instruction.Data0.X, upperRadius: instruction.Data0.Y, height: instruction.Data0.Z);

                    audit.Derived(lane: SdfOperandLanes.Data0W, expected: slope, derivation: "slope");
                    audit.Derived(lane: SdfOperandLanes.Data1Y, expected: cosine, derivation: "slope cosine");
                    audit.Zero(lanes: (SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W));
                    break;
                }
            case SdfShapeType.Trapezoid:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0 | SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W), minimum: 0f);
                RequireTrapezoidProfileSlant(index: audit.Index, instruction: instruction, paramName: audit.ParamName);
                audit.Lift();
                break;
            case SdfShapeType.Ellipse:
                audit.AtLeast(lanes: (SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y | SdfOperandLanes.Data0W | SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W), minimum: 0f);
                audit.Lift();
                audit.Zero(lanes: SdfOperandLanes.Data0Z);
                break;
            case SdfShapeType.Glyph:
                // Data0.xy are atlas UVs packed as integer bits. The sampling correction (Data1.w) is derived from the
                // atlas's pixels, which the program identifies only by hash, so the door checks its range alone.
                audit.AtLeast(lanes: (SdfOperandLanes.Data0Z | SdfOperandLanes.Data0W), minimum: 0f);
                audit.Require(condition: ((instruction.Data1.Y > 0f) && (instruction.Data1.Z > 0f)), lane: SdfOperandLanes.Data1Y, requirement: "a glyph's cell half-extents are above zero");
                audit.Require(condition: ((instruction.Data1.W > 0f) && (instruction.Data1.W <= 1f)), lane: SdfOperandLanes.Data1W, requirement: "a glyph's sampling correction lies in (0, 1]");
                break;
            case SdfShapeType.SampledRegion: {
                    var dims = BitConverter.SingleToUInt32Bits(value: instruction.Data1.Y);
                    var mask = ((uint)SdfProgramBuilder.MaxSampledRegionDim);
                    var x = (dims & mask);
                    var y = ((dims >> 10) & mask);
                    var z = ((dims >> 20) & mask);

                    audit.Require(condition: (instruction.Data0.W > 0f), lane: SdfOperandLanes.Data0W, requirement: "a cell size is above zero");
                    audit.Require(condition: ((x >= 1u) && (y >= 1u) && (z >= 1u) && ((dims >> 30) == 0u)), lane: SdfOperandLanes.Data1Y, requirement: $"it packs three dimensions in [1, {SdfProgramBuilder.MaxSampledRegionDim}], ten bits each");
                    audit.AtLeast(lanes: SdfOperandLanes.Data1W, minimum: 0f);
                    audit.MarkWritten(lanes: SdfOperandLanes.Data1Z);
                    audit.Zero(lanes: SdfOperandLanes.Data1X);
                    break;
                }
            case SdfShapeType.Superellipsoid:
                // The radii and the exponent are values both carriers hold exactly (SdfDerivedLanes.FixedGrid), so the
                // kernel and the fixed-point evaluator scale the gauge by the same smallest radius.
                audit.AtLeast(lanes: Dimensions, minimum: SdfDerivedLanes.MinSuperellipsoidRadius);
                audit.Require(condition: ((instruction.Data0.W >= SdfProgramBuilder.MinSuperellipsoidExponent) && (instruction.Data0.W <= SdfProgramBuilder.MaxSuperellipsoidExponent)), lane: SdfOperandLanes.Data0W, requirement: $"an exponent lies in [{SdfProgramBuilder.MinSuperellipsoidExponent}, {SdfProgramBuilder.MaxSuperellipsoidExponent}], the interval its 1-Lipschitz proof covers");
                audit.OnFixedGrid(lanes: SdfOperandLanes.Data0);
                audit.Derived(lanes: Data1Tail,expected: SdfDerivedLanes.Reciprocal(extents: audit.Data0Xyz), derivation: "inverse radii");
                break;
            case SdfShapeType.ConvexPolygon:
                audit.MarkWritten(lanes: SdfOperandLanes.Data0X);
                audit.AtLeast(lanes: (SdfOperandLanes.Data0W | SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W), minimum: 0f);
                audit.Lift();
                audit.Zero(lanes: (SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z));
                break;
            case SdfShapeType.Sweep: {
                    var strands = instruction.Data0.Y;

                    audit.MarkWritten(lanes: SdfOperandLanes.Data0X);
                    audit.Require(condition: ((strands >= SdfProgramBuilder.MinSweepStrands) && (strands <= SdfProgramBuilder.MaxSweepStrands) && (strands == MathF.Floor(x: strands))), lane: SdfOperandLanes.Data0Y, requirement: $"a strand count is a whole number in [{SdfProgramBuilder.MinSweepStrands}, {SdfProgramBuilder.MaxSweepStrands}]");
                    audit.AtLeast(lanes: SdfOperandLanes.Data0W, minimum: 0f);
                    audit.Zero(lanes: Data1Tail);
                    break;
                }
            case SdfShapeType.Path:
                // ValidatePaths checks the edge count and the reach against the edge table, with the depth, the mode and
                // the reserved lanes.
                audit.MarkWritten(lanes: SdfOperandLanes.Data0X);
                audit.MarkDerived(lanes: (SdfOperandLanes.Data0Y | SdfOperandLanes.Data0Z));
                audit.Zero(lanes: (SdfOperandLanes.Data1Z | SdfOperandLanes.Data1W));
                break;
            default:
                throw new ArgumentException(message: $"Instruction {audit.Index} is a {shape} shape, which has no row in the program's lane contract.", paramName: audit.ParamName);
        }
    }

    // One instruction's lanes under audit: each check classes the lanes it reads and throws naming the instruction, the
    // lane, its value and what the contract requires.
    private struct LaneAudit(SdfInstruction instruction, int index, string paramName) {
        private SdfOperandLanes m_derived;
        private SdfOperandLanes m_unit;
        private SdfOperandLanes m_written;
        private SdfOperandLanes m_zero;

        public readonly SdfLaneContract Contract => new(Derived: m_derived, Unit: m_unit, Written: m_written, Zero: m_zero);
        public readonly Vector3 Data0Xyz => new(x: instruction.Data0.X, y: instruction.Data0.Y, z: instruction.Data0.Z);
        public readonly int Index => index;
        public readonly SdfInstruction Instruction => instruction;
        public readonly string ParamName => paramName;

        // Every lane in the set at least the minimum (by magnitude when asked: a mirror carries its sign in a scale).
        public readonly void AtLeast(SdfOperandLanes lanes, float minimum, bool magnitude = false) {
            foreach (var lane in Lanes(lanes: lanes)) {
                var value = Float(lane: lane);

                Require(condition: ((magnitude ? MathF.Abs(x: value) : value) >= minimum), lane: lane, requirement: $"its operation requires {(magnitude ? "a magnitude of " : string.Empty)}at least {minimum}");
            }
        }
        public void Derived(SdfOperandLanes lane, float expected, string derivation) {
            m_derived |= lane;

            if (BitConverter.SingleToUInt32Bits(value: Float(lane: lane)) != BitConverter.SingleToUInt32Bits(value: expected)) {
                throw new ArgumentException(message: $"Instruction {index} ({instruction.Op}) carries {Float(lane: lane)} in {Name(lane: lane)}, but its {derivation} derives {expected} from its other lanes; a program carries only the lanes the builder would write.", paramName: paramName);
            }
        }
        public void Derived(SdfOperandLanes lanes, Vector3 expected, string derivation) {
            var components = Lanes(lanes: lanes).ToArray();

            Derived(derivation: derivation, expected: expected.X, lane: components[0]);
            Derived(derivation: derivation, expected: expected.Y, lane: components[1]);
            Derived(derivation: derivation, expected: expected.Z, lane: components[2]);
        }
        // A star's or regular polygon's half-sector π/n, for a whole n of at least the minimum; returns n.
        public int HalfSector(SdfOperandLanes lane, int minimumSides) {
            var value = Float(lane: lane);
            var sides = ((value > 0f) ? MathF.Round(x: (MathF.PI / value)) : 0f);

            Require(condition: ((sides >= minimumSides) && (sides <= int.MaxValue)), lane: lane, requirement: $"a half-sector angle is π/n for a whole n of at least {minimumSides}");
            Derived(derivation: "half-sector angle", expected: SdfDerivedLanes.HalfSector(sides: ((int)sides)), lane: lane);

            return ((int)sides);
        }
        // A 2D profile's lift mode (Data1.y): a defined SdfLift as a whole float.
        public readonly void Lift() {
            var mode = instruction.Data1.Y;

            Require(condition: ((mode >= 0f) && (mode == MathF.Floor(x: mode)) && Enum.IsDefined(value: ((SdfLift)((uint)mode)))), lane: SdfOperandLanes.Data1Y, requirement: "it names the profile's lift, a defined SdfLift");
        }
        public void MarkDerived(SdfOperandLanes lanes) => m_derived |= lanes;
        // Every lane in the set a value Q48.16 and single-precision float both hold exactly (SdfDerivedLanes.FixedGrid).
        public readonly void OnFixedGrid(SdfOperandLanes lanes) {
            foreach (var lane in Lanes(lanes: lanes)) {
                var value = Float(lane: lane);

                Require(condition: (SdfDerivedLanes.FixedGrid(value: value) == value), lane: lane, requirement: "its operation requires a multiple of 2^-16, which both carriers hold exactly");
            }
        }
        // Every lane in the set within the magnitude both carriers hold without saturating.
        public readonly void Within(SdfOperandLanes lanes, float limit) {
            foreach (var lane in Lanes(lanes: lanes)) {
                Require(condition: (MathF.Abs(x: Float(lane: lane)) <= limit), lane: lane, requirement: $"its operation requires a magnitude of at most {limit}");
            }
        }
        public void MarkWritten(SdfOperandLanes lanes) => m_written |= lanes;
        public readonly void Require(bool condition, SdfOperandLanes lane, string requirement) {
            if (!condition) {
                throw new ArgumentException(message: $"Instruction {index} ({instruction.Op}) carries {Describe(lane: lane)} in {Name(lane: lane)}; {requirement}.", paramName: paramName);
            }
        }
        public void Unit(SdfOperandLanes lanes, string subject) {
            m_unit |= lanes;

            var squared = 0f;

            foreach (var lane in Lanes(lanes: lanes)) {
                squared += (Float(lane: lane) * Float(lane: lane));
            }

            if (!(MathF.Abs(x: (squared - 1f)) <= UnitLengthTolerance)) {
                throw new ArgumentException(message: $"Instruction {index} ({instruction.Op}) carries a {subject} of length {MathF.Sqrt(x: squared)}; the step analysis counts it as an isometry, so its length must be one.", paramName: paramName);
            }
        }
        public void Zero(SdfOperandLanes lanes) {
            m_zero |= lanes;

            foreach (var lane in Lanes(lanes: lanes)) {
                var zero = ((lane & SdfOperandLanes.Header) != 0)
                    ? (Header(lane: lane) == 0u)
                    : (Float(lane: lane) == 0f);

                if (!zero) {
                    throw new ArgumentException(message: $"Instruction {index} ({instruction.Op}) carries {Describe(lane: lane)} in {Name(lane: lane)}, a lane its operation leaves zero.", paramName: paramName);
                }
            }
        }

        private readonly string Describe(SdfOperandLanes lane) => (((lane & SdfOperandLanes.Header) != 0)
            ? Header(lane: lane).ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
            : Float(lane: lane).ToString(provider: System.Globalization.CultureInfo.InvariantCulture));
        private readonly float Float(SdfOperandLanes lane) => lane switch {
            SdfOperandLanes.Data0X => instruction.Data0.X,
            SdfOperandLanes.Data0Y => instruction.Data0.Y,
            SdfOperandLanes.Data0Z => instruction.Data0.Z,
            SdfOperandLanes.Data0W => instruction.Data0.W,
            SdfOperandLanes.Data1X => instruction.Data1.X,
            SdfOperandLanes.Data1Y => instruction.Data1.Y,
            SdfOperandLanes.Data1Z => instruction.Data1.Z,
            SdfOperandLanes.Data1W => instruction.Data1.W,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(lane)),
        };
        private readonly uint Header(SdfOperandLanes lane) => lane switch {
            SdfOperandLanes.Shape => instruction.Shape,
            SdfOperandLanes.Blend => instruction.Blend,
            SdfOperandLanes.Material => instruction.Material,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(lane)),
        };
    }

    // The single lanes of a set, in lane order.
    private static IEnumerable<SdfOperandLanes> Lanes(SdfOperandLanes lanes) {
        for (var bit = 0; (bit < 11); bit++) {
            var lane = ((SdfOperandLanes)(1 << bit));

            if ((lanes & lane) != 0) {
                yield return lane;
            }
        }
    }
    private static string Name(SdfOperandLanes lane) => lane switch {
        SdfOperandLanes.Data0X => "Data0.x",
        SdfOperandLanes.Data0Y => "Data0.y",
        SdfOperandLanes.Data0Z => "Data0.z",
        SdfOperandLanes.Data0W => "Data0.w",
        SdfOperandLanes.Data1X => "Data1.x",
        SdfOperandLanes.Data1Y => "Data1.y",
        SdfOperandLanes.Data1Z => "Data1.z",
        SdfOperandLanes.Data1W => "Data1.w",
        _ => lane.ToString(),
    };
}
