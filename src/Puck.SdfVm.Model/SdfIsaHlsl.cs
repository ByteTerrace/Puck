using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Maths;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.SdfVm;

/// <summary>
/// Generates <see cref="FileName"/>, the kernels' declarations of the field's instruction set: every enum an instruction
/// word carries, the packed-layout constants the interpreter decodes words with, the capacities and constants the
/// interpreter's shapes and hash read, the sampler filters, and the transform-slot sentinel and packing. It is all the
/// generated vocabulary the field interpreter and the mesh bake read; what the renderer alone reads (lights, exposure,
/// the visibility record, mesh records, the irradiance volume, the debug views) is <see cref="SdfRenderHlsl"/>'s, so a
/// renderer constant added there leaves the bake kernels' declarations, and their bytecode, as they were. Every value
/// is read from the C# model, never transcribed, and an enum member's name is its C# name in upper snake case after the
/// enum's prefix (<see cref="SdfOp.ResetPoint"/> is <c>SDF_OP_RESET_POINT</c>), so a new member reaches the kernels by
/// regenerating. <c>puck shaders generate</c> writes the file beside the kernels and <c>--check</c> fails on drift.
/// <para>The text is a pure function of the model: the same build generates the same bytes, with LF line endings, on
/// every host.</para>
/// <para>The instruction set's fingerprint (<see cref="DescribeFingerprint"/>) names what a host and its kernels must
/// agree on: the generated texts' layout declarations (<see cref="GenerateLayout"/>), which state every enum, lane
/// accessor, stride, word offset, mask and count the kernels read, the field's constants, and the scales the host
/// quantizes or decodes data with, and the encoding the model's builder and packer produce
/// (<see cref="SdfEncodingProbe.Describe()"/>: where every field, bitfield and table entry lands); the field's
/// fingerprint (<see cref="DescribeFieldFingerprint"/>) names this include's layout and that encoding alone. A tuning
/// declaration (<c>Tuning</c> below: a value only the shader reads, such as a bias, a share or a threshold) is in the
/// generated text and in no fingerprint, so retuning one keeps every stamp and a running host accepts the reloaded
/// kernels. <c>puck shaders generate</c> records both in <c>SdfIsaFingerprint</c> (<see cref="FingerprintSourcePath"/>),
/// which the engine reads. The SDF kernels' interfaces carry them as their stamps (<see cref="StampOf"/>,
/// <see cref="SdfKernelInterfaces"/>): the bake's the field's, every other the whole one's, so every kernel's bytecode
/// reflects the instruction set it was compiled against, and a reload refuses kernels whose stamp is not this
/// host's.</para>
/// </summary>
public static class SdfIsaHlsl {
    /// <summary>The file name of the generated include, which sits with the other generated declarations in
    /// <c>Assets/Shaders/Sdf/isa</c>.</summary>
    public const string FileName = "sdf-isa.hlsli";

    private const string Guard = "SDF_ISA_HLSLI";

    /// <summary>The repository-relative path of the generated source that records the fingerprints
    /// (<see cref="DescribeFingerprint"/>, <see cref="DescribeFieldFingerprint"/>) as <c>SdfIsaFingerprint.Value</c> and
    /// <c>SdfIsaFingerprint.Field</c> in <c>Puck.SdfVm</c>, so a host names its instruction set without describing the
    /// encoding. <c>--check</c> holds the record to both.</summary>
    public const string FingerprintSourcePath = "src/Puck.SdfVm/SdfIsaFingerprint.cs";

    /// <summary>Returns the fingerprint of the model's instruction set: <see cref="FingerprintOf"/> of the generated
    /// includes' layouts (this one's and the renderer's, <see cref="SdfRenderHlsl.GenerateLayout"/>), the sky's kind
    /// table (<see cref="SdfSkyKindsHlsl"/>) and the model's described encoding. It moves with any member, value, lane,
    /// stride, offset or quantization scale the kernels read from the model, with any sky kind's value or parameter
    /// layout, and with any change to where the builder puts a field or the packer a word. It does not move with a
    /// tuning value the shader alone reads.</summary>
    /// <returns>The fingerprint.</returns>
    public static uint DescribeFingerprint() =>
        FingerprintOf(
            encoding: SdfEncodingProbe.Describe(),
            include: ((((GenerateLayout() + SdfRenderHlsl.GenerateLayout()) + SdfSkyKindsHlsl.Generate()) + SdfSkyKindsHlsl.GenerateTable()) + SdfShadeHlsl.Generate())
        );
    /// <summary>Returns the fingerprint of the field's instruction set, which the mesh bake's interface carries as its
    /// stamp: <see cref="FingerprintOf"/> of this include's layout alone and the encoding described over each probe
    /// program's <see cref="SdfProgram.FieldWords"/>. It moves with what the field interpreter reads from the model and
    /// with the field words' encoding, and with nothing the renderer alone reads: no material, paint or screen value,
    /// stride or packing.</summary>
    /// <returns>The fingerprint.</returns>
    public static uint DescribeFieldFingerprint() =>
        FingerprintOf(
            encoding: SdfEncodingProbe.Describe(calls: SdfEncodingProbe.Calls(), wordsOf: static program => program.FieldWords.ToArray()),
            include: GenerateLayout()
        );
    /// <summary>Generates the source that records the fingerprints as <c>SdfIsaFingerprint.Value</c> and
    /// <c>SdfIsaFingerprint.Field</c>.</summary>
    /// <param name="fingerprint">The instruction set's fingerprint (<see cref="DescribeFingerprint"/>).</param>
    /// <param name="field">The field's fingerprint (<see cref="DescribeFieldFingerprint"/>).</param>
    /// <returns>The C# text.</returns>
    public static string GenerateFingerprintSource(uint fingerprint, uint field) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $$"""
                // Generated by `puck shaders generate` from the SDF instruction set's C# model; regenerate it, never edit it.
                namespace Puck.SdfVm;

                /// <summary>The fingerprints of the instruction set this build's kernels are declared against
                /// (<see cref="SdfIsaHlsl.DescribeFingerprint"/>, <see cref="SdfIsaHlsl.DescribeFieldFingerprint"/>),
                /// recorded when <c>puck shaders generate</c> wrote the declarations.</summary>
                public static class SdfIsaFingerprint {
                    /// <summary>The whole instruction set's fingerprint.</summary>
                    public const uint Value = 0x{{fingerprint:X8}}u;
                    /// <summary>The field's instruction set's fingerprint, which the mesh bake's interface carries.</summary>
                    public const uint Field = 0x{{field:X8}}u;
                }

                """
        );
    /// <summary>Generates the include.</summary>
    /// <returns>The HLSL text.</returns>
    /// <exception cref="InvalidOperationException">Two declarations would share one HLSL name.</exception>
    public static string Generate() => DeclareField().Text();
    /// <summary>Generates the include's layout: its stamped defines alone, one <c>#define</c> line each in declaration
    /// order, without comments or alignment, so a comment or a tuning value (<c>Declarations.Tuning</c>) moves nothing
    /// the fingerprints hash. Each declaration kind is classified here: an enum member, a count, a bit mask, a lane
    /// accessor, an expression and a signed value (strides, word offsets, flags, capacities) are layout and always
    /// stamped, as is every <c>Real</c>, which the field's own constants and the scales the host quantizes or decodes
    /// data with are; a <c>Tuning</c> real is a value only the shader reads, whose change retunes a result and misreads
    /// no data, and is not stamped.</summary>
    /// <returns>The HLSL text.</returns>
    public static string GenerateLayout() => DeclareField().Layout();
    /// <summary>Returns the fingerprint of an instruction set: the first four bytes, little-endian, of the SHA-256 of the
    /// UTF-8 text of its include, a NUL, and its described encoding.</summary>
    /// <param name="include">The include's stamped text (<see cref="GenerateLayout"/>).</param>
    /// <param name="encoding">The encoding's description (<see cref="SdfEncodingProbe.Describe()"/>).</param>
    /// <returns>The fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="include"/> or <paramref name="encoding"/> is
    /// <see langword="null"/>.</exception>
    public static uint FingerprintOf(string include, string encoding) {
        ArgumentNullException.ThrowIfNull(argument: include);
        ArgumentNullException.ThrowIfNull(argument: encoding);

        return BinaryPrimitives.ReadUInt32LittleEndian(source: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: $"{include}\0{encoding}")));
    }
    /// <summary>Returns the interface stamp (<see cref="Puck.Shaders.ShaderInterface.Stamp"/>) of an instruction set:
    /// <c>Isa</c> followed by its fingerprint in eight upper-case hexadecimal digits.</summary>
    /// <param name="fingerprint">The instruction set's fingerprint.</param>
    /// <returns>The stamp.</returns>
    public static string StampOf(uint fingerprint) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"Isa{fingerprint:X8}"
        );

    // Every declaration the field's include makes.
    private static Declarations DeclareField() {
        var declarations = new Declarations(guard: Guard);

        declarations.Members<SdfOp>(prefix: "SDF_OP");
        declarations.Members<SdfShapeType>(prefix: "SDF_SHAPE");
        declarations.Members<SdfBlendOp>(prefix: "SDF_BLEND");
        declarations.Members<SdfLift>(prefix: "SDF_LIFT");
        declarations.Members<SdfNoiseFlavor>(prefix: "SDF_NOISE");
        declarations.Members<SdfCellMode>(prefix: "SDF_CELL_MODE");
        declarations.Members<SdfAxis>(prefix: "SDF_AXIS");
        declarations.Members<SdfPlane>(prefix: "SDF_PLANE");
        declarations.Members<SdfWallpaperGroup>(prefix: "SDF_WPG");
        declarations.Section(title: "The program header, the first vector of the word stream: the vectors before the instruction headers, and each lane's accessor.");
        declarations.Count(name: "SDF_PROGRAM_HEADER_VECTORS", value: SdfProgram.ProgramHeaderVectors);
        declarations.Lane(lane: SdfProgram.ProgramInstructionCountLane, name: "SDF_PROGRAM_INSTRUCTION_COUNT");
        declarations.Lane(lane: SdfProgram.ProgramDataOffsetLane, name: "SDF_PROGRAM_DATA_OFFSET");
        declarations.Section(title: "An instruction: its header's lane accessors, and the data vectors each instruction holds in the data table.");
        declarations.Lane(lane: SdfProgram.InstructionOpLane, name: "SDF_INSTRUCTION_OP");
        declarations.Lane(lane: SdfProgram.InstructionShapeLane, name: "SDF_INSTRUCTION_SHAPE");
        declarations.Lane(lane: SdfProgram.InstructionBlendLane, name: "SDF_INSTRUCTION_BLEND");
        declarations.Lane(lane: SdfProgram.InstructionMaterialLane, name: "SDF_INSTRUCTION_MATERIAL");
        declarations.Count(name: "SDF_INSTRUCTION_DATA_VECTORS", value: SdfProgram.InstructionDataVectors);
        // SdfProgram.BoundsVectorOf: the bound table follows the instruction data.
        declarations.Expression(name: "SDF_PROGRAM_BOUNDS_OFFSET(v)", value: "(SDF_PROGRAM_DATA_OFFSET(v) + (SDF_INSTRUCTION_DATA_VECTORS * SDF_PROGRAM_INSTRUCTION_COUNT(v)))");
        declarations.Section(title: "The bound records and the directories: a record's vectors, a directory's header vectors, and its header's lane accessors.");
        declarations.Count(name: "SDF_BOUND_RECORD_VECTORS", value: SdfProgram.BoundRecordVectors);
        declarations.Count(name: "SDF_DIRECTORY_HEADER_VECTORS", value: SdfProgram.DirectoryHeaderVectors);
        declarations.Lane(lane: SdfProgram.SegmentCountLane, name: "SDF_SEGMENT_COUNT");
        declarations.Lane(lane: SdfProgram.SegmentStepScaleLane, name: "SDF_SEGMENT_STEP_SCALE");
        declarations.Lane(lane: SdfProgram.SegmentRigidPlanLane, name: "SDF_SEGMENT_RIGID_PLAN_OFFSET");
        declarations.Bits(name: "SDF_PART_LEAF_COUNT_MASK", value: SdfProgram.PartLeafCountMask);
        declarations.Count(name: "SDF_PART_DISTANCE_GAIN_SHIFT", value: SdfProgram.PartDistanceGainShift);
        declarations.Bits(name: "SDF_PART_DISTANCE_GAIN_MASK", value: SdfProgram.PartDistanceGainMask);
        declarations.Real(name: "SDF_PART_DISTANCE_GAIN_UNIT", value: SdfProgram.PartDistanceGainUnit);
        declarations.Lane(lane: SdfProgram.InstanceCountLane, name: "SDF_INSTANCE_COUNT");
        declarations.Lane(lane: SdfProgram.InstancePartProgramsLane, name: "SDF_INSTANCE_PART_PROGRAMS");
        declarations.Lane(lane: SdfProgram.WorldSegmentCountLane, name: "SDF_WORLD_SEGMENT_COUNT");
        declarations.Section(title: "Bound modes, and the flags and masks packed beside them in bound and segment records.");
        declarations.Count(
            name: "SDF_BOUND_NONE",
            value: SdfProgram.BoundModeNone
        );
        declarations.Count(
            name: "SDF_BOUND_STATIC",
            value: SdfProgram.BoundModeStatic
        );
        declarations.Count(
            name: "SDF_BOUND_DYNAMIC",
            value: SdfProgram.BoundModeDynamic
        );
        declarations.Count(name: "SDF_BOUND_DYNAMIC_LOCAL", value: SdfProgram.BoundModeDynamicLocal);
        declarations.Bits(
            name: "SDF_SEGMENT_RIGID_PLAN",
            value: SdfProgram.SegmentRigidPlanFlag
        );
        declarations.Bits(
            name: "SDF_SEGMENT_BOUND_MASK",
            value: SdfProgram.SegmentBoundModeMask
        );
        declarations.Section(title: "The rigid-leaf plan.");
        declarations.Bits(
            name: "SDF_RIGID_LEAF_IDENTITY_ROTATION",
            value: SdfProgram.RigidLeafIdentityRotationFlag
        );
        declarations.Bits(
            name: "SDF_RIGID_LEAF_FOLDED",
            value: SdfProgram.RigidLeafFoldedFlag
        );
        declarations.Bits(
            name: "SDF_RIGID_LEAF_SHAPE_MASK",
            value: SdfProgram.RigidLeafShapeMask
        );
        declarations.Count(
            name: "SDF_RIGID_LEAF_MAX_FOLD_RUN",
            value: SdfProgram.RigidLeafMaxFoldRun
        );
        declarations.Section(title: "The sampled-region shape's packed dims.");
        declarations.Bits(
            name: "SDF_SAMPLED_REGION_DIM_MASK",
            value: SdfProgramBuilder.SampledRegionDimMask
        );
        declarations.Section(title: "Program capacities, strides and floors.");
        declarations.Count(
            name: "SDF_MAX_INSTANCES",
            value: SdfProgramBuilder.MaxInstances
        );
        declarations.Count(
            name: "SDF_MAX_FIELD_SCOPE_DEPTH",
            value: SdfProgramBuilder.MaxFieldScopeDepth
        );
        declarations.Real(
            name: "SDF_FLARE_MIN_SCALE",
            value: SdfProgramBuilder.FlareMinScale
        );
        declarations.Real(
            name: "SDF_LANE_ERODE_RAGGED_AMOUNT",
            value: SdfProgramBuilder.LaneErodeRaggedAmount
        );
        declarations.Signed(
            name: "SDF_SCREEN_MATERIAL",
            value: SdfProgramBuilder.ScreenMaterialId
        );
        declarations.Count(name: "SDF_MAX_NOISE_OCTAVES", value: SdfProgramBuilder.MaxNoiseOctaves);
        declarations.Count(name: "SDF_MAX_SWEEP_STRANDS", value: SdfProgramBuilder.MaxSweepStrands);
        declarations.Count(name: "SDF_MAX_CONVEX_VERTICES", value: SdfPrismProfile.MaxConvexVertices);
        declarations.Count(name: "SDF_CONVEX_COUNT_BITS", value: SdfPrismProfile.ConvexCountBits);
        declarations.Count(name: "SDF_MAX_PATH_EDGES", value: SdfPathProfile.MaxEdges);
        declarations.Count(
            name: "SDF_NOISE_PERIOD_CELLS",
            value: SdfVolume.NoisePeriodCells
        );
        declarations.Members<GpuSamplerFilter>(prefix: "SDF_FILTER");
        declarations.Section(title: "The dynamic-transform slot of no slot, and the packed transform-slot word a rigid segment and a part binding store (SdfProgram.PackTransformSlot).");
        declarations.Signed(name: "SDF_TRANSFORM_SLOT_NONE", value: SdfProgram.NoDynamicTransformSlot);
        declarations.Count(name: "SDF_TRANSFORM_SLOT_STATIC_WORD", value: SdfProgram.StaticTransformSlotWord);
        declarations.Expression(name: "SDF_TRANSFORM_SLOT_UNPACK(word)", value: "((int)(word) + SDF_TRANSFORM_SLOT_NONE)");
        declarations.Section(title: "The field's shared constants: the chamfer bevel's scale (SdfProgram.SqrtHalf), the smooth blends' radius floor (SdfProgram.SmoothRadiusMin) and the sweep search (SdfFieldEvaluator).");
        declarations.Real(name: "SDF_SQRT_HALF", value: SdfProgram.SqrtHalf);
        declarations.Real(name: "SDF_SMOOTH_RADIUS_MIN", value: SdfProgram.SmoothRadiusMin);
        declarations.Count(name: "SDF_SWEEP_SAMPLES", value: SdfFieldEvaluator.SweepSamples);
        declarations.Count(name: "SDF_SWEEP_ROUNDS", value: SdfFieldEvaluator.SweepRounds);
        declarations.Count(name: "SDF_ELLIPSE_ROOT_STEPS", value: SdfFieldEvaluator.EllipseRootSteps);
        declarations.Section(title: "The integer hash (Pcg3dLatticeNoise): PCG3D's LCG step, the stream decorrelators, the 3D value noise's seed streams, and the R2 lattice's increments (LowDiscrepancy).");
        declarations.Bits(name: "SDF_PCG_MULTIPLIER", value: Pcg3dLatticeNoise.PcgMultiplier);
        declarations.Bits(name: "SDF_PCG_INCREMENT", value: Pcg3dLatticeNoise.PcgIncrement);
        declarations.Bits(name: "SDF_HASH_STREAM_A", value: Pcg3dLatticeNoise.HashStreamA);
        declarations.Bits(name: "SDF_HASH_STREAM_B", value: Pcg3dLatticeNoise.HashStreamB);
        declarations.Bits(name: "SDF_HASH_TUMBLE", value: Pcg3dLatticeNoise.HashTumble);
        declarations.Bits(name: "SDF_NOISE_SEED_STREAM_Y", value: Pcg3dLatticeNoise.NoiseSeedStreamY);
        declarations.Bits(name: "SDF_NOISE_SEED_STREAM_Z", value: Pcg3dLatticeNoise.NoiseSeedStreamZ);
        declarations.Bits(name: "SDF_R2_ALPHA1", value: LowDiscrepancy.R2AlphaXQ32);
        declarations.Bits(name: "SDF_R2_ALPHA2", value: LowDiscrepancy.R2AlphaYQ32);

        return declarations;
    }

    internal static string DeclareRender() => DeclareRenderDeclarations().Text();
    internal static string DeclareRenderLayout() => DeclareRenderDeclarations().Layout();

    // Every declaration the renderer's include makes, after it includes the field's.
    private static Declarations DeclareRenderDeclarations() {
        var declarations = new Declarations(builtOn: DeclareField(), guard: SdfRenderHlsl.Guard, include: FileName);

        declarations.Count(name: "SDF_PROBE_HIT_COUNT", value: SdfMeshProbeTrace.HitCount);
        declarations.Count(name: "SDF_PROBE_HIT_WORDS", value: SdfMeshProbeTrace.HitWords);
        declarations.Count(name: "SDF_PROBE_UNKNOWN_MATERIAL", value: SdfMeshProbeTrace.UnknownMaterial);
        declarations.Count(name: "SDF_PROBE_NODE_COUNT_BITS", value: SdfMeshProbeTrace.NodeCountBits);
        declarations.Count(name: "SDF_PROBE_NODE_WORDS", value: SdfMeshProbeTrace.NodeWords);
        declarations.Count(name: "SDF_PROBE_ENTRY_WORDS", value: SdfMeshProbeTrace.EntryWords);
        declarations.Count(name: "SDF_PROBE_NORMAL_WORD", value: SdfMeshProbeTrace.NormalWord);
        declarations.Count(name: "SDF_PROBE_GEOMETRY_WORD", value: SdfMeshProbeTrace.GeometryWord);
        declarations.Count(name: "SDF_PROBE_IDENTITY_WORD", value: SdfMeshProbeTrace.IdentityWord);
        declarations.Count(name: "SDF_PROBE_INSTANCE_INDEX_WORD", value: SdfMeshProbeTrace.InstanceIndexWord);
        declarations.Count(name: "SDF_PROBE_STACK_DEPTH", value: SdfMeshProbeTrace.StackDepth);
        declarations.Count(name: "SDF_PROBE_MAX_WALK_STEPS", value: SdfMeshProbeTrace.MaxWalkSteps);
        declarations.Count(name: "SDF_PROBE_GROUP_SIZE", value: SdfMeshProbeTrace.GroupSize);
        declarations.Count(name: "SDF_PROBE_RELIGHT_OUTPUT_WORDS", value: SdfMeshProbeRelight.OutputWords);
        declarations.Count(name: "SDF_PROBE_RELIGHT_INPUT_WORDS", value: SdfMeshProbeRelight.InputWords);
        declarations.Count(name: "SDF_PROBE_RELIGHT_HEADER_ROWS", value: SdfMeshProbeRelight.HeaderRows);
        declarations.Count(name: "SDF_PROBE_RELIGHT_MATERIAL_ROWS", value: SdfMeshProbeRelight.MaterialRows);
        declarations.Count(name: "SDF_PROBE_RELIGHT_LIGHT_ROWS", value: SdfMeshProbeRelight.LightRows);
        declarations.Count(name: "SDF_PROBE_SELECT_INPUT_WORDS", value: SdfMeshProbeRelight.SelectionInputWords);
        declarations.Count(name: "SDF_PROBE_SELECT_HEADER_ROWS", value: SdfMeshProbeRelight.SelectionHeaderRows);
        declarations.Count(name: "SDF_PROBE_CACHE_WORDS", value: SdfMeshProbeRelight.CacheWords);
        declarations.Count(name: "SDF_PROBE_RELIGHT_RESULT_WORDS", value: SdfMeshProbeRelight.ResultWords);
        declarations.Count(name: "SDF_PROBE_METER_OUTPUT_WORDS", value: SdfMeshProbeRelight.MeterOutputWords);
        declarations.Count(name: "SDF_PROBE_RECORD_INPUT_WORD", value: SdfMeshProbeRelight.RecordInputWord);
        declarations.Count(name: "SDF_PROBE_RELIGHT_PREVIOUS_WORD", value: SdfMeshProbeRelight.PreviousWord);
        declarations.Count(name: "SDF_PROBE_SELECT_GROUP_SIZE", value: SdfMeshProbeRelight.SelectGroupSize);
        declarations.Count(name: "SDF_PROBE_RELIGHT_SLICES", value: SdfMeshProbeRelight.Slices);
        declarations.Count(name: "SDF_PROBE_RELIGHT_PARTIAL_WORDS", value: SdfMeshProbeRelight.PartialWords);
        declarations.Count(name: "SDF_PROBE_RELIGHT_PARTIAL_SKY_WORD", value: SdfMeshProbeRelight.PartialSkyWord);
        declarations.Count(name: "SDF_PROBE_RELIGHT_SKY_OUTPUT_WORD", value: SdfMeshProbeRelight.SkyOutputWord);
        declarations.Count(name: "SDF_PROBE_RELIGHT_SKY_ROW", value: SdfMeshProbeRelight.SkyRow);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_CLEARANCE", value: SdfMeshIrradianceProbe.Clearance);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_INSIDE_SHARE", value: SdfMeshIrradianceProbe.InsideShare);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_OPEN_GROUND_SHARE", value: SdfMeshIrradianceProbe.OpenGroundShare);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_METER_COINCIDENT", value: SdfMeshIrradianceProbe.MeterCoincident);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_LIGHT_BIAS", value: SdfMeshIrradianceProbe.LightBias);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_BOX_MARGIN", value: SdfMeshIrradianceProbe.BoxMargin);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_BOX_MARGIN_SHARE", value: SdfMeshIrradianceProbe.BoxMarginShare);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_DEPTH_SHEET_THICKNESS", value: SdfMeshIrradianceProbe.DepthSheetThickness);

        declarations.Members<SdfIndirectSources>(prefix: "SDF_INDIRECT_SOURCES");
        declarations.Section(title: "The appearance section, after every table the field reads (SdfAppearance): the program header's material count and offset, the material and paint strides and the paint limits.");
        declarations.Lane(lane: SdfProgram.ProgramMaterialCountLane, name: "SDF_PROGRAM_MATERIAL_COUNT");
        declarations.Lane(lane: SdfProgram.ProgramMaterialOffsetLane, name: "SDF_PROGRAM_MATERIAL_OFFSET");
        declarations.Count(name: "SDF_MATERIAL_VECTORS_PER_ENTRY", value: SdfAppearance.MaterialVectorsPerEntry);
        declarations.Count(name: "SDF_PAINT_VECTORS_PER_ENTRY", value: SdfAppearance.PaintVectorsPerEntry);
        declarations.Count(name: "SDF_MAX_PAINTS_PER_MATERIAL", value: SdfAppearance.MaxPaintsPerMaterial);
        declarations.Bits(name: "SDF_PAINT_WINNING_FRAME", value: SdfAppearance.PaintWinningFrame);
        declarations.Section(title: "The renderer's capacities, the photometry and the exposure (SdfPhotometry, SdfExposure).");
        declarations.Count(
            name: "SDF_MAX_SCREEN_SURFACES",
            value: SdfProgramBuilder.MaxScreenSurfaces
        );
        declarations.Count(name: "SDF_MAX_LIGHTS", value: SdfLights.MaxLights);
        declarations.Count(name: "SDF_MAX_SHADOW_SLOTS", value: SdfShadowSlots.MaxSlots);
        declarations.Count(name: "SDF_MAX_DISPATCH_GROUPS", value: checked((int)GpuRegion.CopyMaxGroupsPerDimension));
        declarations.Count(name: "SDF_SCREEN_EMISSION_EDGE", value: SdfScreenEmission.Edge);
        declarations.Tuning(name: "SDF_LIGHT_MINIMUM_DISTANCE", value: SdfPhotometry.MinimumDistance);
        declarations.Tuning(name: "SDF_LUMINANCE_CEILING", value: SdfPhotometry.LuminanceCeiling);
        declarations.Real(name: "SDF_WINDOW_ADAPTATION", value: SdfPhotometry.WindowAdaptation);
        declarations.Real(name: "SDF_LUMINANCE_RED", value: SdfPhotometry.LuminanceRed);
        declarations.Real(name: "SDF_LUMINANCE_GREEN", value: SdfPhotometry.LuminanceGreen);
        declarations.Real(name: "SDF_LUMINANCE_BLUE", value: SdfPhotometry.LuminanceBlue);
        declarations.Expression(name: "SDF_LUMINANCE", value: "float3(SDF_LUMINANCE_RED, SDF_LUMINANCE_GREEN, SDF_LUMINANCE_BLUE)");
        declarations.Tuning(name: "SDF_LOCAL_ADAPTATION_RANGE", value: SdfExposure.LocalRange);
        declarations.Tuning(name: "SDF_LOCAL_REFLECTANCE", value: SdfExposure.LocalReflectance);
        declarations.Tuning(name: "SDF_REFERENCE_WHITE", value: SdfExposure.ReferenceWhite);
        declarations.Tuning(name: "SDF_SKY_PEAK_TARGET", value: SdfExposure.SkyPeakTarget);
        declarations.Real(
            name: "SDF_MINIMUM_NEAR",
            value: SdfWorldPackage.MinimumNear
        );
        declarations.Count(name: "SDF_VOLUME_VECTORS_PER_ENTRY", value: SdfVolume.VectorsPerEntry);
        declarations.Members<GridOverlayFlags>(prefix: "SDF_GRID");
        declarations.Members<SdfUpdateRateCode>(prefix: "SDF_RATE");
        declarations.Members<SdfLightKind>(prefix: "SDF_LIGHT");
        declarations.Members<SdfVisibilityKind>(prefix: "SDF_VISIBILITY_KIND");
        declarations.Section(title: "The visibility record's identity fields and the record passes' workgroup edge (SdfVisibility).");
        declarations.Count(name: "SDF_VISIBILITY_KIND_SHIFT", value: SdfVisibility.KindShift);
        declarations.Bits(name: "SDF_VISIBILITY_SOURCE_MASK", value: SdfVisibility.SourceMask);
        declarations.Count(name: "SDF_VISIBILITY_GROUP_EDGE", value: SdfVisibility.GroupEdgePixels);
        declarations.Count(name: "SDF_SHADE_BINS", value: SdfWorldPackage.ShadeBins);
        declarations.Count(name: "SDF_SHADE_TILE_HEADER_WORDS", value: SdfWorldPackage.ShadeTileHeaderWords);
        declarations.Count(name: "SDF_VIEWS_FILTER_WORDS", value: SdfWorldPackage.ViewsFilterWords);
        declarations.Count(name: "SDF_SHADOW_BITS", value: SdfVisibility.ShadowBits);
        declarations.Bits(name: "SDF_SHADOW_MASK", value: SdfVisibility.ShadowMask);
        declarations.Count(name: "SDF_SHADOW_SLOTS", value: SdfShadowSlots.MaxSlots);
        declarations.Count(name: "SDF_SHADOW_FADE_CAPACITY", value: SdfShadowSlots.MaxFadeSlots);
        declarations.Section(title: "The visibility record's normal word (SdfVisibility): its SNORM16 scale and the word of a record with no normal.");
        declarations.Real(name: "SDF_VISIBILITY_NORMAL_SCALE", value: SdfVisibility.NormalScale);
        declarations.Bits(name: "SDF_VISIBILITY_ZERO_NORMAL", value: SdfVisibility.ZeroNormal);
        declarations.Section(title: "The mesh region's draw record (SdfMeshRecord) and the level shift a mesh draw's pushed index carries (SdfKernelInterfaces.MeshLevelShift).");
        declarations.Count(name: "SDF_MESH_DRAW_WORDS", value: SdfMeshRecord.DrawWords);
        declarations.Count(name: "SDF_MESH_MATRIX_WORDS", value: SdfMeshRecord.MatrixWords);
        declarations.Count(name: "SDF_MESH_MATERIAL_WORD", value: SdfMeshRecord.MaterialWord);
        declarations.Count(name: "SDF_MESH_INDEX_WORD", value: SdfMeshRecord.IndexWord);
        declarations.Count(name: "SDF_MESH_INDEX_COUNT_WORD", value: SdfMeshRecord.IndexCountWord);
        declarations.Count(name: "SDF_MESH_VERTEX_WORD", value: SdfMeshRecord.VertexWord);
        declarations.Count(name: "SDF_MESH_FLAGS_WORD", value: SdfMeshRecord.FlagsWord);
        declarations.Count(name: "SDF_MESH_TRIANGLE_MATERIAL_WORD", value: SdfMeshRecord.TriangleMaterialWord);
        declarations.Count(name: "SDF_MESH_NORMAL_MATRIX_WORD", value: SdfMeshRecord.NormalMatrixWord);
        declarations.Count(name: "SDF_MESH_FRAME_SLOT_WORD", value: SdfMeshRecord.FrameSlotWord);
        declarations.Count(name: "SDF_MESH_SKIN_WORD", value: SdfMeshRecord.SkinWord);
        declarations.Count(name: "SDF_MESH_CARRY_ROTATION_WORD", value: SdfMeshRecord.CarryRotationWord);
        declarations.Count(name: "SDF_MESH_CARRY_TRANSLATION_WORD", value: SdfMeshRecord.CarryTranslationWord);
        declarations.Count(name: "SDF_MESH_CARRY_SCALE_WORD", value: SdfMeshRecord.CarryScaleWord);
        declarations.Count(name: "SDF_MESH_CLIP_PLANE_WORD", value: SdfMeshRecord.ClipPlaneWord);
        declarations.Count(name: "SDF_MESH_LEVEL_TABLE_WORD", value: SdfMeshRecord.LevelTableWord);
        declarations.Count(name: "SDF_MESH_SELECTION_WORD", value: SdfMeshRecord.SelectionWord);
        declarations.Count(name: "SDF_MESH_VERTEX_WORDS", value: SdfMeshRecord.VertexWords);
        declarations.Count(name: "SDF_MESH_MEMBER_WORDS", value: SdfMeshRecord.MemberWords);
        declarations.Bits(name: "SDF_MESH_NORMALS_FLAG", value: SdfMeshRecord.NormalsFlag);
        declarations.Bits(name: "SDF_MESH_MATERIALS_FLAG", value: SdfMeshRecord.MaterialsFlag);
        declarations.Bits(name: "SDF_MESH_SKIN_FLAG", value: SdfMeshRecord.SkinFlag);
        declarations.Bits(name: "SDF_MESH_WORLD_MATERIAL_FLAG", value: SdfMeshRecord.WorldMaterialFlag);
        declarations.Count(name: "SDF_MESH_DISPLACEMENT_WORD", value: SdfMeshRecord.DisplacementWord);
        declarations.Section(title: "The frame's sampled table (SdfFrameSampled): a handle with no entry, a height field entry's header, and the reference height its baked reference stands at.");
        declarations.Bits(name: "SDF_FRAME_SAMPLED_NONE", value: SdfFrameSampled.None);
        declarations.Count(name: "SDF_FRAME_SAMPLED_HEADER_WORDS", value: SdfFrameSampled.HeaderWords);
        declarations.Count(name: "SDF_HEIGHT_FIELD_REFERENCE_CELLS", value: SdfFrameSampled.ReferenceCells);
        declarations.Count(name: "SDF_MESH_LEVEL_SHIFT", value: SdfKernelInterfaces.MeshLevelShift);
        declarations.Count(name: "SDF_MESH_SELECTION_GROUP_SIZE", value: SdfKernelInterfaces.MeshSelectionGroupSize);
        declarations.Count(name: "SDF_MESH_SELECTION_GROUPS_PER_ROW", value: SdfKernelInterfaces.MeshSelectionGroupsPerRow);
        declarations.Count(name: "SDF_OCCLUSION_EARLY_STEP", value: SdfKernelInterfaces.MeshOcclusionEarlyStep);
        declarations.Count(name: "SDF_OCCLUSION_DEPTH_STEP", value: SdfKernelInterfaces.MeshOcclusionDepthStep);
        declarations.Count(name: "SDF_OCCLUSION_LATE_STEP", value: SdfKernelInterfaces.MeshOcclusionLateStep);
        declarations.Count(name: "SDF_OCCLUSION_SIGHT_STEP", value: SdfKernelInterfaces.MeshOcclusionSightStep);
        declarations.Count(name: "SDF_RECEIVER_STEP", value: SdfKernelInterfaces.MeshReceiverStep);
        declarations.Count(name: "SDF_RECEIVER_REDUCE_STEP", value: SdfKernelInterfaces.MeshReceiverReduceStep);
        declarations.Count(name: "SDF_RECEIVER_SHIFT", value: SdfKernelInterfaces.MeshReceiverShift);
        declarations.Count(name: "SDF_RECEIVER_ROW", value: SdfKernelInterfaces.MeshReceiverRow);
        declarations.Count(name: "SDF_SIGHT_ROW", value: SdfKernelInterfaces.MeshSightRow);
        declarations.Count(name: "SDF_SIGHT_COUNT_BASE", value: SdfKernelInterfaces.MeshSightCountBase);
        declarations.Section(title: "The irradiance volume's probe record and read (SdfMeshIrradianceProbe).");
        declarations.Count(name: "SDF_MESH_IRRADIANCE_PROBE_WORDS", value: SdfMeshIrradianceProbe.ProbeWords);
        declarations.Count(name: "SDF_MESH_IRRADIANCE_LEVELS", value: SdfMeshIrradianceProbe.Levels);
        declarations.Count(name: "SDF_MESH_IRRADIANCE_SKY_WORD", value: SdfMeshIrradianceProbe.SkyWord);
        declarations.Count(name: "SDF_MESH_IRRADIANCE_RADIANCE_WORD", value: SdfMeshIrradianceProbe.RadianceWord);
        declarations.Count(name: "SDF_MESH_IRRADIANCE_SKY_LIGHT_WORD", value: SdfMeshIrradianceProbe.SkyLightWord);
        declarations.Count(name: "SDF_MESH_IRRADIANCE_LIGHTS_WORD", value: SdfMeshIrradianceProbe.LightsWord);
        declarations.Count(name: "SDF_MESH_IRRADIANCE_LIGHT_WORDS", value: SdfMeshIrradianceProbe.LightWords);
        declarations.Count(name: "SDF_MESH_IRRADIANCE_DEPTH_WORD", value: SdfMeshIrradianceProbe.DepthWord);
        declarations.Count(name: "SDF_MESH_IRRADIANCE_DEPTH_SIZE", value: SdfMeshIrradianceProbe.DepthSize);
        declarations.Count(name: "SDF_MESH_IRRADIANCE_OFFSET_WORD", value: SdfMeshIrradianceProbe.OffsetWord);
        declarations.Bits(name: "SDF_MESH_IRRADIANCE_WRITTEN", value: SdfMeshIrradianceProbe.WrittenFlag);
        declarations.Bits(name: "SDF_MESH_IRRADIANCE_INVALID", value: SdfMeshIrradianceProbe.InvalidFlag);
        declarations.Tuning(name: "SDF_MESH_IRRADIANCE_NORMAL_BIAS", value: SdfMeshIrradianceProbe.NormalBias);
        declarations.Tuning(name: "SDF_MESH_IRRADIANCE_BEHIND_SHARE", value: SdfMeshIrradianceProbe.BehindShare);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_SURFACE_REACH", value: SdfMeshIrradianceProbe.SurfaceReach);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_SURFACE_REACH_FADE_START", value: SdfMeshIrradianceProbe.SurfaceReachFadeStart);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_DEPTH_NORMAL_SCALE", value: SdfMeshIrradianceProbe.DepthNormalScale);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE", value: SdfMeshIrradianceProbe.DepthSurfaceFade);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_DEPTH_SURFACE_JOIN_GAP", value: SdfMeshIrradianceProbe.DepthSurfaceJoinGap);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_DEPTH_COVERAGE_START", value: SdfMeshIrradianceProbe.DepthCoverageStart);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_DEPTH_COVERAGE_SCALE", value: SdfMeshIrradianceProbe.DepthCoverageScale);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_DEPTH_SHEET_COVERAGE_START", value: SdfMeshIrradianceProbe.DepthSheetCoverageStart);
        declarations.Real(name: "SDF_MESH_IRRADIANCE_DEPTH_SHEET_COVERAGE_SCALE", value: SdfMeshIrradianceProbe.DepthSheetCoverageScale);
        declarations.Section(title: "The temporal resolve: the history surface's words an output pixel (SdfWorldPackage.HistorySurfaceWords), the jitter period in samples (SdfWorldPackage.TemporalPeriod), the weight a history saturates at, one period, and the weight one carrying a remembered sub-pixel feature saturates at (SdfTemporalSamples.FeaturePeriods periods).");
        declarations.Count(name: "SDF_HISTORY_SURFACE_WORDS", value: SdfWorldPackage.HistorySurfaceWords);
        declarations.Count(name: "SDF_HISTORY_WINDOW_OFFSET", value: SdfWorldPackage.HistoryWindowOffset);
        declarations.Count(name: "SDF_HISTORY_SAMPLE_WORDS", value: SdfWorldPackage.HistorySampleWords);
        declarations.Count(name: "SDF_TEMPORAL_PERIOD", value: SdfWorldPackage.TemporalPeriod);
        declarations.Tuning(name: "SDF_HISTORY_WEIGHT_CAP", value: SdfWorldPackage.TemporalPeriod);
        declarations.Tuning(name: "SDF_HISTORY_FEATURE_WEIGHT_CAP", value: (SdfWorldPackage.TemporalPeriod * SdfTemporalSamples.FeaturePeriods));
        declarations.Section(title: "The glass sums the glass raster pass accumulates and the glass shading pass reads.");
        declarations.Count(name: "SDF_GLASS_SUM_WORDS", value: SdfWorldPackage.GlassSumWords);
        // Raster encodes these integers and compute decodes them. Raster pipelines are composition-owned and do not
        // hot reload with the compute kernels, so their shared encoding scale must remain in the interface stamp.
        declarations.Real(name: "SDF_GLASS_SUM_SCALE", value: SdfWorldPackage.GlassSumScale);
        declarations.Real(name: "SDF_GLASS_DEPTH_DISTANCE_SCALE", value: SdfWorldPackage.GlassDepthDistanceScale);
        declarations.Real(name: "SDF_GLASS_ABSORPTION_SCALE", value: SdfWorldPackage.GlassAbsorptionScale);
        declarations.Section(title: "The flow layer's clock period in seconds (SdfFlow.ClockPeriodSeconds) and its control word (SdfAppearance.FlowLaneMask, SdfAppearance.FlowWorldFrame, SdfAppearance.FlowCoverageLaneShift).");
        declarations.Real(name: "SDF_FLOW_CLOCK_PERIOD", value: ((float)SdfFlow.ClockPeriodSeconds));
        declarations.Bits(name: "SDF_FLOW_LANE_MASK", value: SdfAppearance.FlowLaneMask);
        declarations.Bits(name: "SDF_FLOW_WORLD_FRAME", value: SdfAppearance.FlowWorldFrame);
        declarations.Count(name: "SDF_FLOW_COVERAGE_LANE_SHIFT", value: SdfAppearance.FlowCoverageLaneShift);
        declarations.Section(title: "The render lanes (SdfLanes): a frame slot's lanes, then the world's (SdfWorldPackage.WorldLanes).");
        declarations.Count(name: "SDF_FIRST_WORLD_LANE", value: SdfLanes.FirstWorld);
        declarations.Count(name: "SDF_LANE_COUNT", value: SdfLanes.Count);
        declarations.Section(title: "The sky's environment map (SdfSkyEnvironment) and the volume table's capacity (SdfProgramBuilder.MaxVolumes).");
        declarations.Signed(name: "SDF_SKY_ENVIRONMENT_SIZE", value: SdfSkyEnvironment.Size);
        declarations.Count(name: "SDF_SKY_ENVIRONMENT_COEFFICIENTS", value: SdfSkyEnvironment.CoefficientCount);
        declarations.Real(name: "SDF_SKY_ENVIRONMENT_MAX_RADIANCE", value: SdfSkyEnvironment.MaxRadiance);
        declarations.Count(name: "SDF_MAX_VOLUMES", value: SdfProgramBuilder.MaxVolumes);
        declarations.Tuning(name: "SDF_VOLUME_SCATTER_ANISOTROPY", value: SdfVolume.ScatterAnisotropy);
        declarations.Section(title: "The surface transport's word (SdfSurfaceTransport) and the atmosphere's guards and flags (SdfAir).");
        declarations.Real(name: "SDF_TRANSPORT_INVERSE_DISTANCE_SCALE", value: SdfSurfaceTransport.InverseDistanceScale);
        declarations.Bits(name: "SDF_TRANSPORT_SAMPLE_BIT", value: SdfSurfaceTransport.SampleBit);
        declarations.Bits(name: "SDF_AIR_FOG_COLOR_AUTHORED", value: SdfAir.FogColorAuthored);
        declarations.Count(name: "SDF_AIR_MAX_LIGHTS", value: SdfAir.MaxLights);
        declarations.Tuning(name: "SDF_AIR_SERIES_LIMIT", value: SdfAir.SeriesLimit);
        declarations.Tuning(name: "SDF_AIR_EXPONENT_LIMIT", value: SdfAir.ExponentLimit);
        declarations.Tuning(name: "SDF_AIR_STEEP_LIMIT", value: SdfAir.SteepLimit);
        declarations.Section(title: "The debug view modes (DebugViewModes): the count, and each mode's index, the value the pass block's debug mode carries.");
        declarations.Signed(name: "SDF_DEBUG_VIEW_COUNT", value: DebugViewModes.Count);

        for (var mode = 0; (mode < DebugViewModes.Count); mode++) {
            declarations.Signed(name: $"SDF_DEBUG_VIEW_{DebugViewModes.Names[mode].Replace(newChar: '_', oldChar: '-').ToUpperInvariant()}", value: mode);
        }

        return declarations;
    }

    // A C# member name's HLSL spelling: upper snake case, with a word break before an upper-case letter that follows a
    // lower-case one, or that starts a new word after an upper-case letter or a digit (P4M stays P4M, LogSphere becomes
    // LOG_SPHERE, F2MinusF1 becomes F2_MINUS_F1).
    internal static string UpperSnake(string name) {
        var text = new StringBuilder(capacity: (name.Length * 2));

        for (var index = 0; (index < name.Length); index++) {
            var character = name[index];

            if (
                (index > 0) &&
                char.IsUpper(c: character) &&
                (char.IsLower(c: name[(index - 1)]) || (
                    (char.IsUpper(c: name[(index - 1)]) || char.IsAsciiDigit(c: name[(index - 1)])) &&
                    ((index + 1) < name.Length) &&
                    char.IsLower(c: name[(index + 1)])
                ))
            ) {
                text.Append(value: '_');
            }

            text.Append(value: char.ToUpperInvariant(c: character));
        }

        return text.ToString();
    }

    // One generated include's declarations: its guard, the include it builds on, and its defines, none named as one the
    // include it builds on declares. A define is stamped (it enters the layout) unless Tuning declared it.
    private sealed class Declarations {
        private readonly string m_guard;

        private readonly List<(string Name, string Value)> m_group = [];
        private readonly StringBuilder m_layout = new();
        private readonly HashSet<string> m_names = new(comparer: StringComparer.Ordinal);

        private readonly HashSet<string> m_reserved;

        private readonly StringBuilder m_text = new();

        public Declarations(string guard, string? include = null, Declarations? builtOn = null) {
            m_guard = guard;
            m_reserved = ((builtOn is null) ? [] : [.. builtOn.m_names]);
            Line(line: "// Generated by `puck shaders generate` from the SDF instruction set's C# model; regenerate it, never edit it.");
            Line(line: $"#ifndef {guard}");
            Line(line: $"#define {guard}");

            if (include is not null) {
                Line(line: $"#include \"{include}\"");
            }
        }

        private void Define(string name, string value, bool tuning = false) {
            var bare = name.Split(separator: '(')[0];

            if (m_reserved.Contains(item: bare) || !m_names.Add(item: bare)) {
                throw new InvalidOperationException(message: $"Two SDF instruction-set declarations are both named {name}.");
            }

            m_group.Add(item: (name, value));

            if (!tuning) {
                m_layout.Append(value: $"#define {name} {value}").Append(value: '\n');
            }
        }
        private void Flush() {
            var width = m_group.Max(selector: static entry => entry.Name.Length);

            foreach (var (name, value) in m_group) {
                m_text.Append(value: $"#define {name.PadRight(totalWidth: width)} {value}").Append(value: '\n');
            }

            m_group.Clear();
        }
        // A group of defines is aligned when the next non-define line ends it.
        private void Line(string line) {
            if (m_group.Count != 0) {
                Flush();
            }

            m_text.Append(value: line).Append(value: '\n');
        }

        public void Bits(string name, uint value) => Define(
            name: name,
            value: $"0x{value.ToString(
                format: "X8",
                provider: CultureInfo.InvariantCulture
            )}u"
        );
        public void Expression(string name, string value) => Define(
            name: name,
            value: value
        );
        public void Lane(string name, int lane) {
            ArgumentOutOfRangeException.ThrowIfNegative(value: lane);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                other: 3,
                value: lane
            );
            Define(
                name: $"{name}(v)",
                value: $"((v).{"xyzw"[lane]})"
            );
        }
        public void Count(string name, long value) {
            ArgumentOutOfRangeException.ThrowIfNegative(value: value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                other: uint.MaxValue,
                value: value
            );
            Define(
                name: name,
                value: $"{value.ToString(provider: CultureInfo.InvariantCulture)}u"
            );
        }
        public void Members<T>(string prefix) where T : struct, Enum {
            Section(title: $"{typeof(T).FullName}.");

            foreach (var member in Enum.GetValues<T>()) {
                Count(
                    name: $"{prefix}_{UpperSnake(name: member.ToString())}",
                    value: Convert.ToInt64(
                        provider: CultureInfo.InvariantCulture,
                        value: member
                    )
                );
            }
        }
        public void Real(string name, float value) => Real(name: name, tuning: false, value: value);
        // A real only the shader reads (a bias, a share, a threshold): in the generated text and in no stamp. The host may
        // mirror it for a diagnostic, but packs and decodes no data with it.
        public void Tuning(string name, float value) => Real(name: name, tuning: true, value: value);

        private void Real(string name, bool tuning, float value) {
            if (!float.IsFinite(f: value)) {
                throw new ArgumentOutOfRangeException(
                    actualValue: value,
                    message: "An HLSL literal must be finite.",
                    paramName: nameof(value)
                );
            }

            var spelling = value.ToString(
                format: "R",
                provider: CultureInfo.InvariantCulture
            );

            var literal = ((spelling.AsSpan().IndexOfAny(values: ".E") < 0)
                ? (spelling + ".0")
                : spelling);

            Define(
                name: name,
                tuning: tuning,
                value: ((value < 0f) ? $"({literal})" : literal)
            );
        }

        public void Section(string title) {
            Line(line: "");
            Line(line: $"// {title}");
        }
        // A negative literal is parenthesized, so a macro use after a minus or a multiply reads as the value.
        public void Signed(string name, int value) => Define(
            name: name,
            value: ((value < 0)
                ? $"({value.ToString(provider: CultureInfo.InvariantCulture)})"
                : value.ToString(provider: CultureInfo.InvariantCulture))
        );
        public string Layout() => m_layout.ToString();
        public string Text() {
            Line(line: "");
            Line(line: $"#endif // {m_guard}");

            return m_text.ToString();
        }
    }
}
