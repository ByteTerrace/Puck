using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Reverts screen slot <paramref name="screenIndex"/> to the image/procedural path (clears its decal
    /// descriptor's gridCols to 0 — the shader's "no decal" gate). A no-op if the slot carried no decal.</summary>
    /// <param name="screenIndex">The screen slot (0..<see cref="MaxScreenSurfaces"/>-1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is out of range.</exception>
    public void ClearScreenDecal(int screenIndex) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}."
            );
        }

        var descriptorBase = (screenIndex * DecalWordsPerCell);

        // CADENCE GATE: same producer-level fix as SetScreenDecal — a slot that is ALREADY clear (gridCols already 0)
        // must not look like a change; a caller that clears every frame (mirroring SetScreenDecal's every-frame poll)
        // would otherwise defeat the gate exactly as the unconditional bump did.
        if (m_decalScratch[(descriptorBase + 0)] == 0u) {
            return;
        }

        m_decalScratch[(descriptorBase + 0)] = 0u; // gridCols 0 => inert (the image/procedural path applies)
        m_decalScratch[(descriptorBase + 1)] = 0u;
        m_decalScratch[(descriptorBase + 2)] = 0u;
        m_decalScratch[(descriptorBase + 3)] = 0u;
        Array.Fill(
            array: m_decalDirty,
            value: true
        );
        // CADENCE GATE: revision-track the REAL decal change (see SetScreenDecal).
        m_decalRevision++;
    }
    /// <summary>Supplies the storage-image view a hosted child produced for its viewport slot this frame; the next
    /// frame binds it into the source arrays. The host owns this view's lifetime, so the binding is rewritten every
    /// frame rather than skipped on an unchanged handle value — a retired handle value can be re-issued for a
    /// different image, which a value-keyed skip would bind stale (see <c>BindScreenSources</c>).</summary>
    /// <param name="slot">The child's viewport slot (a bit the construction <see cref="SdfWorldEngineOptions.ChildMask"/> set).</param>
    /// <param name="imageViewHandle">The child's same-device storage-image view (General layout; the child owns it).</param>
    public void SetChildSource(int slot, nint imageViewHandle) {
        if (
            (slot < 0) ||
            (slot >= MaxViewports) ||
            !IsChildSlot(slot: slot)
        ) {
            throw new ArgumentException(message: $"Viewport {slot} is not a child slot of this engine (mask 0x{m_childMask:X}).");
        }

        m_childSourceViews[slot] = imageViewHandle;
    }
    /// <summary>Uploads the single font atlas the <see cref="SdfShapeType.Glyph"/> primitive samples as a
    /// distance-level field, replacing any previously set atlas. Static: unlike a screen source (an external per-frame
    /// image-view handle), this copies the CPU pixels into a device image once and holds the sampleable view for the
    /// engine's lifetime; the next produced frame binds it. The atlas must carry the true single-channel signed
    /// distance in the alpha channel (every Puck source does: the managed MTSDF generator computes it exactly, the
    /// coverage-conversion fallback <c>Puck.Text.SdfCoverageAtlas</c> replicates its single channel into alpha, and
    /// an imported MTSDF atlas carries it by construction). Passing an empty
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

        m_glyphAtlasUpload ??= m_gpu.SurfaceTransferFactory.CreateUpload(deviceContext: m_deviceContext);
        m_glyphAtlasView = m_glyphAtlasUpload.Upload(
            deviceContext: m_deviceContext,
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
    /// text changes; <see cref="ClearScreenDecal"/> reverts the slot to the image/procedural path.</summary>
    /// <param name="screenIndex">The screen slot (0..<see cref="MaxScreenSurfaces"/>-1).</param>
    /// <param name="columns">The grid column count (&gt; 0).</param>
    /// <param name="rows">The grid row count (&gt; 0).</param>
    /// <param name="distanceRange">The atlas's SDF distance range in texels (the AA source; 0 = a raw coverage atlas).</param>
    /// <param name="cellWords">The packed cells, row-major (rows × columns), <see cref="DecalWordsPerCell"/> uints each:
    /// (packedUvTopLeft, packedUvBottomRight [unorm2x16], fgRgba8, bgRgba8); a blank cell packs equal UV corners.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/>/<paramref name="columns"/>/<paramref name="rows"/> out of range, or the grid exceeds <see cref="MaxScreenDecalCells"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="cellWords"/> is not <c>rows × columns × 4</c> uints.</exception>
    public void SetScreenDecal(int screenIndex, int columns, int rows, float distanceRange, ReadOnlySpan<uint> cellWords) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}."
            );
        }

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
        var cellDestination = m_decalScratch.AsSpan(
            start: (((int)cellBase) * DecalWordsPerCell),
            length: cellWords.Length
        );

        // A provider that re-supplies the same decal every produced frame (e.g. the diegetic terminal mirroring an
        // untouched console — DiegeticUiDirector.ComposeTerminalDecal returns a fresh SdfScreenDecalFrame wrapper every
        // call even when its cell bytes are unchanged) must not look like new content. Change-detect before writing:
        // a call that reproduces the bytes already stored is a no-op, not a revision bump.
        if (
            (m_decalScratch[(descriptorBase + 0)] == ((uint)columns)) &&
            (m_decalScratch[(descriptorBase + 1)] == ((uint)rows)) &&
            (m_decalScratch[(descriptorBase + 3)] == distanceRangeBits) &&
            cellWords.SequenceEqual(other: cellDestination)
        ) {
            return;
        }

        m_decalScratch[(descriptorBase + 0)] = ((uint)columns);
        m_decalScratch[(descriptorBase + 1)] = ((uint)rows);
        m_decalScratch[(descriptorBase + 2)] = cellBase;
        m_decalScratch[(descriptorBase + 3)] = distanceRangeBits;
        cellWords.CopyTo(destination: cellDestination);
        // Every ring slot's buffer must catch up with the patched mirror when its turn comes.
        Array.Fill(
            array: m_decalDirty,
            value: true
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
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}."
            );
        }

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
    /// with no source bound falls back to the flat/procedural screen material, and an unbound slot IS value-skipped
    /// (its filler is engine-owned).</summary>
    /// <param name="screenIndex">The screen source slot (0..31, matching a program's declared
    /// <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="imageViewHandle">The source's same-device storage-image view, or 0 to unbind.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..31</c>.</exception>
    public void SetScreenSource(int screenIndex, nint imageViewHandle) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}."
            );
        }

        m_screenSourceViews[screenIndex] = imageViewHandle;
        m_screenSourceMask = ((0 != imageViewHandle)
            ? m_screenSourceMask | (1u << screenIndex)
            : m_screenSourceMask & ~(1u << screenIndex)
        );
    }
    /// <summary>Overwrites screen <paramref name="screenIndex"/>'s world-space sampling frame for the next produced
    /// frame — the per-frame counterpart of the screen-surface table <see cref="UploadProgram"/> otherwise writes only
    /// once, at program upload. A slab riding a moving rig must call
    /// this every frame its geometry moves, or its sampling frame goes stale relative to the geometry the dynamic
    /// transform already moved (a mismatched frame sizes/rotates/positions the sampled image wrong without affecting
    /// the geometry at all — see <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>'s
    /// frame contract). Pure host-side buffer state: the shader's <c>screenSurfaces[screenIndex]</c> read
    /// (<c>sdf-world.hlsli</c>) already resolves at shading time with no HLSL change required for this seam — only the
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
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(screenIndex),
                message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}."
            );
        }

        var unitRight = Vector3.Normalize(value: right);
        var unitUp = Vector3.Normalize(value: up);
        var floats = MemoryMarshal.Cast<byte, float>(span: m_screenSurfaceScratch.AsSpan());
        // 3 float4 per entry (right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad) — KEEP IN SYNC with SdfProgram's
        // ScreenSurfaceWords packing and sdf-world.hlsli's ScreenSurfaceData.
        var b = (screenIndex * 12);
        // SdfEngineNode polls this every frame via transform providers, often with an unchanged value (a static screen,
        // or a rig sampled at the same pose) — only an actual change needs to dirty the ring.
        var changed =
            ((floats[(b + 0)] != unitRight.X) || (floats[(b + 1)] != unitRight.Y) || (floats[(b + 2)] != unitRight.Z) || (floats[(b + 3)] != halfWidth) ||
            (floats[(b + 4)] != unitUp.X) || (floats[(b + 5)] != unitUp.Y) || (floats[(b + 6)] != unitUp.Z) || (floats[(b + 7)] != halfHeight) ||
            (floats[(b + 8)] != origin.X) || (floats[(b + 9)] != origin.Y) || (floats[(b + 10)] != origin.Z));

        if (!changed) {
            return;
        }

        floats[(b + 0)] = unitRight.X; floats[(b + 1)] = unitRight.Y; floats[(b + 2)] = unitRight.Z; floats[(b + 3)] = halfWidth;
        floats[(b + 4)] = unitUp.X; floats[(b + 5)] = unitUp.Y; floats[(b + 6)] = unitUp.Z; floats[(b + 7)] = halfHeight;
        floats[(b + 8)] = origin.X; floats[(b + 9)] = origin.Y; floats[(b + 10)] = origin.Z; floats[(b + 11)] = 0f;
        // Every ring slot's buffer must catch up with the patched mirror when its turn comes.
        Array.Fill(
            array: m_screenSurfaceDirty,
            value: true
        );
    }
}
