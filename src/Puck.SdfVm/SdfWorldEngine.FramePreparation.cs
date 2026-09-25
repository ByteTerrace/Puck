using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // (Re)bind the MaxScreenSurfaces screen-source bindings (Stage 1 only) — a slot with no host-supplied source this
    // frame duplicates the DEDICATED ShaderReadOnly filler (m_screenSourceFiller; NOT the sources[] filler BindSources
    // uses, which lives in the General/UAV layout Stage 1/2 read/write it in — aliasing that here would violate the
    // combined-image-sampler binding's required layout the instant any viewport-source dispatch ran). The shader
    // never samples an unbound slot (params.screenMask gates it), so the filler's content never reaches a pixel. Each
    // is a SCALAR binding, not one array (see ScreenSourceBindingIndices), so each is written at arrayElement 0; the
    // change-detected rebind means an idle scene (no sources bound) only writes descriptors that actually changed.
    //
    // THE HANDLE-IDENTITY RULE: a change-detected skip is sound ONLY for a view this engine's own lifetime covers. A
    // HOST-SUPPLIED handle (a screen source, a child's storage image) names an object the host may destroy and replace
    // between any two frames, and a handle value is unique only among LIVE objects — both backends recycle a retired
    // one. Direct3D 12 mints the token as a GCHandle whose freed table slot the next Alloc reuses (measured: three
    // successive QR authorings on one screen produced three different ID3D12Resources behind ONE token value);
    // Vulkan hands back the driver's VkImageView, which a driver is equally free to re-issue after vkDestroyImageView.
    // Skipping the write on a matching value therefore leaves this set's descriptor pointing at the RETIRED resource
    // for the rest of the run, and the next sample of it removes the device. So: rewrite host-owned bindings every
    // frame (a handful of descriptor writes per frame — the engine-owned filler and glyph atlas keep the skip).
    private void BindScreenSources() {
        var fillerView = m_screenSourceFiller.ImageViewHandle;
        var boundViews = m_boundScreenSourceViews[m_currentSlot];
        var viewsSet = m_viewsSets[m_currentSlot];

        for (var element = 0u; (element < MaxScreenSurfaces); element++) {
            var hostView = m_screenSourceViews[element];
            var view = ((0 != hostView)
                ? hostView
                : fillerView
            );

            if (
                (0 == hostView) &&
                (view == boundViews[element])
            ) {
                continue;
            }

            m_bindings.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: ScreenSourceBindingIndices[((int)element)],
                descriptorSetHandle: viewsSet,
                imageViewHandle: view,
                samplerHandle: m_screenSampler
            );
            boundViews[element] = view;
        }

        // The glyph atlas rides the same ShaderReadOnly filler when unset, and the same change-detected rebind. It is
        // static, so this normally writes once per ring slot (the atlas view, or the filler) and then no-ops every
        // later frame.
        var glyphView = ((0 != m_glyphAtlasView)
            ? m_glyphAtlasView
            : fillerView
        );

        if (glyphView != m_boundGlyphAtlasViews[m_currentSlot]) {
            m_bindings.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: GlyphAtlasBindingIndex,
                descriptorSetHandle: viewsSet,
                imageViewHandle: glyphView,
                samplerHandle: m_screenSampler
            );
            m_boundGlyphAtlasViews[m_currentSlot] = glyphView;
        }
    }
    // Bind (or rebind when a child's image-view changed) the source array in both the CURRENT ring slot's Stage 1
    // (views) and Stage 2 (composite) sets: an SDF source texture for a normal slot, the hosted child's storage image
    // for a child slot. Array elements past the live viewport count duplicate slot 0 (Vulkan requires every bound
    // array element to be a valid descriptor); the kernels never read them. The change-detected cache is per ring
    // slot (a slot's set is only rewritten after its fence proved the slot idle) and covers ENGINE-OWNED views only —
    // a child slot's view is host-owned and is rewritten unconditionally (see BindScreenSources for why).
    private void BindSources(uint viewportCount) {
        const int FillerSlot = 0;

        var fillerView = SourceViewForSlot(slot: FillerSlot);
        var fillerIsHostOwned = IsChildSlot(slot: FillerSlot);
        var boundViews = m_boundSourceViews[m_currentSlot];

        for (var element = 0u; (element < MaxViewports); element++) {
            var live = (element < viewportCount);
            var view = (live
                ? SourceViewForSlot(slot: ((int)element))
                : fillerView
            );
            var hostOwned = (live
                ? IsChildSlot(slot: ((int)element))
                : fillerIsHostOwned
            );

            if (
                !hostOwned &&
                (view == boundViews[element])
            ) {
                continue;
            }

            m_bindings.WriteStorageImage(
                arrayElement: element,
                binding: ViewSourceBindingIndex,
                descriptorSetHandle: m_viewsSets[m_currentSlot],
                imageViewHandle: view
            );
            m_bindings.WriteStorageImage(
                arrayElement: element,
                binding: CompositeSourceBindingIndex,
                descriptorSetHandle: m_compositeSets[m_currentSlot],
                imageViewHandle: view
            );
            boundViews[element] = view;
        }
    }
    // Stage 2's CompositeParams2 { uint2 imageExtent; uint viewportCount; uint childMask; float4 rects[5]; uint2 scaleQPacked;
    // uint2 sharpnessQPacked; }: the LIVE regions drive the layout every frame. word[3] carries the child mask (using the former HLSL
    // cbuffer padding ahead of the float4 rects array — KEEP IN SYNC with sdf-world-composite.comp.hlsl's struct);
    // the final four words carry the byte-packed per-view controls.
    private void BuildCompositePush(SdfFrame frame) {
        var words = MemoryMarshal.Cast<byte, uint>(span: m_compositePush.AsSpan());

        words[0] = m_width; words[1] = m_height; words[2] = ((uint)frame.Views.Count); words[3] = m_childMask;

        var floats = MemoryMarshal.Cast<byte, float>(span: m_compositePush.AsSpan());

        for (var index = 0; (index < frame.Views.Count); index++) {
            var region = frame.Views[index].Region;
            var b = (4 + (index * 4));

            floats[(b + 0)] = region.X; floats[(b + 1)] = region.Y; floats[(b + 2)] = region.Width; floats[(b + 3)] = region.Height;
        }

        // scaleQPacked (after rects): view v's quantized render-scale numerator in byte lane (v % 4) of word (v / 4) —
        // the SAME RenderScaleQ the viewport row carries, so Stage 2's upsample derivation matches Stage 1's render.
        // Unpacked slots stay q = 255 (native) so a stale lane can never scale a live view.
        var qBase = (4 + (MaxViewports * 4));

        words[(qBase + 0)] = 0xFFFFFFFFu; words[(qBase + 1)] = 0xFFFFFFFFu;

        for (var index = 0; (index < frame.Views.Count); index++) {
            var word = (qBase + (index / 4));
            var shift = ((index % 4) * 8);

            words[word] = (words[word] & ~(0xFFu << shift)) | (((uint)RenderScaleQ(
                view: frame.Views[index],
                slot: index
            )) << shift);
        }

        // sharpnessQPacked follows scaleQPacked with the same five-view byte-lane layout. Zero is bilinear and retains
        // the existing four-tap path; nonzero blends toward clamped Catmull-Rom. Unused lanes stay zero.
        var sharpnessBase = (qBase + 2);

        words[(sharpnessBase + 0)] = 0u; words[(sharpnessBase + 1)] = 0u;

        for (var index = 0; (index < frame.Views.Count); index++) {
            var word = (sharpnessBase + (index / 4));
            var shift = ((index % 4) * 8);

            words[word] |= (((uint)UpscaleSharpnessQ(view: frame.Views[index])) << shift);
        }
    }
    private bool IsChildSlot(int slot) =>
        (0u != (m_childMask & (1u << slot)));
    // Packs the rows the frame's moved set owes into the dynamic-transform mirror — 3 float4 per slot: position.xyz
    // (+ shadow participation), the orientation quaternion (xyzw), then the lanes — for the device-local table
    // SDF_OP_TRANSFORM_DYNAMIC indexes by slot, and owes each packed row an upload. The owed rows are the ones the
    // producer's SdfMovedTransforms recorded since the last frame this engine consumed from it; a frame with no moved
    // set declares its table static and owes nothing past the first frame that carries that table. Every row is owed
    // on this engine's first frame, for a frame from another producer or another static table, and when the last
    // consumed frame has left the producer's history; nothing compares the table against the mirror. An empty list is
    // only valid for a program with no dynamic slots (PrepareFrame throws otherwise); it still packs the one
    // always-present slot as identity so the binding stays valid. Clamped to the slot capacity the construction
    // options grew the buffer to. Returns whether any row was packed.
    private bool PackDynamicTransforms(SdfFrame frame) {
        var mirror = MemoryMarshal.Cast<byte, float>(span: m_dynamicTransformScratch.AsSpan());
        var transforms = frame.DynamicTransforms;
        var moved = frame.MovedTransforms;
        var count = Math.Min(
            val1: transforms.Count,
            val2: m_dynamicTransformCapacity
        );
        var everything = (
            (m_dynamicTransformSlotsResident == 0) ||
            !ReferenceEquals(
            objA: moved,
            objB: m_movedTransformsSource
        ) ||
            ((moved is null)
                ? !ReferenceEquals(
                    objA: transforms,
                    objB: m_movedTransformsTable
                )
                : !moved.TryCollect(
                    into: m_owedTransforms,
                    since: m_movedTransformsSerial
                ))
        );

        if (moved is null) {
            m_owedTransforms.Clear();
        }

        m_movedTransformsSource = moved;
        m_movedTransformsSerial = (moved?.Serial ?? 0L);
        m_movedTransformsTable = transforms;

        Span<float> floats = stackalloc float[DynamicTransformWordCount];

        if (everything) {
            // One run owes the whole table, so each row only needs its mirror copy.
            m_tableUploads[DynamicTransformTable].Add(
                length: (m_dynamicTransformCapacity * DynamicTransformWordCount),
                start: 0
            );

            if (count == 0) {
                floats.Clear();
                floats[7] = 1f; // identity quaternion
                floats.CopyTo(destination: mirror[..DynamicTransformWordCount]);
            }

            for (var index = 0; (index < count); index++) {
                PackDynamicTransform(
                    floats: floats,
                    transform: transforms[index]
                );
                floats.CopyTo(destination: mirror.Slice(
                    length: DynamicTransformWordCount,
                    start: (index * DynamicTransformWordCount)
                ));
            }

            m_dynamicTransformSlotsResident = m_dynamicTransformCapacity;
            m_dynamicTransformRevision++;

            return true;
        }

        var packed = false;

        for (var run = 0; (run < m_owedTransforms.Count); run++) {
            var start = m_owedTransforms.Start(index: run);
            var end = Math.Min(
                val1: (start + m_owedTransforms.Length(index: run)),
                val2: count
            );

            for (var index = start; (index < end); index++) {
                PackDynamicTransform(
                    floats: floats,
                    transform: transforms[index]
                );
                StageTableRow(
                    entry: index,
                    mirror: mirror,
                    packed: floats,
                    table: DynamicTransformTable
                );
                packed = true;
            }
        }

        if (packed) {
            m_dynamicTransformRevision++;
        }

        return packed;
    }
    // position.w encodes per-instance soft-shadow participation: 0 = casts, 1 = shadow-suppressed (skipped by the
    // soft-shadow march only), read by sdf-world.hlsli's sdfShadowParticipationActive skip. The lanes row is what an op
    // evaluating under this slot (SDF_OP_LANE_ERODE, shade-volumes.hlsli's selected intensity lane) reads through
    // sdfDynamicTransforms[(3*slot)+2]; a shape under no slot reads zero.
    private static void PackDynamicTransform(Span<float> floats, in DynamicTransform transform) {
        floats[0] = transform.Position.X; floats[1] = transform.Position.Y; floats[2] = transform.Position.Z; floats[3] = (transform.CastsSoftShadow
            ? 0f
            : 1f
        );
        floats[4] = transform.Orientation.X; floats[5] = transform.Orientation.Y; floats[6] = transform.Orientation.Z; floats[7] = transform.Orientation.W;
        floats[8] = transform.Lanes.X; floats[9] = transform.Lanes.Y; floats[10] = transform.Lanes.Z; floats[11] = transform.Lanes.W;
    }
    // Pack the per-frame screen-light buffer: entries 0..(MaxScreenSurfaces-1) = each screen's emitted color (the
    // framebuffer average set via SetScreenLight) with the room-glow intensity gain in w, the last entry = the
    // environment (ambient/sun dimming from the frame). KEEP IN SYNC with sdf-world.hlsli's sdfScreenLights layout
    // (SdfScreenLightEnv must equal MaxScreenSurfaces there).
    private void PackScreenLights(SdfFrame frame) {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_screenLightScratch.AsSpan());

        for (var index = 0; (index < MaxScreenSurfaces); index++) {
            var color = m_screenLightColors[index];
            var b = (index * 4);

            floats[(b + 0)] = color.X; floats[(b + 1)] = color.Y; floats[(b + 2)] = color.Z; floats[(b + 3)] = ScreenLightIntensity;
        }

        var envBase = (MaxScreenSurfaces * 4);

        // The env entry's zw lanes carry the SLICE debug view's plane selector (axis + offset — see
        // SdfFrame.DebugSliceAxis); they were spare pads before, so a frame that never sets them uploads the same zeros.
        floats[(envBase + 0)] = frame.AmbientScale; floats[(envBase + 1)] = frame.SunScale; floats[(envBase + 2)] = frame.DebugSliceAxis; floats[(envBase + 3)] = frame.DebugSliceOffset;

        // The grid-lock overlay rows (grid-locking §4a): four float4 rows AFTER the env entry (env stays at
        // MaxScreenSurfaces, load-bearing as the shader's screen-count loop bound). Default 0 = no overlay, so a frame
        // that never sets the Grid* fields uploads the same zeros. KEEP IN SYNC with sdf-world.hlsli's SdfGridWorld..
        var gridWorldBase = ((MaxScreenSurfaces + 1) * 4);

        floats[(gridWorldBase + 0)] = frame.GridFlags; floats[(gridWorldBase + 1)] = frame.GridFloorY; floats[(gridWorldBase + 2)] = frame.GridWorldPitch.X; floats[(gridWorldBase + 3)] = frame.GridWorldPitch.Y;

        var gridObjOriginBase = ((MaxScreenSurfaces + 2) * 4);

        floats[(gridObjOriginBase + 0)] = frame.GridObjectOrigin.X; floats[(gridObjOriginBase + 1)] = frame.GridObjectOrigin.Y; floats[(gridObjOriginBase + 2)] = frame.GridObjectOrigin.Z; floats[(gridObjOriginBase + 3)] = frame.GridObjectPitch.X;

        var gridObjFrameBase = ((MaxScreenSurfaces + 3) * 4);

        floats[(gridObjFrameBase + 0)] = frame.GridObjectFrame.X; floats[(gridObjFrameBase + 1)] = frame.GridObjectFrame.Y; floats[(gridObjFrameBase + 2)] = frame.GridObjectFrame.Z; floats[(gridObjFrameBase + 3)] = frame.GridObjectFrame.W;

        // The .z lane is the analytic-normal A/B toggle (0 = the forward-mode dual normal, the default; 1 = the legacy
        // 4-tap finite-difference probe), read by sdf-world.hlsli's worldUseTapNormals. The .w lane is the soft-shadow
        // GRID-CULL toggle (0 = ON, the default grid-gathered shadow march; 1 = OFF, the flat all-instances reference),
        // read by worldShadowCullEnabled. Both were reserved before, so an unset frame uploads 0 = analytic normals +
        // cull ON. KEEP IN SYNC with SdfFrame.UseFiniteDifferenceNormals / SdfFrame.DisableShadowCull.
        var gridObjParamsBase = ((MaxScreenSurfaces + 4) * 4);

        floats[(gridObjParamsBase + 0)] = frame.GridObjectPitch.Y; floats[(gridObjParamsBase + 1)] = frame.GridObjectPatchRadius; floats[(gridObjParamsBase + 2)] = (frame.UseFiniteDifferenceNormals
            ? 1f
            : 0f
        ); floats[(gridObjParamsBase + 3)] = (frame.DisableShadowCull
            ? 1f
            : 0f
        );

        // Engine-bench shader-feature levers: one reserved row after the grid rows. x = disable soft
        // shadows, y = disable AO, z = shadow-distance scale (0 = the full 1.0 reach — an unset frame uploads 0), w =
        // disable screen lights. All default 0, so a frame that never sets the Disable*/ShadowDistanceScale fields
        // uploads the same zeros = every feature ON at full reach. KEEP IN SYNC with sdf-world.hlsli's SdfBenchParams
        // decode (worldSoftShadowsDisabled/worldAoDisabled/worldShadowDistanceScale/worldScreenLightsDisabled).
        var benchParamsBase = ((MaxScreenSurfaces + 5) * 4);

        floats[(benchParamsBase + 0)] = (frame.DisableSoftShadows
            ? 1f
            : 0f
        ); floats[(benchParamsBase + 1)] = (frame.DisableAmbientOcclusion
            ? 1f
            : 0f
        ); floats[(benchParamsBase + 2)] = frame.ShadowDistanceScale; floats[(benchParamsBase + 3)] = (frame.DisableScreenLights
            ? 1f
            : 0f
        );

        // The shadow-proxy lever (PATH B): one reserved row AFTER the bench-params row (whose four lanes are full). x =
        // enable the shadow proxy (shadow rays skip Subtraction-family carve instances and march the pre-carve union
        // hull); y = use the camera-tile shadow mask instead of the per-pixel shadow-grid gather; z = use the bounded-cost
        // fast soft-shadow marcher; w = use the one-sample contact-AO approximation.
        // Both default 0, so a frame that never sets either lever uploads the same zeros = the full gathered occluder
        // set. KEEP IN SYNC with sdf-world.hlsli's SdfShadowProxyParams / worldShadowProxyEnabled /
        // worldUseCameraTileShadowMask / worldUseFastSoftShadowMarch / worldUseFastAmbientOcclusion.
        var shadowProxyBase = ((MaxScreenSurfaces + 6) * 4);

        floats[(shadowProxyBase + 0)] = (frame.EnableShadowProxy
            ? 1f
            : 0f
        ); floats[(shadowProxyBase + 1)] = (frame.UseCameraTileShadowMask
            ? 1f
            : 0f
        ); floats[(shadowProxyBase + 2)] = (frame.UseFastSoftShadowMarch
            ? 1f
            : 0f
        ); floats[(shadowProxyBase + 3)] = (frame.UseFastAmbientOcclusion
            ? 1f
            : 0f
        );

        // The far-field lever row: x = disable the beam-published per-tile far bound (the fine march then runs to
        // the far distance); yzw reserved. Default 0 = the far bound ON. KEEP IN SYNC with sdf-world.hlsli's
        // SdfFarFieldParams / worldFarBoundDisabled.
        var farFieldBase = ((MaxScreenSurfaces + 7) * 4);

        floats[(farFieldBase + 0)] = (frame.DisableFarBound
            ? 1f
            : 0f
        ); floats[(farFieldBase + 1)] = 0f; floats[(farFieldBase + 2)] = 0f; floats[(farFieldBase + 3)] = 0f;

        PackEnvironment(
            floats: floats,
            frame: frame
        );
    }
    // Eleven float4 rows, paired with shade-volumes.hlsli. Unused trailing slots carry zero bounds.
    private void PackVolumes(SdfFrame frame) {
        Array.Clear(array: m_volumeScratch);

        var floats = MemoryMarshal.Cast<byte, float>(span: m_volumeScratch.AsSpan());
        var volumes = frame.Volumes;

        if (volumes.Count > MaxVolumes) {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                "Too many bounded volumes."
            );
        }
        var count = volumes.Count;

        for (var index = 0; (index < count); index++) {
            var volume = volumes[index];

            volume.Validate(dynamicTransformCount: frame.DynamicTransforms.Count);
            var b = ((index * SdfVolume.VectorsPerEntry) * 4);
            var rotation = Quaternion.Normalize(value: volume.Rotation);

            floats[(b + 0)] = volume.Position.X; floats[(b + 1)] = volume.Position.Y; floats[(b + 2)] = volume.Position.Z; floats[(b + 3)] = volume.DynamicSlot;
            floats[(b + 4)] = rotation.X; floats[(b + 5)] = rotation.Y; floats[(b + 6)] = rotation.Z; floats[(b + 7)] = rotation.W;
            floats[(b + 8)] = volume.HalfExtent.X; floats[(b + 9)] = volume.HalfExtent.Y; floats[(b + 10)] = volume.HalfExtent.Z; floats[(b + 11)] = volume.Axis;
            floats[(b + 12)] = volume.Width; floats[(b + 13)] = volume.Speed; floats[(b + 14)] = BitConverter.UInt32BitsToSingle(value: volume.Seed); floats[(b + 15)] = volume.Steps;
            floats[(b + 16)] = volume.Intensity; floats[(b + 17)] = volume.Extinction;
            floats[(b + 18)] = volume.PulseAmplitude; floats[(b + 19)] = volume.PulseFrequency;
            floats[(b + 20)] = (volume.IntensityLane ?? -1); floats[(b + 21)] = volume.Ramp.Count;
            floats[(b + 22)] = ((float)volume.Kind);
            floats[(b + 40)] = volume.Coverage; floats[(b + 41)] = volume.Softness;
            for (var stop = 0; (stop < volume.Ramp.Count); stop++) {
                var row = ((b + 24) + (stop * 4));
                var value = volume.Ramp[stop];

                floats[row] = value.Color.X; floats[(row + 1)] = value.Color.Y;
                floats[(row + 2)] = value.Color.Z; floats[(row + 3)] = value.Density;
            }
        }
    }
    // The environment block: SdfEnvironment's lanes copied row for row after the far-field row, with the host bakes
    // the shader must not pay per pixel — every directional (light and softbox) normalized in double and rounded once
    // (DXC's DXIL backend constant-folds a normalize() while its SPIR-V backend emits a runtime call; a uniform has
    // no such asymmetry), the sun-disc angular radius baked into the pow() exponent that puts the disc's edge at half
    // brightness (k = ln 0.5 / ln cos r), the twinkle rate baked into a period in engine ticks so the shader reduces
    // the tick counter by an integer modulo, and the cloud drift, shear and spin integrated from the tick counter in
    // double (offsets wrapped modulo the lattice period, the angle modulo 2π). KEEP IN SYNC with sdf-world.hlsli's
    // SdfEnv* rows and SdfEnvironment's row layout.
    private static void PackEnvironment(SdfFrame frame, Span<float> floats) {
        var environment = frame.Environment;
        var lanes = environment.Lanes;
        var envBase = ((MaxScreenSurfaces + 8) * 4);

        lanes.CopyTo(destination: floats.Slice(
            length: SdfEnvironment.LaneCount,
            start: envBase
        ));

        for (var index = 0; (index < SdfEnvironment.MaxLights); index++) {
            var local = ((SdfEnvironment.LightsRow + (index * SdfEnvironment.RowsPerLight)) * 4);
            var row = (envBase + local);
            var kind = ((SdfLightKind)((byte)lanes[(local + 7)]));

            if (kind != SdfLightKind.Directional) {
                continue;
            }

            double x = lanes[(local + 0)], y = lanes[(local + 1)], z = lanes[(local + 2)];
            var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

            if (length <= 0d) {
                // A zero direction has no Lambert term; the authoring doors refuse one by name, and a frame assembled
                // in code still must not upload NaNs into every shaded pixel.
                x = SdfEnvironment.DefaultSunDirection.X; y = SdfEnvironment.DefaultSunDirection.Y; z = SdfEnvironment.DefaultSunDirection.Z;
                length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));
            }

            floats[(row + 0)] = ((float)(x / length)); floats[(row + 1)] = ((float)(y / length)); floats[(row + 2)] = ((float)(z / length));
        }

        var skyControl = (envBase + (SdfEnvironment.SkyControlRow * 4));
        var cosDiscRadius = Math.Cos(d: environment.SunDiscRadians);
        var discExponent = ((cosDiscRadius is > 0d and < 1d)
            ? Math.Clamp(
                value: (Math.Log(d: 0.5d) / Math.Log(d: cosDiscRadius)),
                min: 0d,
                max: 100000d
            )
            : 100000d
        );

        floats[(skyControl + 2)] = ((float)discExponent);

        var twinkle = (envBase + (SdfEnvironment.TwinkleRow * 4));
        var twinklePeriodTicks = ((environment.TwinkleRate > 0f)
            ? Math.Max(
                val1: 1d,
                val2: Math.Round(a: (((double)EngineTicks.PerSecond) / environment.TwinkleRate))
            )
            : 1d
        );

        floats[(twinkle + 2)] = ((float)twinklePeriodTicks);

        var elapsedSeconds = (((double)frame.SampleIndex) / EngineTicks.PerSecond);
        var drift = environment.CloudDrift;
        var shear = environment.CloudShear;
        var cloudsC = (envBase + ((SdfEnvironment.CloudsRow + 2) * 4));
        var cloudsD = (envBase + ((SdfEnvironment.CloudsRow + 3) * 4));

        floats[(cloudsC + 0)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * drift.X),
            y: CloudLatticePeriod
        ));
        floats[(cloudsC + 1)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * drift.Y),
            y: CloudLatticePeriod
        ));
        floats[(cloudsC + 2)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * shear.X),
            y: CloudLatticePeriod
        ));
        floats[(cloudsC + 3)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * shear.Y),
            y: CloudLatticePeriod
        ));
        floats[(cloudsD + 0)] = ((float)Math.IEEERemainder(
            x: (elapsedSeconds * environment.CloudSpin),
            y: Math.Tau
        ));

        for (var index = 0; (index < SdfEnvironment.MaxSoftboxes); index++) {
            var local = ((SdfEnvironment.SoftboxesRow + (index * SdfEnvironment.RowsPerSoftbox)) * 4);
            var row = (envBase + local);

            double x = lanes[(local + 0)], y = lanes[(local + 1)], z = lanes[(local + 2)];
            var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

            if (length <= 0d) {
                continue; // an unauthored softbox slot has zero weight and never contributes; leave its direction zero
            }

            floats[(row + 0)] = ((float)(x / length)); floats[(row + 1)] = ((float)(y / length)); floats[(row + 2)] = ((float)(z / length));
        }
    }

    // The cloud offset's wrap period in layer units. The lattice is hashed on integer cell coordinates, so any
    // integer period is seamless; this one keeps a full period inside float's exact-integer range with room for
    // the sub-cell fraction.
    private const double CloudLatticePeriod = 4096d;

    // Pack each frame's views (camera snapshot + region + render scale + the frame's far distance) into the 96-byte
    // ViewportData rows the kernels read — member-for-member from SdfFrame, no camera math (the snapshot already holds
    // the basis + tan(fov/2) + aspect). The render scale packs as its QUANTIZED numerator q (RenderScaleQ) so Stage 1,
    // the tile passes, and Stage 2 all derive the identical integer render extent. The far distance rides the row's
    // last lane because the viewport table is the one buffer every marching kernel (beam, views, instance cull, sky)
    // already binds — no descriptor grows. Only rows whose packed bytes changed are owed an upload; a row carries the
    // frame's presentation time, so a row is owed whenever that time moves. KEEP IN SYNC with sdf-world.hlsli's
    // ViewportData / worldFarDistance.
    private void PackViewports(SdfFrame frame, uint viewportCount) {
        var mirror = MemoryMarshal.Cast<byte, float>(span: m_viewportScratch.AsSpan());
        var resident = OweWholeTableOnce(
            entries: ((int)m_viewportCapacity),
            entryWords: ViewportWordCount,
            resident: m_viewportRowsResident,
            table: ViewportTable
        );
        Span<float> floats = stackalloc float[ViewportWordCount];

        for (var index = 0; (index < ((int)viewportCount)); index++) {
            var view = frame.Views[index];
            var camera = view.Camera;
            var region = view.Region;

            floats[0] = camera.Position.X; floats[1] = camera.Position.Y; floats[2] = camera.Position.Z; floats[3] = frame.Time;          // position.xyz, time
            floats[4] = camera.Right.X; floats[5] = camera.Right.Y; floats[6] = camera.Right.Z; floats[7] = camera.TanHalfFieldOfView;     // right.xyz, tan(fov/2)
            floats[8] = camera.Up.X; floats[9] = camera.Up.Y; floats[10] = camera.Up.Z; floats[11] = camera.AspectRatio;                   // up.xyz, aspect
            floats[12] = camera.Forward.X; floats[13] = camera.Forward.Y; floats[14] = camera.Forward.Z; floats[15] = DebugMode;           // forward.xyz, debug view mode
            floats[16] = region.X; floats[17] = region.Y; floats[18] = region.Width; floats[19] = region.Height;                           // region origin.xy, size.xy
            floats[20] = RenderScaleQ(
                slot: index,
                view: view
            ); floats[21] = view.AsymmetricFrustumOffset.X; floats[22] = view.AsymmetricFrustumOffset.Y; floats[23] = frame.FarDistance; // renderScale q, off-axis offset xy, far distance
            _ = StageTableEntry(
                entry: index,
                mirror: mirror,
                packed: floats,
                resident: resident,
                table: ViewportTable
            );
        }

        m_viewportRowsResident = Math.Max(
            val1: resident,
            val2: ((int)viewportCount)
        );
    }
    // The shared per-frame front half of both submission paths: validate, (re)bind sources, pack + upload the
    // viewport/transform buffers, and rebuild both push-constant blocks from the LIVE regions (the camera director
    // animates the split layout, so a frozen first-frame layout composited stale/blank rects mid-transition).
    private uint PrepareFrame(SdfFrame frame, Action<int>? onFrameSlotAvailable = null) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var viewportCount = ((uint)frame.Views.Count);

        if (
            (0 == viewportCount) ||
            (viewportCount > m_viewportCapacity)
        ) {
            throw new ArgumentException(message: $"This world engine composites 1 to {m_viewportCapacity} viewports; the frame has {viewportCount}.");
        }

        if (frame.DynamicTransforms.Count < m_requiredDynamicTransformCapacity) {
            throw new ArgumentException(
                message: $"The uploaded SDF program requires {m_requiredDynamicTransformCapacity} dynamic-transform slots; the frame supplies {frame.DynamicTransforms.Count}.",
                paramName: nameof(frame)
            );
        }

        // The far distance is read by every marching kernel as the depth each march ends at; a non-finite or
        // non-positive value would make every cone proof and far exit meaningless, so it is refused here rather than
        // guarded per kernel (the world validator refuses the authored value by name long before it reaches a frame).
        if (
            !float.IsFinite(f: frame.FarDistance) ||
            (frame.FarDistance <= 0f)
        ) {
            throw new ArgumentException(
                message: $"The frame's far distance must be finite and positive; got {frame.FarDistance}.",
                paramName: nameof(frame)
            );
        }

        // FRAME RING: advance to this produced frame's slot (keyed to the produced-frame count — deterministic, never
        // wall clock), then wait that slot's fence: it was armed by frame N − FrameRingSize's submit, so once it
        // signals, every resource about to be rewritten below (command buffer, host-visible buffers, descriptor
        // sets) is provably idle. A never-armed or already-waited fence is a no-op, so waited/first frames pass free.
        var slot = ((int)(m_ringFrame % FrameRingSize));

        m_currentSlot = slot;
        m_ringFrame++;
        m_frameFences[slot].Wait();
        onFrameSlotAvailable?.Invoke(obj: slot);

        BindSources(viewportCount: viewportCount);
        BindScreenSources();
        PackViewports(
            frame: frame,
            viewportCount: viewportCount
        );

        var transformsChanged = PackDynamicTransforms(frame: frame);

        // Re-bin only when an active maskable dynamic instance can move a grid entry, and only on a frame whose
        // transforms moved (or after a program upload): the grid is a pure function of the program and the transforms.
        // Invariant programs staged their grid once at UploadProgram.
        if (
            m_rebuildInstanceGridPerFrame &&
            (transformsChanged || m_instanceGridRebuildOwed)
        ) {
            var frameGrid = m_liveProgram.BuildFrameInstanceGrid(
                transforms: frame.DynamicTransforms,
                inputScratch: m_instanceGridInputScratch,
                workspace: m_instanceGridWorkspace
            );

            ValidateInstanceGridCapacity(words: frameGrid);
            StageInstanceGrid(words: frameGrid);
            m_instanceGridRebuildOwed = false;
        }

        WriteStagedUploads(slot: slot);
        // The ring tables: each slot's buffer receives only the ranges it is behind its mirror by. UploadProgram seeds
        // the screen-surface mirror and SetScreenSurface patches it; SetScreenDecal/ClearScreenDecal patch the decal
        // mirror; the screen-light and volume tables are packed every frame and diffed into theirs.
        m_screenSurfaces.Flush(
            buffer: m_screenSurfaceBuffers[slot],
            slot: slot
        );
        PackScreenLights(frame: frame);
        _ = m_screenLights.Write(
            bytes: m_screenLightScratch,
            offset: 0
        );
        m_screenLights.Flush(
            buffer: m_screenLightBuffers[slot],
            slot: slot
        );
        PackVolumes(frame: frame);
        _ = m_volumes.Write(
            bytes: m_volumeScratch,
            offset: 0
        );
        m_volumes.Flush(
            buffer: m_volumeBuffers[slot],
            slot: slot
        );
        m_decals.Flush(
            buffer: m_decalBuffers[slot],
            slot: slot
        );

        // CompositeParams { uint2 imageExtent; uint2 tileGrid; uint viewportCount; uint childMask; uint screenMask; uint instanceMaskWordCount; uint sampleIndex; } — Stage 0/1 push.
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: m_pushConstant.AsSpan());

        pushWords[0] = m_width; pushWords[1] = m_height; pushWords[2] = m_tileGridX; pushWords[3] = m_tileGridY; pushWords[4] = viewportCount; pushWords[5] = m_childMask; pushWords[6] = m_screenSourceMask; pushWords[7] = ((uint)m_liveInstanceMaskWordCount);
        // The deterministic tick clock star twinkle reads (cloud motion is baked into the environment rows). It rides
        // the push and is folded into ComputeFrameSignature via m_pushConstant, so the cadence gate never skips a frame
        // whose tick moved; a sky with no visible twinkle pushes 0, leaving a static frame skippable.
        var environment = frame.Environment;
        var twinkles = (
            (environment.StarBrightness > 0f) &&
            (environment.StarDensity > 0f) &&
            (environment.TwinkleShare > 0f) &&
            (environment.TwinkleDepth > 0f)
        );

        pushWords[8] = (twinkles
            ? frame.SampleIndex
            : 0u
        );

        BuildCompositePush(frame: frame);
        DecideCadenceSkip(
            frame: frame,
            viewportCount: viewportCount
        );
        return viewportCount;
    }
    // The quantized render-scale numerator q (1..255; 255 = native): one quantization, shared by the viewport row and
    // the composite push, so every kernel derives the same integer render extent. A child slot always renders native
    // (its source is another node's full-rect surface — Stage 1 never renders it, and Stage 2 must copy it 1:1).
    private byte RenderScaleQ(SdfViewSnapshot view, int slot) {
        if (IsChildSlot(slot: slot)) {
            return 255;
        }

        var scale = view.RenderScale;

        if (
            !(scale > 0f) ||
            (scale >= 1f)
        ) {
            return 255;
        }

        return ((byte)Math.Clamp(
            value: ((int)MathF.Round(x: (scale * 255f))),
            min: 1,
            max: 255
        ));
    }
    private nint SourceViewForSlot(int slot) {
        if (IsChildSlot(slot: slot)) {
            var view = m_childSourceViews[slot];

            if (0 == view) {
                throw new InvalidOperationException(message: $"The child node for viewport {slot} did not produce a same-device storage-image surface (an integer-copy child must hand back a general-layout storage image view).");
            }

            return view;
        }

        return m_sourceTextures[slot]!.ImageViewHandle;
    }
    // The per-view reconstruction blend quantized to one byte. Invalid/negative input degrades to the existing
    // bilinear path; values above one saturate at full clamped Catmull-Rom.
    private static byte UpscaleSharpnessQ(SdfViewSnapshot view) {
        var sharpness = view.UpscaleSharpness;

        if (
            !float.IsFinite(f: sharpness) ||
            (sharpness <= 0f)
        ) {
            return 0;
        }

        if (sharpness >= 1f) {
            return 255;
        }

        return ((byte)Math.Clamp(
            value: ((int)MathF.Round(x: (sharpness * 255f))),
            min: 0,
            max: 255
        ));
    }
    private void ValidateInstanceGridCapacity(ReadOnlySpan<uint> words) {
        if (words.Length > m_instanceGridWordCapacity) {
            throw new InvalidOperationException(message: $"The frame instance grid packed {words.Length} words into a {m_instanceGridWordCapacity}-word construction envelope.");
        }
    }

    /// <summary>Gets or sets the SDF debug view mode packed into each viewport row (<c>forward.w</c>); 0 renders the
    /// final lit image.</summary>
    public int DebugMode { get; set; }
}
