using System.Globalization;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>What a visibility identity says a pixel sees.</summary>
public enum SdfVisibilityKind : uint {
    /// <summary>Nothing: the sky, or a pixel whose record is not this frame's.</summary>
    Background = 0,
    /// <summary>An SDF surface; the source is the winning instance's program ordinal plus one.</summary>
    Sdf = 1,
    /// <summary>A baked mesh; the source is its draw ordinal.</summary>
    Mesh = 2,
}
/// <summary>
/// The host's statement of the visibility record's shared words: the identity's kind and source fields, the transform-slot
/// lane (the record's L.x) and its sentinel, and the rule that says whether a record belongs to the frame that rendered
/// it. <see cref="SdfIsaHlsl"/> generates the kernels' spellings of every one of them into <c>sdf-isa.hlsli</c>, so a
/// kernel and a pick read one definition.
/// </summary>
public static class SdfVisibility {
    /// <summary>The bit the identity's kind starts at; the source fills the bits below it.</summary>
    public const int KindShift = 30;
    /// <summary>The identity's source field.</summary>
    public const uint SourceMask = ((1U << KindShift) - 1U);
    /// <summary>The bits the transform-slot lane carries a slot in: a signed word, whose sign only
    /// <see cref="SdfProgram.NoDynamicTransformSlot"/> uses.</summary>
    public const int TransformSlotLaneBits = 31;
    /// <summary>The edge, in pixels, of one unit of the cull-args pass's dispatch box: a hit pass's workgroup edge.</summary>
    public const uint BoxEdgePixels = 8;
    /// <summary>The words of the dispatch box.</summary>
    public const int BoxWords = ((int)(SdfWorldPackage.CullBoundsByteLength / sizeof(uint)));

    // The dispatch box's words in the cull-bounds buffer's order: the pixel axis each word bounds, and whether it is the
    // exclusive upper edge rather than the inclusive lower one, in units of BoxEdgePixels. IsCurrent evaluates this table
    // and CurrencyHlsl spells it for the kernels, so the hit passes and a pick hold one rule.
    private static readonly (int Axis, bool Upper)[] BoxEdges = [(0, false), (1, false), (0, true), (1, true)];

    /// <summary>Gets the kernels' spelling of <see cref="IsCurrent"/>: the body of <c>SDF_VISIBILITY_CURRENT(pixel,
    /// bounds)</c>, over a <c>uint2</c> pixel and the four-word box.</summary>
    public static string CurrencyHlsl { get; } = $"({string.Join(separator: " && ", values: BoxEdges.Select(selector: static (word, index) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"((pixel).{"xy"[word.Axis]} {(word.Upper ? "<" : ">=")} ((bounds)[{index}] * SDF_VISIBILITY_BOX_EDGE))"
    )))})";

    /// <summary>Packs an identity.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="source">The source; only its <see cref="SourceMask"/> bits are kept, as the kernels keep them.</param>
    /// <returns>The identity.</returns>
    public static uint IdentityOf(SdfVisibilityKind kind, uint source) => (((uint)kind) << KindShift) | (source & SourceMask);
    /// <summary>Reads an identity's kind.</summary>
    /// <param name="identity">The identity.</param>
    /// <returns>The kind.</returns>
    public static SdfVisibilityKind KindOf(uint identity) => ((SdfVisibilityKind)(identity >> KindShift));
    /// <summary>Reads an identity's source.</summary>
    /// <param name="identity">The identity.</param>
    /// <returns>The source.</returns>
    public static uint SourceOf(uint identity) => identity & SourceMask;
    /// <summary>Reads the transform-slot lane of an SDF hit's record.</summary>
    /// <param name="word">The record's L.x word.</param>
    /// <returns>The slot, or null for <see cref="SdfProgram.NoDynamicTransformSlot"/>.</returns>
    /// <exception cref="InvalidDataException">The word holds neither the sentinel nor a slot a program can name
    /// (<see cref="SdfProgram.MaxDynamicTransformSlot"/>).</exception>
    public static int? TransformSlotOf(uint word) {
        var slot = unchecked((int)word);

        if (slot == SdfProgram.NoDynamicTransformSlot) {
            return null;
        }
        if ((slot < 0) || (slot > SdfProgram.MaxDynamicTransformSlot)) {
            throw new InvalidDataException(message: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"The visibility record's transform-slot lane holds 0x{word:X8}, which is neither the static sentinel nor a slot in [0, {SdfProgram.MaxDynamicTransformSlot}]."
            ));
        }

        return slot;
    }
    /// <summary>Returns whether a pixel's record belongs to the frame whose cull-args pass wrote a dispatch box: the hit
    /// passes write a record for every pixel inside the box, misses included, and none outside it, where a record is
    /// whatever an earlier frame left. The arithmetic is the kernels' 32-bit unsigned arithmetic.</summary>
    /// <param name="x">The pixel's column in the visibility record's grid.</param>
    /// <param name="y">The pixel's row.</param>
    /// <param name="box">The dispatch box's <see cref="BoxWords"/> words.</param>
    /// <returns>Whether the record is current.</returns>
    /// <exception cref="ArgumentException"><paramref name="box"/> is not <see cref="BoxWords"/> words long.</exception>
    public static bool IsCurrent(uint x, uint y, ReadOnlySpan<uint> box) {
        if (box.Length != BoxWords) {
            throw new ArgumentException(message: $"A dispatch box is {BoxWords} words.", paramName: nameof(box));
        }

        for (var index = 0; (index < BoxEdges.Length); index++) {
            var pixel = ((BoxEdges[index].Axis == 0) ? x : y);
            var edge = unchecked((box[index] * BoxEdgePixels));

            if (BoxEdges[index].Upper ? (pixel >= edge) : (pixel < edge)) {
                return false;
            }
        }

        return true;
    }
}
