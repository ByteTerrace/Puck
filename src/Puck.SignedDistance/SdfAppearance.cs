using System.Numerics;

using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>
/// A program's appearance: its material palette, the paints its materials carry and its screen surfaces, validated and
/// packed apart from the field. <see cref="SdfProgram.Of"/> joins one to a field: the field's constructor reads only the
/// appearance's <see cref="Outline"/> (sizes and the material facts that shape a mesh), and the appearance's words go in
/// the stream's tail, after every table the field interpreter reads. So nothing here enters the mesh bake's identity
/// (<c>SdfMeshBakeClosure</c>, rooted at the field constructor), and an edit to a material's values or to this packing
/// leaves every stored mesh standing. Immutable.
/// </summary>
public sealed class SdfAppearance {
    /// <summary>The material float4 stride, written by the packing and read by the kernels' <c>sdfMaterialLoad</c>
    /// (<c>SDF_MATERIAL_VECTORS_PER_ENTRY</c>).</summary>
    public const int MaterialVectorsPerEntry = 34;
    /// <summary>The paint table's float4 stride, read by the views pass's paint layer (<c>SDF_PAINT_VECTORS_PER_ENTRY</c>):
    /// the frame origin and shape, the rotation, the residual scale, the shape's two data vectors, the color and stroke,
    /// the slot and bound center, the bound half-extents, the primitive rotation, with variable side tables stored after the fixed paint entries.</summary>
    public const int PaintVectorsPerEntry = 9;
    /// <summary>The most paints one material carries (<c>SDF_MAX_PAINTS_PER_MATERIAL</c>).</summary>
    public const int MaxPaintsPerMaterial = 8;
    /// <summary>The dynamic slot word of a paint in the frame of the slot that owns the hit
    /// (<c>SDF_PAINT_WINNING_FRAME</c>).</summary>
    public const uint PaintWinningFrame = uint.MaxValue;
    /// <summary>The bits of a flow's control word (material row 30.w) holding its lane plus one, zero for a constant flow
    /// (<c>SDF_FLOW_LANE_MASK</c>).</summary>
    public const uint FlowLaneMask = 0xFFu;
    /// <summary>The bit of a flow's control word saying its velocity is in world space rather than the surface's object
    /// frame (<c>SDF_FLOW_WORLD_FRAME</c>).</summary>
    public const uint FlowWorldFrame = 0x100u;
    /// <summary>The shift of the byte of a flow's control word holding its coverage lane plus one, zero for a constant
    /// coverage (<c>SDF_FLOW_COVERAGE_LANE_SHIFT</c>).</summary>
    public const int FlowCoverageLaneShift = 16;

    private const int WordsPerVector = 4;
    // Each packed screen-surface entry's uvec4 stride: right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad (KEEP IN
    // SYNC with frame/sdf-environment.hlsli's ScreenSurfaceData).
    private const int ScreenSurfaceVectorsPerEntry = 3;
    // The screen frame's admission tolerances. The skew bound is a cosine (~0.057 degrees); the unit bound admits the
    // rounding a quaternion-derived or fixed-point-derived axis carries. KEEP IN SYNC with SdfProgramBuilder's
    // BasisSkewTolerance — the builder refuses the same frame one layer earlier, naming the caller's argument.
    private const float ScreenFrameSkewTolerance = 1.0e-3f;
    private const float ScreenFrameUnitTolerance = 1.0e-3f;

    private readonly SdfMaterial[] m_materials;
    private readonly SdfScreenSurface[] m_screenSurfaces;
    private readonly uint[] m_screenSurfaceWords;

    private (SdfShadeReads Layers, SdfShadeReads Authored, SdfShadeReads Unshaded, SdfShadeReads Emission, bool Inherits)? m_shadeReads;

    /// <summary>The reads of the selected shading path. A default replaces the old layers only on unprogrammed materials.</summary>
    public SdfShadeReads ResolveShadeReads(bool programs, SdfShadeProgram? fallback) {
        if (m_shadeReads is null) {
            var layers = SdfShadeReads.None;
            var authored = SdfShadeReads.None;
            var unshaded = SdfShadeReads.None;
            var emission = SdfShadeReads.None;
            var inherits = false;

            foreach (var material in m_materials) {
                var reads = ((material.Flow is null) ? SdfShadeReads.None : SdfShadeReads.Clock);

                if (IsWorldLane(lane: material.Weathering?.Lane) || IsWorldLane(lane: material.Flow?.Lane) || IsWorldLane(lane: material.Flow?.CoverageLane)) { reads |= SdfShadeReads.WorldLanes; }
                if (IsWorldLane(lane: material.EmissiveLane)) { emission |= SdfShadeReads.WorldLanes; }
                layers |= reads;
                if (material.Shade is { } shade) { authored |= shade.Reads; } else { inherits = true; unshaded |= reads; }
            }
            m_shadeReads = (layers, authored, unshaded, emission, inherits);
        }
        var cached = m_shadeReads.Value;

        return cached.Emission | (programs ? cached.Authored | (cached.Inherits ? (fallback?.Reads ?? cached.Unshaded) : SdfShadeReads.None) : cached.Layers);
    }

    /// <summary>Initializes a new instance of the <see cref="SdfAppearance"/> class from a palette and its screen
    /// surfaces, snapshotted and validated before anything reads them.</summary>
    /// <param name="materials">The material palette, in material-identifier order.</param>
    /// <param name="screenSurfaces">The screen-surface frames screen materials name; none when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The palette reaches the screen sentinels, more than
    /// <see cref="SdfProgramBuilder.MaxScreenSurfaces"/> screen surfaces are declared, or two claim one index.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A material component is out of its domain, or a screen surface's
    /// index, origin, axes or half-extents are.</exception>
    public SdfAppearance(IReadOnlyList<SdfMaterial> materials, IReadOnlyList<SdfScreenSurface>? screenSurfaces = null) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        // Snapshot before reading: a caller's list could answer two reads differently.
        m_materials = Snapshot(materials: materials);
        m_screenSurfaces = [.. (screenSurfaces ?? [])];

        // Ids from SdfProgramBuilder.ScreenMaterialId up decode as screen shading, so a palette reaching that far
        // carries rows no instruction can name (SdfProgramBuilder.AddMaterial refuses the same row at its own door).
        if (m_materials.Length > SdfProgramBuilder.ScreenMaterialId) {
            throw new ArgumentException(
                message: $"A program declares {m_materials.Length} materials, but ids from {SdfProgramBuilder.ScreenMaterialId} up are the screen sentinels, so those rows can never be addressed.",
                paramName: nameof(materials)
            );
        }
        if (m_screenSurfaces.Length > SdfProgramBuilder.MaxScreenSurfaces) {
            throw new ArgumentException(
                message: $"A program may declare at most {SdfProgramBuilder.MaxScreenSurfaces} screen surfaces; got {m_screenSurfaces.Length}.",
                paramName: nameof(screenSurfaces)
            );
        }

        RequirePackedMaterials(materials: m_materials, paramName: nameof(materials));

        var screens = RequireScreenSurfaces(paramName: nameof(screenSurfaces));
        var glass = new bool[m_materials.Length];

        for (var index = 0; (index < glass.Length); index++) {
            glass[index] = m_materials[index].IsGlass;
        }

        Materials = Array.AsReadOnly(array: m_materials);
        ScreenSurfaces = Array.AsReadOnly(array: m_screenSurfaces);
        Outline = new SdfAppearanceOutline(glass: glass, screens: screens, vectors: VectorsOf(materials: m_materials));
        m_screenSurfaceWords = PackScreenSurfaces();
    }

    /// <summary>Gets the immutable material values, in material-identifier order. Nested reveal and paint lists are
    /// snapshotted too; a later caller edit cannot change CPU reference sources.</summary>
    public IReadOnlyList<SdfMaterial> Materials { get; }
    /// <summary>Gets the screen surfaces declared, in declaration order — the source <see cref="ScreenSurfaceWords"/> is
    /// packed from (at each surface's <see cref="SdfScreenSurface.ScreenIndex"/> slot, not its position in this list).
    /// Empty for none.</summary>
    public IReadOnlyList<SdfScreenSurface> ScreenSurfaces { get; }
    /// <summary>Gets the packed screen-surface side table: always <see cref="SdfProgramBuilder.MaxScreenSurfaces"/>
    /// 3-uvec4 (48-byte) entries, each at its declared screen index's slot; an undeclared slot is all-zero and never
    /// addressed by a program-consistent material id.</summary>
    public ReadOnlySpan<uint> ScreenSurfaceWords => m_screenSurfaceWords;
    /// <summary>Gets what a field may know of this appearance (<see cref="SdfAppearanceOutline"/>).</summary>
    public SdfAppearanceOutline Outline { get; }
    /// <summary>Gets the aggregate authored-input reads, derived from admitted programs.</summary>
    public SdfShadeReads ShadeReads => m_materials.Aggregate(SdfShadeReads.None, static (reads, material) => reads | (material.Shade?.Reads ?? SdfShadeReads.None));
    /// <summary>Gets the authored programs carried by this appearance, including different runtime data for one identity.</summary>
    public IEnumerable<SdfShadeProgram> ShadingPrograms => m_materials.Where(predicate: static m => (m.Shade is not null)).Select(selector: static m => m.Shade!);

    /// <summary>Returns the vectors a palette's packed tables span: each material's <see cref="MaterialVectorsPerEntry"/>,
    /// then each paint's <see cref="PaintVectorsPerEntry"/> and its variable side table. The one statement of the appearance section's size, which a
    /// capacity probe (<see cref="SdfProgramBuilder.MeasureCapacity"/>) reads without packing.</summary>
    /// <param name="materials">The palette.</param>
    /// <returns>The vectors.</returns>
    public static int VectorsOf(IReadOnlyList<SdfMaterial> materials) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        var paints = 0;
        var shades = 0;

        foreach (var material in materials) {
            paints += (material.Paints?.Sum(selector: static paint => (PaintVectorsPerEntry + PaintSideVectors(paint: paint))) ?? 0);
            if (material.Shade is { } shade) { shades = checked((shades + ((shade.Words.Length + shade.ParameterWords.Length) / WordsPerVector))); }
        }

        return checked((((MaterialVectorsPerEntry * materials.Count) + paints) + shades));
    }

    private static int PaintSideVectors(SdfPaint paint) => (((paint.Shape.Table.Count + WordsPerVector) - 1) / WordsPerVector);
    private static bool IsWorldLane(int? lane) => (lane >= SdfLanes.FirstWorld);
    private static SdfMaterial[] Snapshot(IReadOnlyList<SdfMaterial> materials) {
        var snapshot = new SdfMaterial[materials.Count];

        for (var index = 0; (index < snapshot.Length); index++) {
            var entry = materials[index];

            if (entry.Weathering is { Under: { } under } weathering) {
                entry = entry with { Weathering = weathering with { Under = Array.AsReadOnly(array: under.ToArray()) } };
            }
            if (entry.Inset is { Paint: { Stops: { } stops } paint } inset) {
                entry = entry with { Inset = inset with { Paint = paint with { Stops = Array.AsReadOnly(array: stops.ToArray()) } } };
            }
            if (entry.Paints is { } paints) {
                entry = entry with { Paints = Array.AsReadOnly(array: paints.ToArray()) };
            }
            snapshot[index] = entry;
        }
        return snapshot;
    }
    // The packed tables are written straight into GPU words, so material values need the same finite/non-negative domain
    // SdfProgramBuilder.AddMaterial enforces: a caller may assemble an appearance directly.
    private static void RequirePackedMaterials(IReadOnlyList<SdfMaterial> materials, string paramName) {
        for (var index = 0; (index < materials.Count); index++) {
            var material = materials[index];
            var bleed = (material.Bleed ?? Vector3.One);

            if (
                !VectorFunctions.IsFinite(vector: material.Albedo) ||
                (material.Albedo.X < 0f) ||
                (material.Albedo.Y < 0f) ||
                (material.Albedo.Z < 0f) ||
                !float.IsFinite(f: material.Emissive) ||
                (material.Emissive < 0f) ||
                !float.IsFinite(f: material.Specular) ||
                (material.Specular < 0f) ||
                !float.IsFinite(f: material.Roughness) ||
                (material.Roughness < 0f) ||
                (material.Roughness > 1f) ||
                !float.IsFinite(f: material.Sheen) ||
                (material.Sheen < 0f) ||
                (material.Sheen > 1f) ||
                !float.IsFinite(f: material.Metal) ||
                (material.Metal < 0f) ||
                (material.Metal > 1f) ||
                !float.IsFinite(f: material.Coat) ||
                (material.Coat < 0f) ||
                (material.Coat > 1f) ||
                !float.IsFinite(f: material.Wrap) ||
                (material.Wrap < 0f) ||
                (material.Wrap > 1f) ||
                !float.IsFinite(f: material.Soften) ||
                (material.Soften < 0f) ||
                (material.Soften > 1f) ||
                !VectorFunctions.IsFinite(vector: material.Fill) ||
                (material.Fill.X < 0f) ||
                (material.Fill.Y < 0f) ||
                (material.Fill.Z < 0f) ||
                !VectorFunctions.IsFinite(vector: bleed) ||
                (bleed.X < 0f) || (bleed.Y < 0f) || (bleed.Z < 0f) ||
                !float.IsFinite(f: material.Receive) || (material.Receive < 0f) ||
                !SdfLanes.IsLane(lane: material.EmissiveLane) ||
                (material.Paints is { Count: > MaxPaintsPerMaterial }) ||
                !material.IsGlassValid ||
                (material.Toon is { Enabled: true, IsValid: false }) ||
                !SdfMaterialLayers.IsValid(
                inset: material.Inset,
                weathering: material.Weathering,
                paints: material.Paints,
                flow: material.Flow
            )
            ) {
                throw new ArgumentOutOfRangeException(
                    message: $"Material {index} must carry finite, non-negative albedo, emissive, specular, fill, bleed and receive, with roughness/sheen/metal/coat/wrap/soften finite in [0, 1], an emissive lane absent or in [0, {(SdfLanes.Count - 1)}], a glass with a positive depth, an index of refraction in [{SdfGlass.MinIor}, {SdfGlass.MaxIor}] and a tint in [0, 1], a cel style within its ranges, and valid material layers; got {material}.",
                    paramName: paramName
                );
            }
        }
    }
    // Refuses a screen surface the shader cannot project onto, or two claiming one index, and returns the declared
    // indices as a mask.
    private uint RequireScreenSurfaces(string paramName) {
        var declared = 0u;

        foreach (var surface in m_screenSurfaces) {
            if ((surface.ScreenIndex < 0) || (surface.ScreenIndex >= SdfProgramBuilder.MaxScreenSurfaces)) {
                throw new ArgumentOutOfRangeException(
                    paramName: paramName,
                    message: $"A screen index must be 0..{(SdfProgramBuilder.MaxScreenSurfaces - 1)}; got {surface.ScreenIndex}."
                );
            }

            var bit = (1u << surface.ScreenIndex);

            if ((declared & bit) != 0u) {
                throw new ArgumentException(
                    message: $"Two screen surfaces declare index {surface.ScreenIndex}. The packed table is indexed BY screen index, so one would silently overwrite the other.",
                    paramName: paramName
                );
            }

            declared |= bit;

            if (!VectorFunctions.IsFinite(vector: surface.Origin)) {
                throw new ArgumentOutOfRangeException(
                    paramName: paramName,
                    message: $"A screen surface's origin must be finite; got {surface.Origin}."
                );
            }

            // sampleScreenSurface resolves the UV as dot(local, right)/right.w and dot(local, up)/up.w, so a zero (or
            // NaN) half-extent maps every hit on a reachable surface to a non-finite UV. KEEP IN SYNC with
            // SdfProgramBuilder's indexed ScreenSlab overload, which refuses the same face naming halfExtents. The
            // slab's DEPTH half-extent is unconstrained here: nothing divides by it.
            if (!float.IsFinite(f: surface.HalfWidth) || !float.IsFinite(f: surface.HalfHeight) || !(surface.HalfWidth > 0f) || !(surface.HalfHeight > 0f)) {
                throw new ArgumentOutOfRangeException(
                    paramName: paramName,
                    message: $"A screen surface's half-width and half-height must be positive; got {surface.HalfWidth} and {surface.HalfHeight}. The shader divides the hit's projection onto each axis by that axis's half-extent."
                );
            }

            // The frame is packed as two independent axes the shader projects a hit point onto, while the slab's own
            // geometry rides the rotation derived from that same pair — so a non-unit or skewed pair packs a UV that
            // does not describe the surface it labels. KEEP IN SYNC with SdfProgramBuilder's RequireOrthogonalBasis.
            var skew = Vector3.Dot(vector1: surface.Right, vector2: surface.Up);

            if (
                !(MathF.Abs(x: (surface.Right.Length() - 1f)) <= ScreenFrameUnitTolerance) ||
                !(MathF.Abs(x: (surface.Up.Length() - 1f)) <= ScreenFrameUnitTolerance) ||
                !(MathF.Abs(x: skew) <= ScreenFrameSkewTolerance)
            ) {
                throw new ArgumentOutOfRangeException(
                    paramName: paramName,
                    message: $"A screen surface's right/up axes must be unit and orthogonal; got |right| {surface.Right.Length()}, |up| {surface.Up.Length()}, dot {skew}."
                );
            }
        }

        return declared;
    }
    // ALWAYS the full MaxScreenSurfaces capacity, indexed by ScreenIndex (not declaration order): the shader resolves a
    // hit's surface with a direct index, no search, so an undeclared slot's all-zero entry is simply never addressed.
    private uint[] PackScreenSurfaces() {
        var words = new uint[((SdfProgramBuilder.MaxScreenSurfaces * ScreenSurfaceVectorsPerEntry) * WordsPerVector)];

        foreach (var surface in m_screenSurfaces) {
            var entryBase = ((surface.ScreenIndex * ScreenSurfaceVectorsPerEntry) * WordsPerVector);

            SdfProgram.WriteVector4(words: words, baseIndex: entryBase, x: surface.Right.X, y: surface.Right.Y, z: surface.Right.Z, w: surface.HalfWidth);
            SdfProgram.WriteVector4(words: words, baseIndex: (entryBase + WordsPerVector), x: surface.Up.X, y: surface.Up.Y, z: surface.Up.Z, w: surface.HalfHeight);
            SdfProgram.WriteVector4(words: words, baseIndex: (entryBase + (2 * WordsPerVector)), x: surface.Origin.X, y: surface.Origin.Y, z: surface.Origin.Z, w: 0f);
        }

        return words;
    }

    // MaterialVectorsPerEntry float4 rows. KEEP IN SYNC with sdfMaterialLoad in shade/sdf-material.hlsli.
    // 0..3 base shading; 4..7 inset frame/ramp controls; 8..11 radial stops;
    // 12..13 weathering controls; 14..17 two reveal surfaces; 18..19 deposit; 26 the pattern lattice's turn;
    // 20 the paints and the emissive lane: (first paint's vector, paint count, emissive lane + 1 or 0 for a static
    // emissive, a glass's contact: its reach in the low half and its coverage in the high, half floats, zero for none) as
    // uint bits; 21 the glass: (absorption per world unit, index of refraction), zero for an opaque material, so a positive
    // index says glass; 31 the glass's medium: (scatter colour, scattering per world unit), zero for none (KEEP IN SYNC
    // with sdfGlassContact and the medium in passes/sdf-glass.comp.hlsl); 22..25 the material's own cel style (SdfToon.WriteRows; KEEP IN SYNC with
    // sdfToonStyle in shade/sdf-toon.hlsli), all zero to inherit the world's default, a mode of -1 for a style that
    // keeps the material continuous. Row 25.w carries glass shadow extinction per world unit, independent of cel ink RGB.
    // Bleed occupies 2.w/7.zw, and indirect receive is 3.w. Weathering pattern XYZ uses
    // 15.w/17.w/19.z; noise coverage is 19.w; reveal softness is 17.z. The second octave's frequency ratio is 15.z and
    // its gain 18.w (zero reads one octave); the turn is row 26 as a unit quaternion (x, y, z, w). A reveal stage is
    // present when its threshold (14.w, 16.w) is positive, and a deposit when the settle share (12.z) is.
    // 27..30 the flow layer (SdfFlow; KEEP IN SYNC with sdfApplyFlow in shade/shade-layers.hlsli): 27 (velocity,
    // pattern scale), 28 (stretch, cycles per clock period, tilt, coverage), 29 (revealed colour, softness), 30 (revealed
    // roughness, revealed metal, seed bits, control bits: FlowControl); all zero for a material with no flow, which a zero
    // scale says.
    /// <summary>Writes the material table at a vector of a program's word stream, then the paints after it, every paint
    /// and side-table reference naming its absolute vector: the stream's appearance section, which follows every table
    /// the field interpreter reads.</summary>
    /// <param name="words">The program's whole word stream.</param>
    /// <param name="materialVector">The vector the material table starts at (the header's material offset).</param>
    internal void Pack(uint[] words, int materialVector) {
        var paintCursor = (materialVector + (MaterialVectorsPerEntry * m_materials.Length));
        var tableCursor = checked((paintCursor + m_materials.Sum(selector: static m => ((m.Paints?.Count ?? 0) * PaintVectorsPerEntry))));
        var shadeCursor = checked((tableCursor + m_materials.Sum(selector: static m => (m.Paints?.Sum(selector: PaintSideVectors) ?? 0))));
        Span<Vector4> toonRows = stackalloc Vector4[4];

        for (var index = 0; (index < m_materials.Length); index++) {
            var m = m_materials[index];
            var bleed = (m.Bleed ?? Vector3.One);
            var materialBase = ((materialVector + (MaterialVectorsPerEntry * index)) * WordsPerVector);

            void Row(int row, Vector4 value) => SdfProgram.WriteVector4(
                baseIndex: (materialBase + (row * WordsPerVector)),
                w: value.W,
                words: words,
                x: value.X,
                y: value.Y,
                z: value.Z
            );
            Row(row: 0, value: new(value: m.Albedo, w: m.Emissive));
            Row(row: 1, value: new(m.Specular, m.Roughness, m.Sheen, m.Metal));
            Row(row: 2, value: new(m.Coat, m.Wrap, m.Soften, bleed.X));
            Row(row: 3, value: new(value: m.Fill, w: m.Receive));
            if (m.Shade is { } shade) {
                var length = (shade.Words.Length / WordsPerVector);

                shade.Words.CopyTo(destination: words.AsSpan(start: (shadeCursor * WordsPerVector)));
                shade.ParameterWords.CopyTo(destination: words.AsSpan(start: ((shadeCursor + length) * WordsPerVector)));
                Row(row: 32, value: new(BitConverter.UInt32BitsToSingle(value: ((uint)shadeCursor)), BitConverter.UInt32BitsToSingle(value: ((uint)length)),
                    BitConverter.UInt32BitsToSingle(value: ((uint)(shadeCursor + length))), BitConverter.UInt32BitsToSingle(value: ((uint)shade.ParameterLayout.Count))));
                Row(row: 33, value: new(shade.Demand.Cosine(baseWrap: m.Wrap), BitConverter.UInt32BitsToSingle(value: ((uint)shade.Reads)), 0, 0));
                shadeCursor = checked(((shadeCursor + length) + (shade.ParameterWords.Length / WordsPerVector)));
            }
            Row(row: 7, value: new Vector4(w: bleed.Z, x: 0, y: 0, z: bleed.Y));
            var paints = (m.Paints ?? []);

            Row(
                row: 20,
                value: new(
                    x: BitConverter.UInt32BitsToSingle(value: ((paints.Count > 0) ? (uint)paintCursor : 0u)),
                    y: BitConverter.UInt32BitsToSingle(value: ((uint)paints.Count)),
                    z: BitConverter.UInt32BitsToSingle(value: ((m.EmissiveLane is { } lane) ? (((uint)lane) + 1u) : 0u)),
                    w: BitConverter.UInt32BitsToSingle(value: ((m.Glass is { HasContact: true } contact)
                        ? BitConverter.HalfToUInt16Bits(value: ((Half)contact.ContactReach)) | (((uint)BitConverter.HalfToUInt16Bits(value: ((Half)contact.ContactCoverage))) << 16)
                        : 0u))
                )
            );
            foreach (var paint in paints) {
                PackPaint(paint: paint, paintVector: paintCursor, tableVector: tableCursor, words: words);
                tableCursor += PaintSideVectors(paint: paint);
                paintCursor += PaintVectorsPerEntry;
            }
            if (m.Toon is { } toon) {
                toon.WriteRows(inkW: (m.Glass?.ShadowDensity ?? 0f), mode: (toon.Enabled ? toon.Mode : -1f), rows: toonRows);
                for (var row = 0; (row < toonRows.Length); row++) {
                    Row(row: (22 + row), value: toonRows[row]);
                }
            }
            if (m.Flow is { } flow) {
                var revealed = (flow.Surface ?? default);

                Row(row: 27, value: new(value: flow.Velocity, w: flow.Scale));
                Row(row: 28, value: new(flow.Stretch, flow.CyclesPerClock, flow.Tilt, flow.Coverage));
                Row(row: 29, value: new(value: revealed.Color, w: flow.Softness));
                Row(
                    row: 30,
                    value: new(
                        revealed.Roughness,
                        revealed.Metal,
                        BitConverter.UInt32BitsToSingle(value: flow.Seed),
                        BitConverter.UInt32BitsToSingle(value: FlowControl(flow: flow))
                    )
                );
            }
            if (m.Glass is { } glass) {
                Row(row: 21, value: new(value: glass.Absorption(tint: m.Albedo), w: glass.Ior));
                Row(row: 31, value: new(value: glass.Scatter, w: glass.Scattering));
                // Row 25.w is independent of the cel ink's RGB; keep the existing table stride.
                if (m.Toon is null) { Row(row: 25, value: new Vector4(0f, 0f, 0f, glass.ShadowDensity)); }
            }
            if (m.Inset is { } inset) {
                var paint = inset.Paint;
                var rotation = Quaternion.Normalize(value: inset.Rotation);

                Row(row: 4, value: new(value: inset.Origin, w: inset.Depth));
                Row(row: 5, value: new(w: rotation.W, x: rotation.X, y: rotation.Y, z: rotation.Z));
                Row(row: 6, value: new(inset.Ior, paint.Stops.Count, paint.Softness, paint.ModulationAmplitude));
                Row(row: 7, value: new(paint.ModulationFrequency, BitConverter.UInt32BitsToSingle(value: paint.Seed), bleed.Y, bleed.Z));
                for (var stop = 0; (stop < paint.Stops.Count); stop++) {
                    Row(row: (8 + stop), value: new(value: paint.Stops[stop].Color, w: paint.Stops[stop].Radius));
                }
            }
            if (m.Weathering is { } w) {
                Row(row: 12, value: new(w.Edge, w.Lines, w.Settle, w.Reach));
                Row(row: 13, value: new(BitConverter.UInt32BitsToSingle(value: w.Seed), w.Scale, w.Floor, w.Lane));
                if (w.Under is { } stages) {
                    for (var stage = 0; (stage < stages.Count); stage++) {
                        Row(row: (14 + (2 * stage)), value: new(value: stages[stage].Surface.Color, w: stages[stage].Threshold));
                        Row(row: (15 + (2 * stage)), value: new(stages[stage].Surface.Roughness, stages[stage].Surface.Metal, 0, 0));
                    }
                }
                if (w.Deposit is { } deposit) {
                    Row(row: 18, value: new(value: deposit.Color, w: 0));
                    Row(row: 19, value: new(deposit.Roughness, deposit.Metal, 0, 0));
                }
                // Pattern XYZ and noise coverage use existing reserved components; material stride stays fixed.
                var pattern = (w.PatternScale ?? Vector3.One);

                words[((materialBase + (15 * WordsPerVector)) + 3)] = BitConverter.SingleToUInt32Bits(value: pattern.X);
                words[((materialBase + (17 * WordsPerVector)) + 3)] = BitConverter.SingleToUInt32Bits(value: pattern.Y);
                words[((materialBase + (19 * WordsPerVector)) + 2)] = BitConverter.SingleToUInt32Bits(value: pattern.Z);
                words[((materialBase + (19 * WordsPerVector)) + 3)] = BitConverter.SingleToUInt32Bits(value: w.Noise);
                words[((materialBase + (17 * WordsPerVector)) + 2)] = BitConverter.SingleToUInt32Bits(value: w.Softness);
                // A second octave exists when the gain word is positive; a single octave leaves both words zero.
                if (w.Octaves >= 2) {
                    words[((materialBase + (15 * WordsPerVector)) + 2)] = BitConverter.SingleToUInt32Bits(value: w.OctaveRatio);
                    words[((materialBase + (18 * WordsPerVector)) + 3)] = BitConverter.SingleToUInt32Bits(value: w.OctaveGain);
                }
                var turn = Quaternion.Normalize(value: (w.Turn ?? Quaternion.Identity));

                Row(row: 26, value: new(w: turn.W, x: turn.X, y: turn.Y, z: turn.Z));
            }
        }
    }

    // A flow's control word (row 30.w): its velocity lane plus one, or zero for a constant flow, under FlowLaneMask;
    // FlowWorldFrame for a velocity authored in world space; its coverage lane plus one, or zero, at
    // FlowCoverageLaneShift.
    private static uint FlowControl(SdfFlow flow) =>
        LanePlusOne(lane: flow.Lane) | ((flow.Frame == SdfFlowFrame.World) ? FlowWorldFrame : 0u) |
        (LanePlusOne(lane: flow.CoverageLane) << FlowCoverageLaneShift);
    private static uint LanePlusOne(int? lane) => ((lane is { } index) ? (((uint)index) + 1u) : 0u);
    // PaintVectorsPerEntry float4 rows. KEEP IN SYNC with sdfApplyPaints in shade/shade-layers.hlsli.
    // 0 frame origin, shape type (uint bits); 1 frame rotation; 2 residual scale, its smallest component;
    // 3..4 the shape's data vectors, a side-table shape's absolute offset; 5 color, stroke;
    // 6 (dynamic slot as uint bits, bound center); 7 (bound half-extents, infinite when unbounded, 0);
    // 8 primitive rotation. Side tables follow all fixed paint entries.
    private static void PackPaint(SdfPaint paint, int paintVector, int tableVector, uint[] words) {
        var shape = paint.Shape;
        var data0 = shape.Data0At(tableVector: tableVector);
        var rotation = Quaternion.Normalize(value: paint.Rotation);
        var minimumScale = MathF.Min(x: shape.Scale.X, y: MathF.Min(x: shape.Scale.Y, y: shape.Scale.Z));

        void Row(int row, Vector4 value) => SdfProgram.WriteVector4(
            baseIndex: ((paintVector + row) * WordsPerVector),
            w: value.W,
            words: words,
            x: value.X,
            y: value.Y,
            z: value.Z
        );
        Row(row: 0, value: new(value: paint.Origin, w: BitConverter.UInt32BitsToSingle(value: ((uint)shape.Type))));
        Row(row: 1, value: new(w: rotation.W, x: rotation.X, y: rotation.Y, z: rotation.Z));
        Row(row: 2, value: new(value: shape.Scale, w: minimumScale));
        Row(row: 3, value: data0);
        Row(row: 4, value: shape.Data1);
        Row(row: 5, value: new(value: paint.Color, w: paint.Stroke));
        Row(row: 6, value: new(
            x: BitConverter.UInt32BitsToSingle(value: ((paint.Slot == SdfPaint.WinningFrame) ? PaintWinningFrame : (uint)paint.Slot)),
            y: shape.Bound.Center.X,
            z: shape.Bound.Center.Y,
            w: shape.Bound.Center.Z
        ));
        Row(row: 7, value: new(value: (shape.Bound.IsUnbounded ? new Vector3(value: float.PositiveInfinity) : shape.Bound.HalfExtents), w: 0f));
        Row(row: 8, value: new(shape.Rotation.X, shape.Rotation.Y, shape.Rotation.Z, shape.Rotation.W));
        for (var index = 0; (index < shape.Table.Count); index++) {
            words[((tableVector * WordsPerVector) + index)] = BitConverter.SingleToUInt32Bits(value: shape.Table[index]);
        }
    }
}
/// <summary>
/// What a program's field derivation may know of its appearance (<see cref="SdfAppearance.Outline"/>): the material
/// count, the vectors the appearance section spans, which materials are glass (a glass shape bakes into a group of its
/// own) and which screen indices are declared. The field constructor validates material and screen references against
/// it and sizes the stream by it; it holds no material value, so no appearance value or packing code reaches the field
/// or the mesh bake's identity through it. Immutable.
/// </summary>
public readonly struct SdfAppearanceOutline {
    private readonly bool[]? m_glass;

    internal SdfAppearanceOutline(bool[] glass, uint screens, int vectors) {
        m_glass = glass;
        Screens = screens;
        Vectors = vectors;
    }

    /// <summary>Gets the number of materials in the palette.</summary>
    public int Materials => (m_glass?.Length ?? 0);
    /// <summary>Gets the vectors the appearance section spans (<see cref="SdfAppearance.VectorsOf"/>).</summary>
    public int Vectors { get; }
    /// <summary>Gets the declared screen indices, a bit an index.</summary>
    public uint Screens { get; }

    /// <summary>Returns whether a material id names a glass material; a screen sentinel or an id past the palette is
    /// opaque.</summary>
    /// <param name="material">The material id.</param>
    /// <returns><see langword="true"/> for a glass material.</returns>
    public bool IsGlass(int material) => ((m_glass is { } glass) && (material >= 0) && (material < glass.Length) && glass[material]);
    /// <summary>Returns whether a screen index is declared.</summary>
    /// <param name="screenIndex">The screen index.</param>
    /// <returns><see langword="true"/> when a screen surface declares it.</returns>
    public bool DeclaresScreen(int screenIndex) => ((screenIndex >= 0) && (screenIndex < SdfProgramBuilder.MaxScreenSurfaces) && ((Screens & (1u << screenIndex)) != 0u));
}
