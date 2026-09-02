using System.Numerics;

namespace Puck.Commands;

/// <summary>The viewport edge a binding-bar anchor group hangs from.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<BindingBarEdge>))]
public enum BindingBarEdge {
    /// <summary>The bottom edge; the group is centered left-to-right and its lowest plate sits at the inset.</summary>
    Bottom,
    /// <summary>The top edge; centered left-to-right, highest plate at the inset.</summary>
    Top,
    /// <summary>The left edge; centered top-to-bottom, leftmost plate at the inset.</summary>
    Left,
    /// <summary>The right edge; centered top-to-bottom, rightmost plate at the inset.</summary>
    Right,
}
/// <summary>One anchor group of a compiled layout: an edge, an inset, and the extent of every plate of every bank
/// hanging there, in button pitches. Plates in the group carry pitches normalized to it — along the edge's axis 0 is
/// the plate whose edge touches the inset; across it 0 is the group's center. Inward is the direction of INCREASING
/// pitch for a <see cref="BindingBarEdge.Bottom"/> or <see cref="BindingBarEdge.Left"/> group and DECREASING pitch
/// for a <see cref="BindingBarEdge.Top"/> or <see cref="BindingBarEdge.Right"/> one, because the pitch axes are the
/// overlay's own (x right, y up) rather than each edge's: <see cref="CompiledBindingBarLayout.Build"/> shifts a top/right group by its
/// MAXIMUM, so its normalized pitches run from 0 down through negatives. <c>BindingBarLayout.PlateCenter</c> is
/// written against exactly that sign per edge.</summary>
/// <param name="Edge">The edge.</param>
/// <param name="Inset">The inset from the edge, pitches.</param>
/// <param name="Along">The farthest plate center from the inset line along the edge's axis, pitches (≥ 0).</param>
/// <param name="Across">The span between the outermost plate centers across the edge's axis, pitches (≥ 0).</param>
public readonly record struct BindingBarFrame(BindingBarEdge Edge, float Inset, float Along, float Across) {
    /// <summary>Gets a value indicating whether the edge runs vertically (left or right), so "along" is x.</summary>
    public bool Sideways => (Edge is BindingBarEdge.Left or BindingBarEdge.Right);
}
/// <summary>One bank of a compiled layout: the frame it hangs in and its plates by source, pitches already
/// normalized to that frame.</summary>
/// <param name="Frame">The index into <see cref="CompiledBindingBarLayout.Frames"/>.</param>
/// <param name="Plates">The plates by input source id.</param>
public sealed record CompiledBindingBarBank(int Frame, IReadOnlyDictionary<string, BindingPlatePlacement> Plates);
/// <summary>A binding-bar layout with every derivation done: the anchor groups (frames) with their extents, and
/// each bank's plates normalized into its frame. Built once per document; a tick only looks things up.</summary>
/// <param name="Frames">The anchor groups.</param>
/// <param name="Banks">The banks by id; a bank the layout does not place is absent.</param>
public sealed record CompiledBindingBarLayout(ReadOnlyMemory<BindingBarFrame> Frames, IReadOnlyDictionary<string, CompiledBindingBarBank> Banks) {
    /// <summary>Gets the layout with no frames and no banks — nothing placed, nothing drawn.</summary>
    public static CompiledBindingBarLayout Empty { get; } = new(
        Banks: new Dictionary<string, CompiledBindingBarBank>(comparer: StringComparer.Ordinal),
        Frames: ReadOnlyMemory<BindingBarFrame>.Empty
    );

    /// <summary>Returns the button size at which every frame fits a region of the given aspect: the authored size,
    /// shrunk until each frame's inset plus its extent along its edge's axis fits that axis and its extent across
    /// fits the other. One uniform size, so the frames keep their relationships.</summary>
    /// <param name="buttonSize">The authored (scaled) button size, region-height units.</param>
    /// <param name="aspect">The region's width over its height.</param>
    public float FitButtonSize(float buttonSize, float aspect) =>
        FitButtonSize(
            aspect: aspect,
            buttonSize: buttonSize,
            frames: Frames.Span
        );
    /// <summary>Returns the button size at which every one of <paramref name="frames"/> fits a region of the given
    /// aspect — see <see cref="FitButtonSize(float, float)"/>.</summary>
    /// <param name="frames">The frames.</param>
    /// <param name="buttonSize">The authored (scaled) button size, region-height units.</param>
    /// <param name="aspect">The region's width over its height.</param>
    public static float FitButtonSize(ReadOnlySpan<BindingBarFrame> frames, float buttonSize, float aspect) {
        var fitted = buttonSize;

        foreach (var frame in frames) {
            // Plates are one pitch wide: a frame spans (extent + 1) pitches, plus its inset along its own axis.
            var alongPitches = ((frame.Along + 1f) + frame.Inset);
            var acrossPitches = (frame.Across + 1f);

            var (alongLimit, acrossLimit) = (frame.Sideways
                ? (aspect, 1f)
                : (1f, aspect)
            );

            fitted = MathF.Min(x: fitted, y: (alongLimit / alongPitches));
            fitted = MathF.Min(x: fitted, y: (acrossLimit / acrossPitches));
        }

        return fitted;
    }
    /// <summary>Builds a compiled layout from raw bank tables: every plate of every bank sharing an edge and inset
    /// forms one frame, and each frame's plates are shifted so its nearest plate sits at pitch 0 along the edge's
    /// axis and its extent is centered across it. Every plate counts toward the extent, bound or not, so what a
    /// frame shows never moves it.</summary>
    /// <param name="banks">Each bank's edge, inset, and raw plates (pitches in the author's own origin).</param>
    public static CompiledBindingBarLayout Build(IEnumerable<(string Id, BindingBarEdge Edge, float Inset, IReadOnlyDictionary<string, BindingPlatePlacement> Plates)> banks) {
        ArgumentNullException.ThrowIfNull(argument: banks);

        var frameIndex = new Dictionary<(BindingBarEdge, float), int>();
        var frameBounds = new List<(BindingBarEdge Edge, float Inset, float MinX, float MaxX, float MinY, float MaxY)>();
        var raw = new List<(string Id, int Frame, IReadOnlyDictionary<string, BindingPlatePlacement> Plates)>();

        foreach (var (id, edge, inset, plates) in banks) {
            if (!frameIndex.TryGetValue(
                key: (edge, inset),
                value: out var frame
            )) {
                frame = frameBounds.Count;
                frameIndex[(edge, inset)] = frame;
                frameBounds.Add(item: (edge, inset, float.MaxValue, float.MinValue, float.MaxValue, float.MinValue));
            }

            var bounds = frameBounds[frame];

            foreach (var plate in plates.Values) {
                bounds.MinX = MathF.Min(x: bounds.MinX, y: plate.Position.X);
                bounds.MaxX = MathF.Max(x: bounds.MaxX, y: plate.Position.X);
                bounds.MinY = MathF.Min(x: bounds.MinY, y: plate.Position.Y);
                bounds.MaxY = MathF.Max(x: bounds.MaxY, y: plate.Position.Y);
            }

            frameBounds[frame] = bounds;
            raw.Add(item: (id, frame, plates));
        }

        var frames = new BindingBarFrame[frameBounds.Count];
        var shifts = new Vector2[frameBounds.Count];

        for (var index = 0; (index < frames.Length); index++) {
            var (edge, inset, minX, maxX, minY, maxY) = frameBounds[index];
            var empty = (minX > maxX);

            if (empty) {
                (minX, maxX, minY, maxY) = (0f, 0f, 0f, 0f);
            }

            var shift = new Vector2(
                x: (edge switch {
                    BindingBarEdge.Left => minX,
                    BindingBarEdge.Right => maxX,
                    _ => ((minX + maxX) * 0.5f),
                }),
                y: (edge switch {
                    BindingBarEdge.Bottom => minY,
                    BindingBarEdge.Top => maxY,
                    _ => ((minY + maxY) * 0.5f),
                })
            );
            var sideways = (edge is BindingBarEdge.Left or BindingBarEdge.Right);

            shifts[index] = shift;
            frames[index] = new BindingBarFrame(
                Across: (sideways
                    ? (maxY - minY)
                    : (maxX - minX)),
                Along: (sideways
                    ? (maxX - minX)
                    : (maxY - minY)),
                Edge: edge,
                Inset: inset
            );
        }

        var compiled = new Dictionary<string, CompiledBindingBarBank>(comparer: StringComparer.Ordinal);

        foreach (var (id, frame, plates) in raw) {
            var normalized = new Dictionary<string, BindingPlatePlacement>(capacity: plates.Count, comparer: StringComparer.Ordinal);

            foreach (var (source, plate) in plates) {
                normalized[source] = plate with { Position = (plate.Position - shifts[frame]), };
            }

            compiled[id] = new CompiledBindingBarBank(
                Frame: frame,
                Plates: normalized
            );
        }

        return new CompiledBindingBarLayout(
            Banks: compiled,
            Frames: frames
        );
    }
}
