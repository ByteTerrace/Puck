using System.IO.Hashing;
using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The cadence: whether a view's latest render stands for the frame the tables hold now. A view stands only when its
// signature equals the one it last rendered at and nothing unhashed forces a render (ForcesRender).
//
// The whole-view signature folds every packed table and the frame block. PassSignature partitions the same inputs:
// geometry owns the field and camera; shadow owns directions and the fitted cascades; lighting owns the screens' tables,
// colors, surface fog and analytic environment; visual owns sky and media. A generated offset table separates pass-block
// members without spelling HLSL offsets. Temporal sampling is the instance's, and graph versions check last writes.
// A pass signs with the revision of its motion scope (SdfMotionScope), which moves only for the draws and slots the pass
// can show, so a body moving where a pass draws nothing leaves that pass standing. The whole view signs with the
// revisions of every scope the tables hold for it, and the static draws' revision the distant map signs with, in place of
// the residency's (m_dynamicTransformRevision, m_meshRevision): every pass of the view signs with one of those scopes or
// with the distant map's own inputs, so no change a pass can show leaves the whole view standing. A view the tables hold
// no scope for yet signs with the residency's revisions.
// Whole-view coverage:
//   - m_programRevision  : changed program words and every kernel reload. The geometry signature uses the separate
//                          m_programGeometryRevision, which excludes only palette values with identical bindings.
//   - the world values    : the bound screen count and the mesh draws' count.
//   - the pass block      : the view's camera basis, fov/aspect, off-axis offset, far distance and debug view mode, every
//                          lever, the light count, the shadow slots and the curvature shading (SdfFrameBlock), less the
//                          render extent, which the scheduler renders a view again for when it moves, and the cascade
//                          set the frame fits for the view, which stands for every input of the fit (InputsOf).
//   - m_lightRegion, m_skyRegion, m_skyLayerRegion : the lights, sky gains, layer stack and its clock bakes.
//   - shadow owner names : exact names invalidate shadow history even when replacement lights pack identical values.
//   - the flow clock     : a lighting value of the pass block, moved by the presented tick only for a program with a
//                          flow layer, so the shading passes render again as the flow moves while the geometry stands.
//   - m_volumeRegion     : the bounded media, whose advection and pulse are baked from the presented tick, so a view
//                          showing one renders again exactly when the presented tick moves it.
//   - m_dynamicTransformRevision : bumped when packed transform contents or the active row count changes, so the table
//                          is never re-hashed. The whole view's alone; a pass signs with its scope's revision.
//   - m_meshRevision     : the mesh draws the mesh and cascade passes rasterize. The whole view's alone, as above.
//   - m_frameSampledRevision : the heights the height fields' draws are displaced by, a geometry value.
//   - m_screenSurfaceRegion, m_screenMappingRegion : the screen-surface sampling table and the mapping and bound flag
//                          each screen is drawn from.
//   - m_decalRevision    : the glyph-decal buffer (the screens' cells and the surface texts' glyphs), revision-tracked
//                          rather than hashed each frame.
//   - m_meshIrradianceRevision : each publication of the irradiance volume, which the device copies into one table
//                          behind the same binding; a lighting value too, so only the shading passes gather it again.
// Not covered by any packed span: a screen's image, which its source updates in place behind the same handle. A pass
// that samples one folds the image's publication into its signature (SdfWorldPasses.ReadSignatureOf); the whole view,
// which sees no publication, renders while any screen shows a source (ForcesRender).
// The pass block's temporal values (jitter, previous view) and the previous transform tables belong to each instance's
// history, not the tables: SdfWorldPasses.IsUnchanged holds them through SdfTemporalHistory.Stands, by the poses of the
// instance's own scope (SdfMotionScope.Poses).
public sealed partial class SdfWorldTables {
    // The atmosphere occupies the packed tail; its flags precede the sky header and are hashed separately.
    private static readonly int AtmosphereOffset = Marshal.OffsetOf<SdfSkyBlock>(fieldName: nameof(SdfSkyBlock.AirLightCount)).ToInt32();

    private readonly byte[] m_signatureBlock = new byte[SdfFrameBlock.SizeBytes];

    private ulong m_tablesSignature;
    // The tables' signature without the residency's poses and draw records, which a view's scopes stand for instead.
    private ulong m_tablesStillSignature;
    private ulong m_geometrySignature;
    private ulong m_lightingSignature;
    private ulong m_shadowSignature;
    // The live program's material wraps (SdfMaterial.Wrap), which the surface pass reads (WrapSignatureOf).
    private ulong m_wrapSignature;
    private ulong m_shadowDensitySignature;
    private ulong m_visualSignature;

    private static readonly (int Offset, int Length)[] CompositeValues = Members(members: [SdfWorldPackage.GeometryOnly]);
    private static readonly (int Offset, int Length)[] LightingValues = Members(members: [
        SdfWorldPackage.ShadePrograms, SdfWorldPackage.ShadeDefault, SdfWorldPackage.ShadeIdentity, SdfWorldPackage.ShadeDemand,
        SdfWorldPackage.ToonOutline, SdfWorldPackage.ToonInk,
        SdfWorldPackage.LightCount, SdfWorldPackage.LightMask, SdfWorldPackage.Exposure, SdfWorldPackage.ExposeShare, SdfWorldPackage.AirLight, SdfWorldPackage.MeterLuminance, SdfWorldPackage.MeterLight, SdfWorldPackage.LocalAdaptation, SdfWorldPackage.SkyPeakLuminance, SdfWorldPackage.FlowClock, SdfWorldPackage.WorldLanes, SdfWorldPackage.DisableScreenLights, SdfWorldPackage.IndirectSources,
        SdfWorldPackage.IndirectSourceGains, SdfWorldPackage.IndirectApply, SdfWorldPackage.IndirectContact,
        SdfWorldPackage.ViewsRate, SdfWorldPackage.GridFlags, SdfWorldPackage.GridLineWidth, SdfWorldPackage.GridPlaneY,
        SdfWorldPackage.GridWorldPitch, SdfWorldPackage.GridWorldOrigin, SdfWorldPackage.GridWorldFrame,
        SdfWorldPackage.GridObjectOrigin, SdfWorldPackage.GridObjectPitch, SdfWorldPackage.GridObjectFrame,
        SdfWorldPackage.GridObjectPatchRadius,
    ]);
    private static readonly (int Offset, int Length)[] ShadowValues = Members(members: [
        SdfWorldPackage.ShadowSlots, SdfWorldPackage.ShadowRoles, SdfWorldPackage.ShadowSlotCount, SdfWorldPackage.ShadowFadeCount,
        SdfWorldPackage.DisableSoftShadows, SdfWorldPackage.MeshShadowCascades, SdfWorldPackage.MeshShadowCount,
        SdfWorldPackage.MeshShadowParameters, SdfWorldPackage.MeshShadowTexels, SdfWorldPackage.MeshShadowSlopes,
        SdfWorldPackage.MeshShadowRoles,
    ]);

    // A view's signature inputs, built once for each frame the view's passes read (InputsOf): the cascade set the frame
    // fits for the view, and its pass block (SdfFrameBlock.Write) with the render extent left out and that cascade set
    // written, folded into the hashes every pass group's signature takes of it. The fit's cameras stand for every input
    // of the fit (the shadow coverage, the distant map's reach and texels, the view's shadow resolution) without naming
    // them, so an input a later fit reads cannot be left out of a signature.
    //
    // A memo stands only while every input the fit and the block read is the one it was built from. The fit reads the
    // frame alone; the block reads the frame, the view and the tables' pass values. FitOf and InputsOf, the one door to
    // the memo, hold each:
    //   - the frame object      : compared by reference on every read.
    //   - the frame's contents  : the frame and the objects it borrows (its view list, its lights and shadow slots) are
    //                             written by its producer's capture, so the epoch moves wherever one can change while
    //                             these tables hold a frame: the residency's frame start and capture (ForgetViewInputs),
    //                             Pack and UpdateTablesSignature.
    //   - the view              : the memo's index, clamped to the frame's views as the fit clamps it.
    //   - the pass values       : compared by value on every read (a record struct, so a value it gains is compared with
    //                             it), the exposures object among them by reference.
    //   - the exposures' values : compared by their revision (SdfViewExposures.Revision), which moves at every stage.
    // Everything else a signature folds (the tables' group signatures, the environment publications, the material wraps,
    // the shading demand, the view's bloom and tone) is read afresh on every call, never memoized.
    private sealed class ViewInputs {
        public readonly byte[] Block = new byte[SdfFrameBlock.SizeBytes];
        public SdfFrame? Frame;
        public long Epoch = -1;
        public SdfMeshShadowCascades Cascades;
        public int Texels;
        public int DistantTexels;
        public bool Built;
        public SdfPassValues Values;
        public long Exposures;
        public ulong Whole;
        public ulong Composite;
        public ulong Lighting;
        public ulong Shadow;
        public ulong Geometry;
        public ulong Owners;
    }

    private ViewInputs?[] m_viewInputs = [];
    private long m_viewInputsEpoch;

    // Forgets every view's signature inputs, so the next signature or cascade set reads the frame afresh: the residency
    // calls it at each frame's start and capture, where the frames these tables render are written.
    internal void ForgetViewInputs() => m_viewInputsEpoch++;

    /// <summary>Returns the cascade set the frame fits for a view (<see cref="SdfMeshShadowCascades.Fit"/>), fitted once a
    /// frame for each view and read by every pass that asks with the view's own texels.</summary>
    /// <param name="frame">The frame the tables hold.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>, clamped to the frame's views.</param>
    /// <param name="texels">The texels along each side of the cascade depths the pass binds, or <see langword="null"/>
    /// for the view's quality.</param>
    /// <param name="distantTexels">The texels along each side of the distant map's depth the pass binds, or
    /// <see langword="null"/> for the frame's distant map at the view's quality.</param>
    /// <returns>The cascade set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public SdfMeshShadowCascades CascadesOf(SdfFrame frame, int view, int? texels = null, int? distantTexels = null) {
        var inputs = FitOf(frame: frame, view: view);

        // A pass binding a depth of another size than the quality's (a graph of the new size building) fits its own.
        return ((((texels ?? inputs.Texels) == inputs.Texels) && ((distantTexels ?? inputs.DistantTexels) == inputs.DistantTexels))
            ? inputs.Cascades
            : SdfMeshShadowCascades.Fit(distantTexels: distantTexels, frame: frame, texels: texels, view: view));
    }
    private ViewInputs FitOf(SdfFrame frame, int view) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var index = Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1));

        if (index >= m_viewInputs.Length) {
            Array.Resize(array: ref m_viewInputs, newSize: Math.Max(val1: (index + 1), val2: (m_viewInputs.Length * 2)));
        }
        var inputs = (m_viewInputs[index] ??= new ViewInputs());

        if (!ReferenceEquals(objA: inputs.Frame, objB: frame) || (inputs.Epoch != m_viewInputsEpoch)) {
            (inputs.Texels, inputs.DistantTexels) = SdfMeshShadowCascades.TexelsFor(frame: frame, view: index);
            inputs.Cascades = SdfMeshShadowCascades.Fit(distantTexels: inputs.DistantTexels, frame: frame, texels: inputs.Texels, view: index);
            inputs.Frame = frame;
            inputs.Epoch = m_viewInputsEpoch;
            inputs.Built = false;
        }

        return inputs;
    }
    private ViewInputs InputsOf(SdfFrame frame, int view) {
        var inputs = FitOf(frame: frame, view: view);
        var values = PassValues;
        var exposures = (values.Exposures?.Revision ?? 0L);

        if (inputs.Built && (inputs.Values == values) && (inputs.Exposures == exposures)) {
            return inputs;
        }

        var block = inputs.Block.AsSpan();

        block.Clear();
        // The draw count stands in a signature only as none or some: each row added or removed is judged by every scope
        // (NoteStagedRow, NoteUnnamedRow), so a list that grows where a pass draws nothing leaves the pass standing.
        SdfFrameBlock.Write(block: block, frame: frame, height: 0u, tables: (values with { MeshDraws = Math.Min(val1: values.MeshDraws, val2: 1u) }), view: Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1)), width: 0u);
        SdfFrameBlock.WriteMeshShadows(block: block, cascades: inputs.Cascades);
        inputs.Whole = Fnv1aHash.Compute(values: block);

        var owners = Fnv1aHash.Create();

        AddShadowOwners(hash: ref owners, slots: frame.Lights.ShadowSlots);
        inputs.Owners = owners.Value;
        // Each group hashes what the groups before it leave of the block: composite, then lighting, then shadow, and
        // geometry what none of them claims.
        var scratch = m_signatureBlock.AsSpan();

        block.CopyTo(destination: scratch);
        var composite = Fnv1aHash.Create();

        AddMembers(block: scratch, hash: ref composite, members: CompositeValues);
        inputs.Composite = composite.Value;
        ClearMembers(block: scratch, members: CompositeValues);
        inputs.Lighting = Fnv1aHash.Compute(values: scratch);
        var shadow = Fnv1aHash.Create();

        AddMembers(block: scratch, hash: ref shadow, members: ShadowValues);
        inputs.Shadow = shadow.Value;
        ClearMembers(block: scratch, members: ShadowValues);
        ClearMembers(block: scratch, members: LightingValues);
        inputs.Geometry = Fnv1aHash.Compute(values: scratch);
        inputs.Values = values;
        inputs.Exposures = exposures;
        inputs.Built = true;

        return inputs;
    }

    /// <summary>Returns the package-owned inputs of a pass group, independent of temporal sampling and graph versions.
    /// Geometry covers the field, the camera and the draws and poses the pass can show, shadow covers selected directions
    /// and reach, lighting covers hit shading, and visual covers the sky and bounded media. The graph also checks each
    /// input's last write.</summary>
    /// <param name="frame">The packed frame.</param>
    /// <param name="view">The view index.</param>
    /// <param name="part">The fragment pass name.</param>
    /// <param name="motion">The revision of the pass's motion scope (<see cref="ObserveMotion(SdfMotionScope, in SdfMeshCull, int)"/>),
    /// which stands for every draw record and transform slot the pass can show.</param>
    /// <param name="scopeLighting">The lighting revision of the pass's motion scope (<see cref="SdfMotionScope.Lighting"/>),
    /// which stands for the screens' emission the pass can show; folded by the passes that shade.</param>
    /// <returns>The pass group's input identity.</returns>
    public ulong PassSignature(SdfFrame frame, int view, string part, long motion, long scopeLighting = 0L) {
        SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.Signatures);
        var inputs = InputsOf(frame: frame, view: view);
        var lighting = Fnv1aHash.Create();

        lighting.Add(value: m_lightingSignature);
        // Submit records the environment before signatures are read; a sub-code candidate keeps this revision. The screens'
        // emission lights the scopes its changed screens reach instead (NoteEmission), whose lighting revision is folded.
        lighting.Add(value: unchecked((ulong)m_skyEnvironment.Publication.Renders));
        lighting.Add(value: unchecked((ulong)scopeLighting));
        lighting.Add(value: m_shadowSignature);
        lighting.Add(value: inputs.Lighting);
        var shadow = Fnv1aHash.Create();

        shadow.Add(value: m_shadowSignature);
        shadow.Add(value: inputs.Owners);
        shadow.Add(value: inputs.Shadow);
        var geometry = Fnv1aHash.Create();

        geometry.Add(value: m_geometrySignature);
        geometry.Add(value: inputs.Geometry);
        geometry.Add(value: unchecked((ulong)motion));
        var hash = Fnv1aHash.Create();

        hash.Add(value: geometry.Value);
        switch (part) {
            // The surface pass writes each pixel's shadow visibility from the cascades, which draw along the shadowed
            // lights.
            case SdfWorldPackage.Parts.Surface:
            case SdfWorldPackage.Parts.ShadowNear:
            case SdfWorldPackage.Parts.ShadowFar:
            case SdfWorldPackage.Parts.ShadowSecond:
                hash.Add(value: shadow.Value);
                // The surface pass queries the cascades wherever a material's wrap carries its light, so a palette's wraps
                // decide which pixels it measures.
                if (part == SdfWorldPackage.Parts.Surface) {
                    hash.Add(value: m_wrapSignature);
                    hash.Add(value: frame.ShadePrograms ? 1u : 0u);
                    if (frame.ShadePrograms && frame.ShadeDefault is { } shade) {
                        hash.AddBits(value: shade.Demand.MaxWrap);
                        hash.AddBits(value: shade.Demand.MaxTiltRadians);
                        hash.Add(value: shade.Demand.EverySide ? 1u : 0u);
                    }
                }
                break;
            // The glass shading pass lights the glass and reads the lit image; the clear and the glass raster are
            // geometry alone, one signature, so a standing raster never accumulates onto a clear it did not see.
            // A binned view's filter and bins passes, and the probe lookup, are views' own work and stand or render with
            // it: the lookup gathers the irradiance volume views would otherwise gather itself.
            case SdfWorldPackage.Parts.ViewsProbes:
            case SdfWorldPackage.Parts.Views:
            case SdfWorldPackage.Parts.ViewsFilter:
            case SdfWorldPackage.Parts.ViewsBins:
            case SdfWorldPackage.Parts.GlassShade:
            case SdfWorldPackage.Resolve:
                hash.Add(value: lighting.Value);
                if (part == SdfWorldPackage.Resolve) {
                    hash.AddBits(value: frame.Views[view].UpscaleSharpness);
                }
                break;
            case SdfWorldPackage.Parts.Sky:
            case SdfWorldPackage.Parts.Composite:
                hash.Add(value: lighting.Value);
                hash.Add(value: m_visualSignature);
                if (part == SdfWorldPackage.Parts.Composite) { hash.Add(value: inputs.Composite); }
                break;
        }
        // The optical tail's own values: the lens's scatter for the pyramid, and for the display pass the sharpen, the
        // fidelity and the world's tone curve as well. Its scene light is an input the graph checks itself.
        if ((part == SdfWorldPackage.Display) || part.StartsWith(value: "bloom-", comparisonType: StringComparison.Ordinal)) {
            var snapshot = frame.Views[view];
            var bloom = (snapshot.Bloom ?? default);

            hash.AddBits(value: bloom.Scatter);
            hash.AddBits(value: bloom.Radius);
            hash.AddBits(value: bloom.Cutoff);
            hash.AddBits(value: snapshot.ScreenFidelity);
            if (part == SdfWorldPackage.Display) {
                hash.Add(value: (snapshot.Quality.Temporal ? BitConverter.SingleToUInt32Bits(value: snapshot.UpscaleSharpness) : 0u));
                hash.AddBits(value: frame.Tone.Peak);
                hash.AddBits(value: frame.Tone.Contrast);
                hash.AddBits(value: frame.Tone.LinearStart);
                hash.AddBits(value: frame.Tone.LinearLength);
                hash.AddBits(value: frame.Tone.Toe);
            }
        }
        return hash.Value;
    }

    private static (int Offset, int Length)[] Members(string[] members) {
        var layout = SdfWorldInterfaces.WorldParameters;

        return [.. members.Select(selector: member => {
            var value = SdfWorldPackage.Values.Single(predicate: value => (value.Name == member));

            return (((int)layout.BlockOffsetOf(member: member)), checked((int)(value.Type!.Value.SizeBytes() * (value.Length ?? 1))));
        })];
    }
    private static void ClearMembers(Span<byte> block, (int Offset, int Length)[] members) {
        foreach (var member in members) { block.Slice(length: member.Length, start: member.Offset).Clear(); }
    }
    private static void AddMembers(ref Fnv1aHash hash, Span<byte> block, (int Offset, int Length)[] members) {
        foreach (var member in members) { hash.Add(values: block.Slice(length: member.Length, start: member.Offset)); }
    }
    /// <summary>Returns whether the frame the tables hold forces every view to render whatever its signature: the cadence
    /// gate is off, or a view samples another instance's image (a screen showing a source, a sky layer's infinity image),
    /// which changes in place behind the same handle. A view's passes identify those images by their publications instead
    /// (<see cref="SdfWorldPasses"/>), which the whole view's signature cannot see.</summary>
    /// <param name="frame">The frame the tables packed.</param>
    /// <returns><see langword="true"/> when no view's latest render may stand for the frame.</returns>
    public bool ForcesRender(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        if (!frame.EnableCadenceGate || (BoundScreenCount() != 0u)) { return true; }
        for (var view = 0; (view < frame.Views.Count); view++) {
            if (frame.Views[view].SkyViews is { Count: > 0 }) { return true; }
        }
        return false;
    }

    /// <summary>Returns the 64-bit FNV-1a signature of what one view renders from in the frame the tables hold: the
    /// tables' signature (<see cref="UpdateTablesSignature"/>) folded with the view's pass block
    /// (<see cref="SdfFrameBlock"/>), written with no render extent and with the cascade set the frame fits for the view
    /// (<see cref="SdfMeshShadowCascades.Fit"/>), and its separate sky field fraction. A collision would need a
    /// 64-bit hash clash across two genuinely different input sets — negligible, and still only presentation, never
    /// simulation.</summary>
    /// <param name="frame">The frame the tables packed.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <returns>The signature.</returns>
    public ulong ViewSignature(SdfFrame frame, int view) {
        SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.Signatures);
        var hash = Fnv1aHash.Create();
        var inputs = InputsOf(frame: frame, view: view);
        var scopes = 0;

        foreach (var scope in m_motionScopes) {
            if (scope.View != view) { continue; }
            hash.Add(value: unchecked((ulong)scope.Revision));
            hash.Add(value: unchecked((ulong)scope.Lighting));
            scopes++;
        }
        hash.Add(value: ((uint)scopes));
        hash.Add(value: ((scopes > 0) ? m_tablesStillSignature : m_tablesSignature));
        hash.Add(value: unchecked((ulong)m_skyEnvironment.Publication.Renders));
        hash.Add(value: unchecked((ulong)m_screenEmission.Publication.Renders));
        hash.Add(value: inputs.Whole);
        hash.AddBits(value: frame.Views[view].Quality.SkyFieldFraction);
        var bloom = frame.Views[view].Bloom;

        hash.AddBits(value: (bloom?.Scatter ?? 0f));
        hash.AddBits(value: (bloom?.Radius ?? 0f));
        hash.AddBits(value: (bloom?.Cutoff ?? 0f));
        hash.Add(value: ((uint)frame.Views[view].BloomQuality));
        hash.Add(value: ((ulong)frame.MeshCull));
        hash.Add(value: ((ulong)frame.MeshDetail));
        hash.Add(value: inputs.Owners);

        return hash.Value;
    }

    private static void AddShadowOwners(ref Fnv1aHash hash, SdfShadowSlots slots) {
        Span<int> ownerLength = stackalloc int[1];

        for (var slot = 0; (slot < SdfShadowSlots.MaxSlots); slot++) {
            var owner = slots.Owner(slot: slot);

            ownerLength[0] = (owner?.Length ?? -1);
            hash.Add(values: MemoryMarshal.AsBytes(span: ownerLength));
            if (owner is not null) {
                hash.Add(values: MemoryMarshal.AsBytes(span: owner.AsSpan()));
            }
        }
    }

    /// <summary>Folds everything every view reads of the tables besides its pass block into the tables' signature, once
    /// the frame is packed and its screens are bound.</summary>
    public void UpdateTablesSignature() {
        ForgetViewInputs();
        // A region's write already compares its bytes with the host copy, so the signatures fold the revision that
        // comparison moves (GpuRegion.ContentRevision) rather than hashing the same bytes again.
        var volumes = unchecked((ulong)m_volumeRegion.ContentRevision);
        var lights = unchecked((ulong)m_lightRegion.ContentRevision);
        var skies = unchecked((ulong)m_skyRegion.ContentRevision);
        var skyLayers = unchecked((ulong)m_skyLayerRegion.ContentRevision);
        var handoffs = XxHash3.HashToUInt64(source: MemoryMarshal.AsBytes(span: m_shadowHandoffs.AsSpan(length: m_shadowHandoffCount, start: 0)));
        var screenSurfaces = unchecked((ulong)m_screenSurfaceRegion.ContentRevision);
        var screenMappings = unchecked((ulong)m_screenMappingRegion.ContentRevision);
        var hash = Fnv1aHash.Create();

        Span<ulong> values = [
            m_programRevision,
            m_decalRevision,
            m_dynamicTransformRevision,
            unchecked((ulong)m_meshRevision),
            m_meshDrawCount,
            m_frameSampledRevision,
            BoundScreenCount(),
            volumes,
            lights,
            skies,
            skyLayers,
            handoffs,
            screenSurfaces,
            screenMappings,
            unchecked((ulong)m_meshIrradianceRevision),
        ];

        hash.Add(values: MemoryMarshal.AsBytes(span: values));
        m_tablesSignature = hash.Value;
        // As a pass signs, the draw count stands as none or some, and the poses and records as the view's scopes.
        values[2] = unchecked((ulong)m_staticDrawRevision);
        values[3] = 0UL;
        values[4] = Math.Min(val1: m_meshDrawCount, val2: 1u);
        m_tablesStillSignature = Fnv1aHash.Compute(values: MemoryMarshal.AsBytes(span: values));

        var geometry = Fnv1aHash.Create();
        Span<ulong> geometryValues = [m_programGeometryRevision, m_frameSampledRevision];

        geometry.Add(values: MemoryMarshal.AsBytes(span: geometryValues));
        m_geometrySignature = geometry.Value;

        var lighting = Fnv1aHash.Create();
        ref var sky = ref m_skyRecord[0];

        lighting.Add(value: m_programRevision);
        lighting.Add(value: m_decalRevision);
        // Only shading reads the screens' tables (their surfaces, mappings, ripples and bound flags), so a ripple's
        // clock or a moving screen's frame renders the shading passes again while the geometry stands.
        lighting.Add(value: BoundScreenCount());
        lighting.Add(value: screenSurfaces);
        lighting.Add(value: screenMappings);
        lighting.Add(value: lights);
        lighting.Add(value: handoffs);
        // The views and glass shading gather the irradiance volume, which a publication rewrites behind one binding.
        lighting.Add(value: unchecked((ulong)m_meshIrradianceRevision));
        Span<float> hitSky = [sky.Ambient, sky.Reflection];

        lighting.Add(values: MemoryMarshal.AsBytes(span: hitSky));
        lighting.Add(value: sky.AirFlags);
        lighting.Add(value: XxHash3.HashToUInt64(source: MemoryMarshal.AsBytes(span: MemoryMarshal.CreateReadOnlySpan(length: 1, reference: in sky))[AtmosphereOffset..]));
        // Reflections evaluate panels directly, even when their change is below the map's refresh threshold.
        var panels = false;

        foreach (ref readonly var layer in m_skyLayerRecords.AsSpan(length: ((int)sky.LayerCount), start: 0)) {
            if ((layer.Kind != SdfSkyLayerKind.Panel) || ((layer.Visibility & SdfSkyVisibility.Lighting) == 0)) { continue; }
            lighting.Add(value: XxHash3.HashToUInt64(source: MemoryMarshal.AsBytes(span: MemoryMarshal.CreateReadOnlySpan(length: 1, reference: in layer))));
            panels = true;
        }
        if (panels) {
            Span<float> axes = [sky.FrameRight.X, sky.FrameRight.Y, sky.FrameRight.Z, sky.FrameUp.X, sky.FrameUp.Y, sky.FrameUp.Z, sky.FrameForward.X, sky.FrameForward.Y, sky.FrameForward.Z];

            lighting.Add(values: MemoryMarshal.AsBytes(span: axes));
        }
        m_lightingSignature = lighting.Value;

        var shadow = Fnv1aHash.Create();

        shadow.Add(value: m_shadowDensitySignature);
        foreach (var light in m_lightRecords) {
            if (!light.CastsShadow) { continue; }
            Span<float> direction = [light.Direction.X, light.Direction.Y, light.Direction.Z, light.Param];

            shadow.Add(values: MemoryMarshal.AsBytes(span: direction));
        }
        foreach (var handoff in m_shadowHandoffs.AsSpan(length: m_shadowHandoffCount, start: 0)) {
            shadow.Add(value: unchecked((uint)handoff.Outgoing));
            shadow.Add(value: unchecked((uint)handoff.Incoming));
            shadow.Add(value: unchecked((uint)handoff.Slot));
        }
        m_shadowSignature = shadow.Value;

        var visual = Fnv1aHash.Create();

        visual.Add(value: skies);
        visual.Add(value: skyLayers);
        visual.Add(value: volumes);
        m_visualSignature = visual.Value;
    }
}
