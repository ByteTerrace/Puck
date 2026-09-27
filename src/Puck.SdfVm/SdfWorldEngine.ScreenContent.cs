using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Commands;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Reverts screen slot <paramref name="screenIndex"/> to the image or unbound-glass path (clears its decal
    /// descriptor's gridCols to 0 — the shader's "no decal" gate). A no-op if the slot carried no decal.</summary>
    /// <param name="screenIndex">The screen slot (0..<see cref="MaxScreenSurfaces"/>-1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is out of range.</exception>
    public void ClearScreenDecal(int screenIndex) {
        RequireScreenIndex(screenIndex: screenIndex);

        var descriptorBase = (screenIndex * DecalWordsPerCell);

        // CADENCE GATE: same producer-level fix as SetScreenDecal — a slot that is ALREADY clear (gridCols already 0)
        // must not look like a change; a caller that clears every frame (mirroring SetScreenDecal's every-frame poll)
        // would otherwise defeat the gate exactly as the unconditional bump did.
        if (DecalWords[(descriptorBase + 0)] == 0u) {
            return;
        }

        // gridCols 0 => inert (the image or unbound-glass path applies).
        ReadOnlySpan<uint> cleared = [0u, 0u, 0u, 0u];

        _ = m_decalRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: cleared),
            offset: (descriptorBase * sizeof(uint))
        );
        // CADENCE GATE: revision-track the REAL decal change (see SetScreenDecal).
        m_decalRevision++;
    }
    /// <summary>Uploads the single font atlas the <see cref="SdfShapeType.Glyph"/> primitive samples as a
    /// distance-level field, replacing any previously set atlas. Static: unlike a screen source (an external per-frame
    /// image-view handle), this copies the CPU pixels into a device image once and holds the sampleable view for the
    /// engine's lifetime; the next produced frame binds it. The atlas must carry single-channel signed-distance
    /// samples in alpha, as generated and imported MTSDF atlases do. Quantization, outline approximation, and
    /// filtering mean these samples are not an exact-distance guarantee. Supply the same pixels and dimensions
    /// used to compute each Glyph instruction's sampling correction. Passing an empty
    /// <paramref name="rgbaPixels"/> clears the atlas back to the neutral 1×1 filler.</summary>
    /// <param name="rgbaPixels">The tightly packed, row-major, top-down RGBA atlas pixels
    /// (<paramref name="width"/> × <paramref name="height"/> × 4 bytes), or empty to clear.</param>
    /// <param name="width">The atlas width in texels.</param>
    /// <param name="height">The atlas height in texels.</param>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="ArgumentException">The dimensions are zero, or the pixel buffer length is not
    /// <paramref name="width"/> × <paramref name="height"/> × 4.</exception>
    public void SetGlyphAtlas(ReadOnlyMemory<byte> rgbaPixels, uint width, uint height) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (rgbaPixels.IsEmpty) {
            m_glyphAtlasView = 0;
            Array.Clear(array: m_boundGlyphAtlasViews);

            return;
        }

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "A glyph atlas must have non-zero dimensions.");
        }

        if (rgbaPixels.Length != checked((int)((width * height) * 4))) {
            throw new ArgumentException(
                message: $"A glyph atlas of {width}x{height} needs {((width * height) * 4)} RGBA bytes; got {rgbaPixels.Length}.",
                paramName: nameof(rgbaPixels)
            );
        }

        // The upload object owns the image + staging + the returned view. One instance held for the lifetime (a re-set
        // re-uploads through it — Vulkan reuses the view, Direct3D 12 replaces it, so re-read the handle every time).
        // The atlas is shared across the frame ring like the program buffer, so a RE-upload (rewriting an image an
        // in-flight frame may still sample) first drains the ring — a rare host event, typically once per engine.
        WaitForFrameRing();

        m_glyphAtlasUpload ??= m_gpu.SurfaceTransferFactory.CreateUpload();
        m_glyphAtlasView = m_glyphAtlasUpload.Upload(
            format: Format,
            height: height,
            pixels: rgbaPixels,
            width: width
        );

        // A re-upload retires the previous view, and a retired handle value can come straight back as the new one (see
        // BindScreenSources' handle-identity rule), so the value alone cannot tell BindScreenSources that this binding
        // must be rewritten. Invalidate the per-ring-slot cache explicitly — the ring is already drained above, so the
        // next frame in each slot rewrites the descriptor before anything samples it.
        Array.Clear(array: m_boundGlyphAtlasViews);
    }
    /// <summary>Binds a glyph decal (the material-level text tier) to screen slot <paramref name="screenIndex"/> for the
    /// next produced frame: the screen's ScreenSlab face then samples this grid of glyph cells + colours at the hit
    /// instead of a bound image (dense reading text, resolution-independent at walk-up distance — see
    /// <c>sdfSampleGlyphDecal</c>). The carrier geometry is the same screen-surface frame the image path uses (declared
    /// by <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>);
    /// a glyph atlas must be uploaded (<see cref="SetGlyphAtlas"/>) for the letters to resolve. Re-set every frame the
    /// text changes; <see cref="ClearScreenDecal"/> reverts the slot to the image or unbound-glass path.</summary>
    /// <param name="screenIndex">The screen slot (0..<see cref="MaxScreenSurfaces"/>-1).</param>
    /// <param name="columns">The grid column count (&gt; 0).</param>
    /// <param name="rows">The grid row count (&gt; 0).</param>
    /// <param name="distanceRange">The atlas's SDF distance range in texels (the AA source; 0 = a raw coverage atlas).</param>
    /// <param name="cellWords">The packed cells, row-major (rows × columns), <see cref="DecalWordsPerCell"/> uints each:
    /// (packedUvTopLeft, packedUvBottomRight [unorm2x16], fgRgba8, bgRgba8); a blank cell packs equal UV corners.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/>/<paramref name="columns"/>/<paramref name="rows"/> out of range, or the grid exceeds <see cref="MaxScreenDecalCells"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="cellWords"/> is not <c>rows × columns × 4</c> uints.</exception>
    public void SetScreenDecal(int screenIndex, int columns, int rows, float distanceRange, ReadOnlySpan<uint> cellWords) {
        RequireScreenIndex(screenIndex: screenIndex);

        if (
            (columns <= 0) ||
            (rows <= 0)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(columns),
                message: "A decal grid must have positive columns and rows."
            );
        }

        var cellCount = (columns * rows);

        if (cellCount > MaxScreenDecalCells) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(columns),
                message: $"A decal grid of {columns}×{rows} = {cellCount} cells exceeds the per-screen budget {MaxScreenDecalCells}."
            );
        }

        if (cellWords.Length != (cellCount * DecalWordsPerCell)) {
            throw new ArgumentException(
                message: $"A {columns}×{rows} decal needs {(cellCount * DecalWordsPerCell)} cell words; got {cellWords.Length}.",
                paramName: nameof(cellWords)
            );
        }

        var cellBase = ((uint)(DecalDescriptorCount + (screenIndex * MaxScreenDecalCells)));
        var descriptorBase = (screenIndex * DecalWordsPerCell);
        var distanceRangeBits = BitConverter.SingleToUInt32Bits(value: distanceRange);
        var decalWords = DecalWords;
        var cellWordStart = (((int)cellBase) * DecalWordsPerCell);
        var cellDestination = decalWords.Slice(
            length: cellWords.Length,
            start: cellWordStart
        );

        // A provider that re-supplies the same decal every produced frame (e.g. the diegetic terminal mirroring an
        // untouched console — DiegeticUiDirector.ComposeTerminalDecal returns a fresh SdfScreenDecalFrame wrapper every
        // call even when its cell bytes are unchanged) must not look like new content. Change-detect before writing:
        // a call that reproduces the bytes already stored is a no-op, not a revision bump.
        if (
            (decalWords[(descriptorBase + 0)] == ((uint)columns)) &&
            (decalWords[(descriptorBase + 1)] == ((uint)rows)) &&
            (decalWords[(descriptorBase + 3)] == distanceRangeBits) &&
            cellWords.SequenceEqual(other: cellDestination)
        ) {
            return;
        }

        ReadOnlySpan<uint> descriptor = [((uint)columns), ((uint)rows), cellBase, distanceRangeBits];

        _ = m_decalRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: descriptor),
            offset: (descriptorBase * sizeof(uint))
        );
        _ = m_decalRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: cellWords),
            offset: (cellWordStart * sizeof(uint))
        );
        // CADENCE GATE: the decal buffer is revision-tracked (not re-hashed each frame — it is 820 KB), so a REAL decal
        // change invalidates the signature.
        m_decalRevision++;
    }
    /// <summary>Supplies the colored light a declared screen surface at <paramref name="screenIndex"/> emits into the
    /// room this frame — typically the average color of its framebuffer, so the room glows the game's dominant hue. The
    /// light's position/orientation/extent come from the program's screen-surface table (a screen is an area emitter);
    /// only its color is per-frame. Contributes nothing while the screen is unbound (the shader gates on the same
    /// screen mask <see cref="SetScreenSource"/> maintains) or while the color is zero (a dark screen).</summary>
    /// <param name="screenIndex">The screen slot (0..31, matching a program's declared <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="color">The emitted light color (linear RGB, typically 0..1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..31</c>.</exception>
    public void SetScreenLight(int screenIndex, Vector3 color) {
        RequireScreenIndex(screenIndex: screenIndex);

        m_screenLightColors[screenIndex] = color;
    }
    /// <summary>Supplies (or clears) the GPU image a declared screen surface (see <see cref="SdfProgramBuilder"/>'s
    /// screen-surface <c>ScreenSlab</c> overload) at <paramref name="screenIndex"/> samples this frame — a
    /// same-device storage-image view (General layout, shader-readable), typically a hosted child's or an emulator's
    /// native framebuffer image (not a pane-resampled one: Stage 1 samples it directly, so any fit/scale is the
    /// sampling itself). The next frame binds it into the screen-source array. The host owns this view's lifetime and
    /// may retire it between any two frames, so a bound slot's descriptor is rewritten every frame instead of being
    /// skipped on an unchanged handle value: a handle value is unique only among live objects, and a retired one can
    /// come back naming a different image (see <c>BindScreenSources</c>). Passing 0 clears the slot: a screen surface
    /// with no source bound shades as unbound glass, and an unbound slot IS value-skipped
    /// (its filler is engine-owned).</summary>
    /// <param name="screenIndex">The screen source slot (0..31, matching a program's declared
    /// <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="imageViewHandle">The source's same-device storage-image view, or 0 to unbind.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..31</c>.</exception>
    public void SetScreenSource(int screenIndex, nint imageViewHandle) {
        RequireScreenIndex(screenIndex: screenIndex);

        m_screenSourceViews[screenIndex] = imageViewHandle;
        m_screenSourceMask = ((0 != imageViewHandle)
            ? m_screenSourceMask | (1u << screenIndex)
            : m_screenSourceMask & ~(1u << screenIndex)
        );
    }
    /// <summary>Supplies (or clears) the mapping screen <paramref name="screenIndex"/> is drawn from for the next produced
    /// frame: the mapping its row publishes, whose draw form (<see cref="SourceMapping.Draw"/>) the screen shading reads
    /// for the glass's warp, the layout, the fit's letterbox and the crop. A screen shades its bound source only while it
    /// has a mapping; with none it shades as unbound glass. A call with the mapping already supplied packs nothing, and
    /// the region owes only the words a new mapping changes.</summary>
    /// <param name="screenIndex">The screen slot (0..<see cref="MaxScreenSurfaces"/>-1).</param>
    /// <param name="mapping">The screen's surface mapping, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="mapping"/> is not drawable: its placement is not a
    /// surface, its warp declares no inverse, or its shape is invalid.</exception>
    public void SetScreenMapping(int screenIndex, SourceMapping? mapping) {
        RequireScreenIndex(screenIndex: screenIndex);

        if (ReferenceEquals(
            objA: m_screenMappings[screenIndex],
            objB: mapping
        )) {
            return;
        }

        // The six float4 rows frame/sdf-environment.hlsli reads as ScreenMappingData: the warp's u and v rows
        // (coefficients of u, v and 1, then the face distance one warped unit spans), the image map's u and v rows
        // (coefficients, then whether the screen is mapped and whether it letterboxes), the crop (left, top, right,
        // bottom), and the crop inset by half a source pixel, which every sample is clamped to.
        Span<float> floats = stackalloc float[(ScreenMappingByteLength / sizeof(float))];

        floats.Clear();

        if (mapping is not null) {
            var draw = mapping.Draw();
            var warp = draw.Warp;
            var image = draw.Image;
            var crop = draw.Crop;
            var right = (crop.X + crop.Width);
            var bottom = (crop.Y + crop.Height);
            var halfTexel = (draw.Texel * 0.5f);

            floats[0] = warp.M11; floats[1] = warp.M21; floats[2] = warp.M31; floats[3] = FaceSpan(x: warp.M11, y: warp.M21);
            floats[4] = warp.M12; floats[5] = warp.M22; floats[6] = warp.M32; floats[7] = FaceSpan(x: warp.M12, y: warp.M22);
            floats[8] = image.M11; floats[9] = image.M21; floats[10] = image.M31; floats[11] = 1f;
            floats[12] = image.M12; floats[13] = image.M22; floats[14] = image.M32; floats[15] = (draw.Letterboxes ? 1f : 0f);
            floats[16] = crop.X; floats[17] = crop.Y; floats[18] = right; floats[19] = bottom;
            floats[20] = (crop.X + halfTexel.X); floats[21] = (crop.Y + halfTexel.Y); floats[22] = (right - halfTexel.X); floats[23] = (bottom - halfTexel.Y);
        }

        _ = m_screenMappingRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: floats),
            offset: (screenIndex * ScreenMappingByteLength)
        );
        m_screenMappings[screenIndex] = mapping;

        // The face distance one unit of a warped coordinate spans: the reciprocal of its gradient's length, or zero for a
        // warp that collapses the axis.
        static float FaceSpan(float x, float y) {
            var length = MathF.Sqrt(x: ((x * x) + (y * y)));

            return ((length > 0f) ? (1f / length) : 0f);
        }
    }
    /// <summary>Overwrites screen <paramref name="screenIndex"/>'s world-space sampling frame for the next produced
    /// frame — the per-frame counterpart of the screen-surface table <see cref="UploadProgram"/> otherwise writes only
    /// once, at program upload. A slab riding a moving rig must call
    /// this every frame its geometry moves, or its sampling frame goes stale relative to the geometry the dynamic
    /// transform already moved (a mismatched frame sizes/rotates/positions the sampled image wrong without affecting
    /// the geometry at all — see <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>'s
    /// frame contract). Pure host-side buffer state: the shader's <c>screenSurfaces[screenIndex]</c> read
    /// (<c>shade/sdf-environment.hlsli</c>) already resolves at shading time with no HLSL change required for this seam — only the
    /// host-side table this call patches needed to become writable per frame. A call that reproduces the entry's
    /// current values (a static screen, or a rig sampled at an unchanged pose) is a no-op — it does not dirty the
    /// upload; the GPU table only re-uploads on an actual change.</summary>
    /// <param name="screenIndex">The screen slot (0..31, matching a program's declared <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="origin">The front face's world-space center this frame.</param>
    /// <param name="right">The unit world-space axis the UV's U increases along this frame (need not be pre-normalized —
    /// normalized here, matching <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>'s contract).</param>
    /// <param name="up">The unit world-space axis the UV's V increases against this frame (V = 0 at the top; normalized here).</param>
    /// <param name="halfWidth">The half-extent along <paramref name="right"/> this frame.</param>
    /// <param name="halfHeight">The half-extent along <paramref name="up"/> this frame.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..31</c>.</exception>
    public void SetScreenSurface(int screenIndex, Vector3 origin, Vector3 right, Vector3 up, float halfWidth, float halfHeight) {
        RequireScreenIndex(screenIndex: screenIndex);

        var unitRight = Vector3.Normalize(value: right);
        var unitUp = Vector3.Normalize(value: up);
        // 3 float4 per entry (right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad) — KEEP IN SYNC with SdfProgram's
        // ScreenSurfaceWords packing and frame/sdf-environment.hlsli's ScreenSurfaceData.
        Span<float> floats = stackalloc float[(ScreenSurfaceByteLength / sizeof(float))];

        floats[0] = unitRight.X; floats[1] = unitRight.Y; floats[2] = unitRight.Z; floats[3] = halfWidth;
        floats[4] = unitUp.X; floats[5] = unitUp.Y; floats[6] = unitUp.Z; floats[7] = halfHeight;
        floats[8] = origin.X; floats[9] = origin.Y; floats[10] = origin.Z; floats[11] = 0f;
        // SdfEngineNode polls this every frame via transform providers, often with an unchanged value (a static screen,
        // or a rig sampled at the same pose); the region owes only the words that actually changed.
        _ = m_screenSurfaceRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: floats),
            offset: (screenIndex * ScreenSurfaceByteLength)
        );
    }

    // The decal table as the descriptor band + cell region's words.
    private ReadOnlySpan<uint> DecalWords => MemoryMarshal.Cast<byte, uint>(span: m_decalRegion.Contents);

    /// <summary>Throws if <paramref name="screenIndex"/> falls outside <c>0..<see cref="MaxScreenSurfaces"/>-1</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is out of range.</exception>
    private static void RequireScreenIndex(int screenIndex) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}."
            );
        }
    }
}
