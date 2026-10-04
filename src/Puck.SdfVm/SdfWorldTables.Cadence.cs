using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The cadence: whether a view's latest render stands for the frame the tables hold now. A view stands only when its
// signature equals the one it last rendered at and nothing unhashed forces a render (ForcesRender).
//
// The whole-view signature folds every packed table and the frame block. PassSignature partitions the same inputs:
// geometry owns the field, poses, mesh, screen mappings and camera; shadow owns directions and reach; lighting owns
// colors, surface fog and analytic environment; visual owns sky and media. A generated offset table separates pass-block
// members without spelling HLSL offsets. Temporal sampling is the instance's, and graph versions check last writes.
// Whole-view coverage:
//   - m_programRevision  : the uploaded program (words, live instance-mask width, kernel variant, reseeded screen-surface
//                          table, invariant instance grid) and every kernel reload — bumped by UploadProgram and
//                          InstallReload.
//   - the world values    : the bound screen count and the mesh draws' count.
//   - the pass block      : the view's camera basis, fov/aspect, off-axis offset, far distance and debug view mode, every
//                          lever, the light count, the shadow slots and the curvature shading (SdfFrameBlock), less the
//                          render extent, which the scheduler renders a view again for when it moves.
//   - m_lightRegion, m_skyRegion, m_skyLayerRegion : the lights, sky gains, layer stack and its clock bakes.
//   - shadow owner names : exact names invalidate shadow history even when replacement lights pack identical values.
//   - m_volumeRegion     : the bounded media, whose advection and pulse are baked from the presented tick, so a view
//                          showing one renders again exactly when the presented tick moves it.
//   - m_dynamicTransformRevision : bumped whenever a frame packs an owed dynamic-transform row, so the table is never
//                          re-hashed. Also covers the frame instance grid (a pure function of these transforms + the
//                          program).
//   - m_meshRevision     : the mesh draws the mesh pass rasterizes and primary bounds its march by.
//   - m_screenSurfaceRegion, m_screenMappingRegion : the screen-surface sampling table and the mapping and bound flag
//                          each screen is drawn from.
//   - m_screenLightScratch : the per-screen glow colors.
//   - m_decalRevision    : the glyph-decal buffer — revision-tracked (it is 820 KB, not re-hashed each frame).
// Not covered by any packed span — handled conservatively by forcing a render:
//   - a sky layer sampling a screen (a panorama, a textured disc): the screen's image updates in place like a slab's, so
//                          the sky and the composite run every frame (ForcesPass) and the whole view never stands.
//   - m_programDeclaresScreenSlab : a bound screen's image content updates in place each frame with the same view handle,
//                          unseen by any packed span, so any declared ScreenSlab force-renders.
//   - AnyBrickBaking()  : an in-progress carve bake writing brick voxels each upload.
// The pass block's temporal values (jitter, previous view) and the previous transform tables belong to each instance's
// history, not the tables: SdfWorldPasses.IsUnchanged holds them through SdfTemporalHistory.Stands.
public sealed partial class SdfWorldTables {
    // The atmosphere occupies the packed tail; its flags precede the sky header and are hashed separately.
    private static readonly int AtmosphereOffset = Marshal.OffsetOf<SdfSkyBlock>(fieldName: nameof(SdfSkyBlock.AirLightCount)).ToInt32();

    private readonly byte[] m_signatureBlock = new byte[SdfFrameBlock.SizeBytes];

    private ulong m_tablesSignature;
    private ulong m_geometrySignature;
    private ulong m_lightingSignature;
    private ulong m_shadowSignature;
    private ulong m_visualSignature;

    private static readonly (int Offset, int Length)[] LightingValues = Members(members: [
        SdfWorldPackage.LightCount, SdfWorldPackage.DisableScreenLights,
        SdfWorldPackage.GridFlags, SdfWorldPackage.GridLineWidth, SdfWorldPackage.GridPlaneY,
        SdfWorldPackage.GridWorldPitch, SdfWorldPackage.GridWorldOrigin, SdfWorldPackage.GridWorldFrame,
        SdfWorldPackage.GridObjectOrigin, SdfWorldPackage.GridObjectPitch, SdfWorldPackage.GridObjectFrame,
        SdfWorldPackage.GridObjectPatchRadius,
    ]);
    private static readonly (int Offset, int Length)[] ShadowValues = Members(members: [
        SdfWorldPackage.ShadowSlots, SdfWorldPackage.ShadowSlotCount, SdfWorldPackage.ShadowFadeCount,
        SdfWorldPackage.DisableSoftShadows, SdfWorldPackage.DisableShadowCull, SdfWorldPackage.ShadowDistanceScale,
        SdfWorldPackage.EnableShadowProxy, SdfWorldPackage.CameraTileShadowMask, SdfWorldPackage.FastSoftShadowMarch,
    ]);

    /// <summary>Returns the package-owned inputs of a pass group, independent of temporal sampling and graph versions.
    /// Geometry covers the field and camera, shadow covers selected directions and reach, lighting covers hit shading,
    /// and visual covers the sky and bounded media. The graph also checks each input's last write.</summary>
    /// <param name="frame">The packed frame.</param>
    /// <param name="view">The view index.</param>
    /// <param name="part">The fragment pass name.</param>
    /// <returns>The pass group's input identity.</returns>
    public ulong PassSignature(SdfFrame frame, int view, string part) {
        var block = m_signatureBlock.AsSpan();

        block.Clear();
        SdfFrameBlock.Write(block: block, frame: frame, height: 0, tables: PassValues, view: view, width: 0);
        var lighting = Fnv1aHash.Create();

        lighting.Add(value: m_lightingSignature);
        // Submit records the environment before signatures are read; a sub-code candidate keeps this revision.
        lighting.Add(value: unchecked((ulong)m_skyEnvironment.Renders));
        lighting.Add(value: m_shadowSignature);
        lighting.Add(values: block);
        var shadow = Fnv1aHash.Create();

        shadow.Add(value: m_shadowSignature);
        AddShadowOwners(hash: ref shadow, slots: frame.Lights.ShadowSlots);
        AddMembers(block: block, hash: ref shadow, members: ShadowValues);
        ClearMembers(block: block, members: ShadowValues);
        ClearMembers(block: block, members: LightingValues);
        var geometry = Fnv1aHash.Create();

        geometry.Add(value: m_geometrySignature);
        geometry.Add(values: block);
        var hash = Fnv1aHash.Create();

        hash.Add(value: geometry.Value);
        switch (part) {
            case SdfWorldPackage.Parts.Shadow:
                hash.Add(value: shadow.Value);
                break;
            case SdfWorldPackage.Parts.Views:
            case SdfWorldPackage.Resolve:
                hash.Add(value: lighting.Value);
                if (part == SdfWorldPackage.Resolve) {
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: frame.Views[view].UpscaleSharpness));
                }
                break;
            case SdfWorldPackage.Parts.Sky:
            case SdfWorldPackage.Parts.Composite:
                hash.Add(value: lighting.Value);
                hash.Add(value: m_visualSignature);
                break;
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

    /// <summary>Returns whether the frame the tables hold forces every view to render whatever its signature: a
    /// declared screen slab, whose bound image changes in place, a carve bake in progress, or a sky layer sampling a
    /// screen (<see cref="ForcesPass"/>).</summary>
    /// <param name="frame">The frame the tables packed.</param>
    /// <returns><see langword="true"/> when no view's latest render may stand for the frame.</returns>
    public bool ForcesRender(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        return (ForcesEveryPass(frame: frame) || SkySamplesScreen(sky: frame.Sky));
    }
    /// <summary>Returns whether the frame the tables hold forces one pass group to run whatever its signature: every
    /// group under <see cref="ForcesRender"/>'s whole-view causes, and the sky and the composite while a sky layer (a
    /// panorama, a textured disc) samples a screen, whose bound image changes in place unseen by any packed span.</summary>
    /// <param name="frame">The frame the tables packed.</param>
    /// <param name="part">The fragment pass name.</param>
    /// <returns><see langword="true"/> when the pass's latest output may not stand for the frame.</returns>
    public bool ForcesPass(SdfFrame frame, string part) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        return (
            ForcesEveryPass(frame: frame) ||
            ((part is SdfWorldPackage.Parts.Sky or SdfWorldPackage.Parts.Composite) && SkySamplesScreen(sky: frame.Sky))
        );
    }

    private bool ForcesEveryPass(SdfFrame frame) => (
        !frame.EnableCadenceGate ||
        m_programDeclaresScreenSlab ||
        AnyBrickBaking()
    );
    // Whether any layer of the sky names a screen to sample: a panorama, or a disc with a texture.
    private static bool SkySamplesScreen(SdfSky sky) {
        for (var layer = 0; (layer < sky.LayerCount); layer++) {
            var screen = sky.LayerAt(index: layer).Kind switch {
                SdfSkyLayerKind.Panorama => sky.Parameters<SdfSkyPanorama>(index: layer).Screen,
                SdfSkyLayerKind.Disc => sky.Parameters<SdfSkyDisc>(index: layer).Screen,
                _ => -1,
            };

            if (screen >= 0) { return true; }
        }

        return false;
    }

    /// <summary>Returns the 64-bit FNV-1a signature of what one view renders from in the frame the tables hold: the
    /// tables' signature (<see cref="UpdateTablesSignature"/>) folded with the view's pass block
    /// (<see cref="SdfFrameBlock"/>), written with no render extent. A collision would need a
    /// 64-bit hash clash across two genuinely different input sets — negligible, and still only presentation, never
    /// simulation.</summary>
    /// <param name="frame">The frame the tables packed.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <returns>The signature.</returns>
    public ulong ViewSignature(SdfFrame frame, int view) {
        var hash = Fnv1aHash.Create();
        var block = m_signatureBlock.AsSpan();
        Span<byte> tables = stackalloc byte[sizeof(ulong)];

        block.Clear();
        SdfFrameBlock.Write(
            block: block,
            frame: frame,
            height: 0u,
            tables: PassValues,
            view: view,
            width: 0u
        );
        MemoryMarshal.Write(
            destination: tables,
            value: in m_tablesSignature
        );
        hash.Add(values: tables);
        hash.Add(value: unchecked((ulong)m_skyEnvironment.Renders));
        hash.Add(values: block);
        AddShadowOwners(hash: ref hash, slots: frame.Lights.ShadowSlots);

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
        var hash = Fnv1aHash.Create();

        Span<ulong> values = [
            m_programRevision,
            m_decalRevision,
            m_dynamicTransformRevision,
            unchecked((ulong)m_meshRevision),
            m_meshDrawCount,
            BoundScreenCount(),
        ];

        hash.Add(values: MemoryMarshal.AsBytes(span: values));
        hash.Add(values: m_volumeRegion.Contents);
        hash.Add(values: m_lightRegion.Contents);
        hash.Add(values: m_skyRegion.Contents);
        hash.Add(values: m_skyLayerRegion.Contents);
        hash.Add(values: MemoryMarshal.AsBytes(span: m_shadowHandoffs.AsSpan(length: m_shadowHandoffCount, start: 0)));
        hash.Add(values: m_screenSurfaceRegion.Contents);
        hash.Add(values: m_screenMappingRegion.Contents);
        hash.Add(values: m_screenLightScratch);
        m_tablesSignature = hash.Value;

        var geometry = Fnv1aHash.Create();
        Span<ulong> geometryValues = [m_programRevision, m_dynamicTransformRevision, unchecked((ulong)m_meshRevision), m_meshDrawCount, BoundScreenCount()];

        geometry.Add(values: MemoryMarshal.AsBytes(span: geometryValues));
        geometry.Add(values: m_screenSurfaceRegion.Contents);
        geometry.Add(values: m_screenMappingRegion.Contents);
        m_geometrySignature = geometry.Value;

        var lighting = Fnv1aHash.Create();

        lighting.Add(value: m_decalRevision);
        lighting.Add(values: m_lightRegion.Contents);
        lighting.Add(values: m_screenLightScratch);
        lighting.Add(values: MemoryMarshal.AsBytes(span: m_shadowHandoffs.AsSpan(length: m_shadowHandoffCount, start: 0)));
        ref var sky = ref m_skyRecord[0];
        Span<float> hitSky = [sky.Ambient, sky.Reflection];

        lighting.Add(values: MemoryMarshal.AsBytes(span: hitSky));
        lighting.Add(value: sky.AirFlags);
        lighting.Add(values: MemoryMarshal.AsBytes(span: MemoryMarshal.CreateReadOnlySpan(length: 1, reference: in sky))[AtmosphereOffset..]);
        // Reflections evaluate panels directly, even when their change is below the map's refresh threshold.
        var panels = false;

        foreach (ref readonly var layer in m_skyLayerRecords.AsSpan(length: ((int)sky.LayerCount), start: 0)) {
            if ((layer.Kind != SdfSkyLayerKind.Panel) || ((layer.Visibility & SdfSkyVisibility.Lighting) == 0)) { continue; }
            lighting.Add(values: MemoryMarshal.AsBytes(span: MemoryMarshal.CreateReadOnlySpan(length: 1, reference: in layer)));
            panels = true;
        }
        if (panels) {
            Span<float> axes = [sky.FrameRight.X, sky.FrameRight.Y, sky.FrameRight.Z, sky.FrameUp.X, sky.FrameUp.Y, sky.FrameUp.Z, sky.FrameForward.X, sky.FrameForward.Y, sky.FrameForward.Z];

            lighting.Add(values: MemoryMarshal.AsBytes(span: axes));
        }
        m_lightingSignature = lighting.Value;

        var shadow = Fnv1aHash.Create();

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

        visual.Add(values: m_skyRegion.Contents);
        visual.Add(values: m_skyLayerRegion.Contents);
        visual.Add(values: m_volumeRegion.Contents);
        m_visualSignature = visual.Value;
    }

    // Whether any brick slot is mid-bake: a Baking slot has RecordBrickBakeSlices writing new voxels every upload, so the
    // sampled field the beam/views marches changes with no packed-span change — the cadence must render through it.
    private bool AnyBrickBaking() {
        if (m_brickBakePipeline is null) {
            return false;
        }

        for (var slot = 0; (slot < SdfBrickPoolLayout.MaxBricks); slot++) {
            if (m_brickStates[slot] == BrickBakeState.Baking) {
                return true;
            }
        }

        return false;
    }
    // Whether the program's instruction stream declares any shape of the given type — a one-time UploadProgram walk
    // backing per-program facts (the cadence's ScreenSlab force-render).
    private static bool ProgramDeclaresShape(SdfProgram program, SdfShapeType shapeType) {
        foreach (var instruction in program.Instructions) {
            if (
                (instruction.Op == SdfOp.ShapeBlend) &&
                (((SdfShapeType)instruction.Shape) == shapeType)
            ) {
                return true;
            }
        }

        return false;
    }
}
