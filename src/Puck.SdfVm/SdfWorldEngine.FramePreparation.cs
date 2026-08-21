using System.Diagnostics;
using System.Runtime.InteropServices;
using Puck.Hosting;

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

            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: ScreenSourceBindingIndices[((int)element)],
                descriptorSetHandle: viewsSet,
                deviceHandle: m_deviceHandle,
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
            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: GlyphAtlasBindingIndex,
                descriptorSetHandle: viewsSet,
                deviceHandle: m_deviceHandle,
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

            m_descriptorAllocator.WriteStorageImage(
                arrayElement: element,
                binding: ViewSourceBindingIndex,
                descriptorSetHandle: m_viewsSets[m_currentSlot],
                deviceHandle: m_deviceHandle,
                imageViewHandle: view
            );
            m_descriptorAllocator.WriteStorageImage(
                arrayElement: element,
                binding: CompositeSourceBindingIndex,
                descriptorSetHandle: m_compositeSets[m_currentSlot],
                deviceHandle: m_deviceHandle,
                imageViewHandle: view
            );
            boundViews[element] = view;
        }
    }
    // Stage 2's CompositeParams2 { uint2 imageExtent; uint viewportCount; float4 rects[5]; uint2 scaleQPacked;
    // uint2 sharpnessQPacked; }: the LIVE regions drive the layout every frame. word[3] is unused (implicit HLSL
    // cbuffer padding ahead of the float4 rects array — KEEP IN SYNC with sdf-world-composite.comp.hlsl's struct);
    // the final four words carry the byte-packed per-view controls.
    private void BuildCompositePush(SdfFrame frame) {
        var words = MemoryMarshal.Cast<byte, uint>(span: m_compositePush.AsSpan());

        words[0] = m_width; words[1] = m_height; words[2] = ((uint)frame.Views.Count);

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
    private static double[] CrossDouble(double[] left, double[] right) => [
        ((left[1] * right[2]) - (left[2] * right[1])),
        ((left[2] * right[0]) - (left[0] * right[2])),
        ((left[0] * right[1]) - (left[1] * right[0])),
    ];
    private bool IsChildSlot(int slot) =>
        (0u != (m_childMask & (1u << slot)));
    private static double[] NormalizeDouble(double[] vector) {
        var length = Math.Sqrt(d: (((vector[0] * vector[0]) + (vector[1] * vector[1])) + (vector[2] * vector[2])));

        return [(vector[0] / length), (vector[1] / length), (vector[2] / length)];
    }
    // Pack each moving entity's rigid transform into the dynamic-transform scratch — 2 float4 per slot: position.xyz
    // (+ pad) then the orientation quaternion (xyzw) — for upload into the buffer SDF_OP_TRANSFORM_DYNAMIC indexes by
    // slot. An empty list is only valid for a program with no dynamic slots (PrepareFrame throws otherwise); it still
    // writes the one always-present slot as identity so the binding stays valid (a static program never reads it).
    // Clamped to the slot capacity the construction options grew the buffer to.
    private void PackDynamicTransforms(SdfFrame frame) {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_dynamicTransformScratch.AsSpan());
        var transforms = frame.DynamicTransforms;
        var capacity = (m_dynamicTransformScratch.Length / DynamicTransformByteLength);
        var count = Math.Min(
            val1: transforms.Count,
            val2: capacity
        );

        if (count == 0) {
            floats[0] = 0f; floats[1] = 0f; floats[2] = 0f; floats[3] = 0f;   // position.xyz, pad
            floats[4] = 0f; floats[5] = 0f; floats[6] = 0f; floats[7] = 1f;   // identity quaternion

            return;
        }

        for (var index = 0; (index < count); index++) {
            var transform = transforms[index];
            var b = (index * 8);

            // position.w encodes per-instance soft-shadow participation: 0 = casts (the default pad every prior frame
            // uploaded → byte-identical), 1 = shadow-suppressed (skipped by the soft-shadow march only). Read by
            // sdf-world.hlsli's sdfShadowParticipationActive skip; camera/AO marches ignore it.
            floats[(b + 0)] = transform.Position.X; floats[(b + 1)] = transform.Position.Y; floats[(b + 2)] = transform.Position.Z; floats[(b + 3)] = (transform.CastsSoftShadow
                ? 0f
                : 1f
            );
            floats[(b + 4)] = transform.Orientation.X; floats[(b + 5)] = transform.Orientation.Y; floats[(b + 6)] = transform.Orientation.Z; floats[(b + 7)] = transform.Orientation.W;
        }
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

        // The F1/F2 far-field lever row: one reserved row AFTER the shadow-proxy row. x = disable the
        // beam-published per-tile far bound (F1 A/B "off" side — the fine march ignores plane 3 and runs to MaxDistance
        // exactly as pre-F1); y = disable the F2 shadow light-side early exit (softShadow runs its full budget/reach); zw
        // reserved. Both levers default 0, so a frame that sets neither uploads zeros = both features ON (the shipped
        // behavior). KEEP IN SYNC with sdf-world.hlsli's SdfFarFieldParams / worldFarBoundDisabled / worldShadowEscapeExitDisabled.
        var farFieldBase = ((MaxScreenSurfaces + 7) * 4);

        floats[(farFieldBase + 0)] = (frame.DisableFarBound
            ? 1f
            : 0f
        ); floats[(farFieldBase + 1)] = (frame.DisableShadowEscapeExit
            ? 1f
            : 0f
        ); floats[(farFieldBase + 2)] = 0f; floats[(farFieldBase + 3)] = 0f;

        PackSunFrame(
            floats: floats,
            frame: frame
        );
        PackSkyFrame(
            floats: floats,
            frame: frame
        );
    }
    // The lighting rows: the scene's directional sun and its ambient, as per-frame data (SdfSunDirection/
    // SdfSunTangent/SdfSunBitangent/SunWeight/AmbientBase/AmbientHemisphere). Five rows AFTER the far-field row.
    // KEEP IN SYNC with sdf-world.hlsli's SdfSunFrameA..SdfAmbientColor.
    //
    // The sun is a FRAME, not a vector: the area-light shadow estimator samples a disc around the direction, so it
    // needs two tangents too. They are derived HERE, host-side: DXC's DXIL backend constant-folds normalize() while
    // its SPIR-V backend emits a runtime call, so a shader-side cross/normalize would be one compile-time constant on
    // DXIL and a driver rsqrt on SPIR-V. A uniform has no such asymmetry: both backends read these identical bits.
    //
    // The arithmetic is DOUBLE, rounded once at the end: a float32 Vector3.Normalize lands one ulp high in the
    // bitangent's Z for the default sun, so double precision keeps the default-sun path bit-identical.
    private static void PackSunFrame(SdfFrame frame, Span<float> floats) {
        var sunBase = ((MaxScreenSurfaces + 8) * 4);
        double[] sun = [frame.SunDirection.X, frame.SunDirection.Y, frame.SunDirection.Z];
        var length = Math.Sqrt(d: (((sun[0] * sun[0]) + (sun[1] * sun[1])) + (sun[2] * sun[2])));

        if (length <= 0d) {
            // A zero/degenerate direction has no frame to build. Fall back to the pinned default rather than uploading
            // NaNs into every shading term on the frame.
            sun = [0.51343602f, 0.79349202f, 0.32673201f];
            length = Math.Sqrt(d: (((sun[0] * sun[0]) + (sun[1] * sun[1])) + (sun[2] * sun[2])));
        }

        sun = [(sun[0] / length), (sun[1] / length), (sun[2] / length)];

        // tangent = normalize(Z x sun), bitangent = normalize(sun x tangent) — the construction the pasted literals
        // came from. A sun parallel to +Z degenerates the first cross, so fall back to the X axis there.
        var reference = ((Math.Abs(value: sun[2]) > 0.9999d)
            ? new double[] { 1d, 0d, 0d }
            : [0d, 0d, 1d]
        );
        var tangent = NormalizeDouble(vector: CrossDouble(
            left: reference,
            right: sun
        ));
        var bitangent = NormalizeDouble(vector: CrossDouble(
            left: sun,
            right: tangent
        ));

        floats[(sunBase + 0)] = ((float)sun[0]); floats[(sunBase + 1)] = ((float)sun[1]); floats[(sunBase + 2)] = ((float)sun[2]); floats[(sunBase + 3)] = frame.SunWeight;
        floats[(sunBase + 4)] = ((float)tangent[0]); floats[(sunBase + 5)] = ((float)tangent[1]); floats[(sunBase + 6)] = ((float)tangent[2]); floats[(sunBase + 7)] = frame.AmbientBase;
        floats[(sunBase + 8)] = ((float)bitangent[0]); floats[(sunBase + 9)] = ((float)bitangent[1]); floats[(sunBase + 10)] = ((float)bitangent[2]); floats[(sunBase + 11)] = frame.AmbientHemisphere;
        floats[(sunBase + 12)] = frame.SunColor.X; floats[(sunBase + 13)] = frame.SunColor.Y; floats[(sunBase + 14)] = frame.SunColor.Z; floats[(sunBase + 15)] = 0f;
        floats[(sunBase + 16)] = frame.AmbientColor.X; floats[(sunBase + 17)] = frame.AmbientColor.Y; floats[(sunBase + 18)] = frame.AmbientColor.Z; floats[(sunBase + 19)] = 0f;
    }
    // The procedural-sky rows: nine rows AFTER the five lighting rows. KEEP IN SYNC with sdf-world.hlsli's
    // SdfSkyZenith..SdfSkyCloudsD.
    //
    // The sun-disc exponent is HOST-BAKED from SkySunDiscRadians so worldSkyColor pays one pow() per pixel rather
    // than deriving the exponent from an angle: solving pow(cos(discRadians), k) = 0.5 for k (the disc's edge reads
    // half brightness) gives k = ln(0.5) / ln(cos(discRadians)), clamped away from the pole at discRadians -> 0.
    // The twinkle rate is likewise baked to a PERIOD IN ENGINE TICKS so the shader reduces the tick counter by an
    // integer modulo (exact, no float drift over a long session) before it ever touches a float phase. The cloud
    // drift, shear and spin are integrated HERE, in double, from the same tick counter — the offsets in layer units
    // wrapped modulo the lattice period, the spin angle modulo 2π — so they stay float-precise however long the
    // session runs (SampleIndex itself wraps once per 2^32 ticks, ~23.7 h, a single jump the layer takes at that
    // moment).
    private static void PackSkyFrame(SdfFrame frame, Span<float> floats) {
        var skyBase = ((MaxScreenSurfaces + 13) * 4);

        floats[(skyBase + 0)] = frame.SkyZenithColor.X; floats[(skyBase + 1)] = frame.SkyZenithColor.Y; floats[(skyBase + 2)] = frame.SkyZenithColor.Z; floats[(skyBase + 3)] = frame.SkyFogDensity;
        floats[(skyBase + 4)] = frame.SkyHorizonColor.X; floats[(skyBase + 5)] = frame.SkyHorizonColor.Y; floats[(skyBase + 6)] = frame.SkyHorizonColor.Z; floats[(skyBase + 7)] = (frame.SkyEnabled
            ? 1f
            : 0f
        );
        floats[(skyBase + 8)] = frame.SkyGroundColor.X; floats[(skyBase + 9)] = frame.SkyGroundColor.Y; floats[(skyBase + 10)] = frame.SkyGroundColor.Z; floats[(skyBase + 11)] = frame.SkySunDiscIntensity;

        var cosDiscRadius = Math.Cos(d: frame.SkySunDiscRadians);
        var discExponent = ((cosDiscRadius is > 0d and < 1d)
            ? Math.Clamp(
                value: (Math.Log(d: 0.5d) / Math.Log(d: cosDiscRadius)),
                min: 0d,
                max: 100000d
            )
            : 100000d
        );

        floats[(skyBase + 12)] = ((float)discExponent); floats[(skyBase + 13)] = frame.SkyStarDensity; floats[(skyBase + 14)] = frame.SkyStarBrightness; floats[(skyBase + 15)] = frame.SkyStarSeed;

        var twinklePeriodTicks = ((frame.SkyStarTwinkleRate > 0f)
            ? Math.Max(
                val1: 1d,
                val2: Math.Round(a: (((double)EngineTicks.PerSecond) / frame.SkyStarTwinkleRate))
            )
            : 1d
        );

        floats[(skyBase + 16)] = frame.SkyStarTwinkleShare; floats[(skyBase + 17)] = frame.SkyStarTwinkleDepth; floats[(skyBase + 18)] = ((float)twinklePeriodTicks); floats[(skyBase + 19)] = 0f;

        var elapsedSeconds = (((double)frame.SampleIndex) / EngineTicks.PerSecond);
        var cloudOffsetX = Math.IEEERemainder(x: (elapsedSeconds * frame.SkyCloudDrift.X), y: CloudLatticePeriod);
        var cloudOffsetY = Math.IEEERemainder(x: (elapsedSeconds * frame.SkyCloudDrift.Y), y: CloudLatticePeriod);

        floats[(skyBase + 20)] = frame.SkyCloudColor.X; floats[(skyBase + 21)] = frame.SkyCloudColor.Y; floats[(skyBase + 22)] = frame.SkyCloudColor.Z; floats[(skyBase + 23)] = frame.SkyCloudCoverage;
        floats[(skyBase + 24)] = frame.SkyCloudSoftness; floats[(skyBase + 25)] = frame.SkyCloudScale; floats[(skyBase + 26)] = frame.SkyCloudSeed; floats[(skyBase + 27)] = 0f;
        var shearOffsetX = Math.IEEERemainder(x: (elapsedSeconds * frame.SkyCloudShear.X), y: CloudLatticePeriod);
        var shearOffsetY = Math.IEEERemainder(x: (elapsedSeconds * frame.SkyCloudShear.Y), y: CloudLatticePeriod);
        var spinAngle = Math.IEEERemainder(x: (elapsedSeconds * frame.SkyCloudSpin), y: Math.Tau);

        floats[(skyBase + 28)] = ((float)cloudOffsetX); floats[(skyBase + 29)] = ((float)cloudOffsetY); floats[(skyBase + 30)] = ((float)shearOffsetX); floats[(skyBase + 31)] = ((float)shearOffsetY);
        floats[(skyBase + 32)] = ((float)spinAngle); floats[(skyBase + 33)] = frame.SkyCloudCurl; floats[(skyBase + 34)] = 0f; floats[(skyBase + 35)] = 0f;
    }

    // The cloud offset's wrap period in layer units. The lattice is hashed on integer cell coordinates, so any
    // integer period is seamless; this one keeps a full period inside float's exact-integer range with room for
    // the sub-cell fraction.
    private const double CloudLatticePeriod = 4096d;

    // Pack each frame's views (camera snapshot + region + render scale) into the 96-byte ViewportData rows the kernels
    // read — member-for-member from SdfFrame, no camera math (the snapshot already holds the basis + tan(fov/2) +
    // aspect). The render scale packs as its QUANTIZED numerator q (RenderScaleQ) so Stage 1, the tile passes, and
    // Stage 2 all derive the identical integer render extent.
    private void PackViewports(SdfFrame frame, uint viewportCount) {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_viewportScratch.AsSpan());

        for (var index = 0; (index < ((int)viewportCount)); index++) {
            var view = frame.Views[index];
            var camera = view.Camera;
            var region = view.Region;
            var b = (index * 24);

            floats[(b + 0)] = camera.Position.X; floats[(b + 1)] = camera.Position.Y; floats[(b + 2)] = camera.Position.Z; floats[(b + 3)] = frame.Time;          // position.xyz, time
            floats[(b + 4)] = camera.Right.X; floats[(b + 5)] = camera.Right.Y; floats[(b + 6)] = camera.Right.Z; floats[(b + 7)] = camera.TanHalfFieldOfView;     // right.xyz, tan(fov/2)
            floats[(b + 8)] = camera.Up.X; floats[(b + 9)] = camera.Up.Y; floats[(b + 10)] = camera.Up.Z; floats[(b + 11)] = camera.AspectRatio;                   // up.xyz, aspect
            floats[(b + 12)] = camera.Forward.X; floats[(b + 13)] = camera.Forward.Y; floats[(b + 14)] = camera.Forward.Z; floats[(b + 15)] = DebugMode;           // forward.xyz, debug view mode
            floats[(b + 16)] = region.X; floats[(b + 17)] = region.Y; floats[(b + 18)] = region.Width; floats[(b + 19)] = region.Height;                           // region origin.xy, size.xy
            floats[(b + 20)] = RenderScaleQ(
                slot: index,
                view: view
            ); floats[(b + 21)] = view.AsymmetricFrustumOffset.X; floats[(b + 22)] = view.AsymmetricFrustumOffset.Y; floats[(b + 23)] = 0f; // renderScale q, off-axis offset xy, spare
        }
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
        m_viewportBuffers[slot].Write<byte>(data: m_viewportScratch);
        PackDynamicTransforms(frame: frame);
        m_dynamicTransformBuffers[slot].Write<byte>(data: m_dynamicTransformScratch);
        // Re-bin only when an active maskable dynamic instance can move a grid entry. Invariant programs had every
        // ring slot seeded by UploadProgram; rewriting the same CSR words every frame would be pure CPU/upload work.
        if (m_rebuildInstanceGridPerFrame) {
            var rebuildStopwatch = Stopwatch.StartNew();
            var frameGrid = m_liveProgram.BuildFrameInstanceGrid(
                transforms: frame.DynamicTransforms,
                inputScratch: m_instanceGridInputScratch,
                workspace: m_instanceGridWorkspace
            );

            ValidateInstanceGridCapacity(words: frameGrid);
            m_instanceGridBuffers[slot].Write<uint>(data: frameGrid);
            m_lastInstanceGridRebuildMilliseconds = rebuildStopwatch.Elapsed.TotalMilliseconds;
        } else {
            m_lastInstanceGridRebuildMilliseconds = null;
        }
        // The screen-surface table: UploadProgram seeds the host mirror once; any SetScreenSurface call since patches
        // it in place. Unlike the buffers above, this one is only re-uploaded when a value-changing SetScreenSurface
        // (or UploadProgram) actually dirtied this slot's copy — a static program's screens never rewrite this table
        // after their first upload, and a screen riding a dynamic transform still renders fresh every produced frame,
        // because SetScreenSurface dirties every ring slot the instant a value actually changes (C5, screen-surface
        // upload; the screen-light buffer below stays unconditional — it also carries per-frame env/grid/bench rows
        // that genuinely dirty most frames).
        if (m_screenSurfaceDirty[slot]) {
            m_screenSurfaceBuffers[slot].Write<byte>(data: m_screenSurfaceScratch);
            m_screenSurfaceDirty[slot] = false;
        }

        PackScreenLights(frame: frame);
        m_screenLightBuffers[slot].Write<byte>(data: m_screenLightScratch);
        // The glyph-decal buffer: SetScreenDecal/ClearScreenDecal patch the host mirror; unlike the buffers above this
        // one is only re-uploaded when a decal call actually dirtied this slot's copy — it is 820 KB, and a program
        // that never touches decals (e.g. the bare revealed room) must not pay that upload every frame. An all-zero
        // mirror (no decal declared) uploads inert descriptors once per slot's first frame so the GPU buffers'
        // initial contents are known-zero — byte-identical shading either way.
        if (m_decalDirty[slot]) {
            m_decalBuffers[slot].Write<uint>(data: m_decalScratch);
            m_decalDirty[slot] = false;
        }

        // CompositeParams { uint2 imageExtent; uint2 tileGrid; uint viewportCount; uint childMask; uint screenMask; uint instanceMaskWordCount; uint sampleIndex; } — Stage 0/1 push.
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: m_pushConstant.AsSpan());

        pushWords[0] = m_width; pushWords[1] = m_height; pushWords[2] = m_tileGridX; pushWords[3] = m_tileGridY; pushWords[4] = viewportCount; pushWords[5] = m_childMask; pushWords[6] = m_screenSourceMask; pushWords[7] = ((uint)m_liveInstanceMaskWordCount);
        // The shadow estimator's net index. It rides the push (not a buffer) because it changes every frame and
        // nothing else does, and it is folded into ComputeFrameSignature below via m_pushConstant so the cadence gate
        // can never skip a frame whose sample index moved.
        pushWords[8] = frame.SampleIndex;
        // Word 9 — the shadow accumulator's control word: bit 0 disables it, bit 1 forces a reset. The reset covers a
        // freshly constructed engine, whose source textures have not yet carried a written alpha lane (the history's
        // home), so the recurrence never folds in an undefined value. The textures are allocated once and never
        // reallocated or cleared, so that is the only moment the lane is undefined.
        //
        // A PROGRAM UPLOAD DELIBERATELY DOES NOT RESET. The history is screen-space and every read is already validated
        // by reprojection, the epoch, and the depth tolerance, so changed geometry is rejected per pixel rather than
        // wholesale. A live world uploads a new program EVERY frame — tying the reset to the program revision pins the
        // reset bit high forever and holds the accumulator at its raw single-frame estimate, which is a silent
        // no-op rather than a visible failure.
        //
        // It is written BEFORE the cadence decision precisely so it IS hashed — an enable flip or a reset must force a
        // render, or it would be latched and never applied.
        pushWords[9] = (frame.DisableShadowAccumulation
            ? 1u
            : 0u) | ((m_shadowAccumulationResetFrames > 0)
            ? 2u
            : 0u
        );

        if (m_shadowAccumulationResetFrames > 0) {
            m_shadowAccumulationResetFrames--;
        }

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
