using System.Numerics;

namespace Puck.Overlays;

public sealed partial class OverlayFrameBuilder {
    private const int TileEdge = 16;
    private const int MaskBits = 32;
    private const int TileHeaderWords = 2;

    internal int TileBaseWords => ClipBaseWords + MaxClips * ClipWords;
    internal int TileElementWords => ElementMaskWords(m_elementCount);
    internal int TileIndexWords { get; private set; }

    private static int TileCount(uint pixels) => checked((int)((pixels + TileEdge - 1) / TileEdge));
    private static int ElementMaskWords(int elements) => (elements + MaskBits - 1) / MaskBits;
    private static int AxisStride(int elementWords) => 1 + ElementMaskWords(elementWords) + elementWords;
    private static int TileWords(uint width, uint height, int elementWords) =>
        checked(TileHeaderWords + elementWords + (TileCount(width) + TileCount(height)) * AxisStride(elementWords));

    // The intersection of a tile column and row's masks is the ordered set of records reaching that tile. Storing
    // the axes independently bounds the index even if every record covers the whole screen.
    internal void BuildTileIndex(uint sourceWidth, uint sourceHeight) {
        var dims = new Vector2(sourceWidth, sourceHeight);
        var elementWords = TileElementWords;
        var tileColumns = TileCount(Width);
        var stride = AxisStride(elementWords);
        var maskBase = TileBaseWords + TileHeaderWords;
        var axisBase = maskBase + elementWords;
        var edgeAa = ReadFloat(OverlayTokenBlock.RoleCount * 4 + (int)OverlayTokenBlock.Scalar.EdgeAa);
        var halo = ReadFloat(OverlayTokenBlock.RoleCount * 4 + (int)OverlayTokenBlock.Scalar.GlowHaloBlur);

        TileIndexWords = TileWords(Width, Height, elementWords);
        Array.Clear(m_scratch, TileBaseWords, TileIndexWords);
        m_scratch[TileBaseWords] = (uint)tileColumns;
        BinRecords(m_panelCount, PanelBaseWords, PanelWords, panels: true);
        BinRecords(m_elementCount, ElementBaseWords, ElementWords, panels: false);

        void BinRecords(int count, int recordBase, int recordWords, bool panels) {
            var lastClip = uint.MaxValue;
            (Vector2 Low, Vector2 High) clipBounds = default;
            for (var index = 0; index < count; index++) {
                var record = recordBase + index * recordWords;
                var clip = m_scratch[record + 9];
                if (clip != lastClip) {
                    lastClip = clip;
                    clipBounds = clip == 0
                        ? (Vector2.Zero, new Vector2(Width, Height))
                        : RectOf(ClipBaseWords + ((int)clip - 1) * ClipWords, dims, margin: 1);
                }
                if ((clipBounds.High.X < 0) || (clipBounds.High.Y < 0) ||
                    (clipBounds.Low.X > Width) || (clipBounds.Low.Y > Height) ||
                    (clipBounds.Low.X > clipBounds.High.X) || (clipBounds.Low.Y > clipBounds.High.Y)) {
                    var end = m_scratch[ClipBaseWords + ((int)clip - 1) * ClipWords + (panels ? 4 : 5)];
                    if (end > index) { index = (int)end - 1; }
                    continue;
                }

                var kind = m_scratch[record + 4] & 15u;
                var (low, high) = panels
                    ? RectOf(record, dims, (m_scratch[record + 6] == 0 ? edgeAa : halo) + 1)
                    : ElementBounds(record, dims, kind, edgeAa, halo);
                low = Vector2.Max(low, clipBounds.Low);
                high = Vector2.Min(high, clipBounds.High);
                if ((high.X < 0) || (high.Y < 0) || (low.X > Width) || (low.Y > Height) ||
                    (low.X > high.X) || (low.Y > high.Y)) { continue; }
                var word = index / MaskBits;
                var bit = 1u << (index % MaskBits);
                if (!panels && (kind is 6u or 7u)) {
                    m_scratch[TileBaseWords + 1] = 1;
                    m_scratch[maskBase + word] |= bit;
                }

                BinAxis(low.X, high.X, 0, tileColumns);
                BinAxis(low.Y, high.Y, tileColumns, TileCount(Height));

                void BinAxis(float minimum, float maximum, int axis, int tiles) {
                    var first = (int)(Math.Clamp(minimum, 0, tiles * TileEdge - 1) / TileEdge);
                    var last = (int)(Math.Clamp(maximum, 0, tiles * TileEdge - 1) / TileEdge);
                    for (var tile = first; tile <= last; tile++) {
                        var offset = axisBase + (axis + tile) * stride;
                        if (panels) {
                            m_scratch[offset] |= bit;
                        } else {
                            m_scratch[offset + 1 + word / MaskBits] |= 1u << (word % MaskBits);
                            m_scratch[offset + 1 + ElementMaskWords(elementWords) + word] |= bit;
                        }
                    }
                }
            }
        }
    }

    private (Vector2 Low, Vector2 High) ElementBounds(int record, Vector2 dims, uint kind, float edgeAa, float halo) {
        var origin = ReadPoint(record) * dims;
        var ab = ReadPoint(record + 2);
        switch (kind) {
            case 0: {
                var cellHeight = ab.Y * dims.Y;
                var cellWidth = m_glyphs.AtlasCellWidth * (cellHeight / m_glyphs.AtlasCellHeight);
                return Bounds(origin - new Vector2(cellWidth, 0),
                    origin + new Vector2(ab.X * dims.X * m_scratch[record + 6] + cellWidth, cellHeight), 1);
            }
            case 1:
            case 4:
                return RectOf(record, dims, edgeAa + 1);
            case 3: {
                var halfWidth = ReadFloat(record + 10);
                var support = edgeAa + (halfWidth > 0 ? halfWidth : 0.5f) + 0.5f;
                // Outside the ellipse, the distance estimate is at least (k0 - 1) times the smaller radius.
                var radii = Vector2.Max(ab, Vector2.One);
                var reach = ab.Y > 0
                    ? radii * (1 + support / Math.Min(radii.X, radii.Y))
                    : new Vector2(ab.X + support);
                return Bounds(origin - reach, origin + reach, 1);
            }
            case 5: {
                var reach = new Vector2(ab.Y + halo + edgeAa);
                return Bounds(origin - reach, origin + reach, 1);
            }
            case 6:
            case 7:
                return RectOf(record, dims, margin: 1);
            case 8: {
                var end = ab * dims;
                return Bounds(Vector2.Min(origin, end), Vector2.Max(origin, end), 2.5f);
            }
            default: {
                var reach = new Vector2(ab.X * 2.2f);
                var pressed = (m_scratch[record + 6] & 0x100) != 0;
                origin.Y += pressed ? 1 : 0;
                return Bounds(origin - reach, origin + reach, 1);
            }
        }
    }

    private (Vector2 Low, Vector2 High) RectOf(int record, Vector2 dims, float margin) {
        var origin = ReadPoint(record) * dims;
        return Bounds(origin, origin + ReadPoint(record + 2) * dims, margin);
    }

    // A pixel of padding covers normalized-coordinate round trips and the shader's inclusive boundaries.
    // Nonfinite bounds stay conservative, leaving the shader to apply the record's original tests.
    private static (Vector2 Low, Vector2 High) Bounds(Vector2 low, Vector2 high, float margin) => (
        new Vector2(Lower(low.X - margin), Lower(low.Y - margin)), new Vector2(Upper(high.X + margin), Upper(high.Y + margin)));

    private Vector2 ReadPoint(int word) => new(ReadFloat(word), ReadFloat(word + 1));
    private float ReadFloat(int word) => BitConverter.UInt32BitsToSingle(m_scratch[word]);

    private static float Lower(float value) => float.IsFinite(value) ? value : float.MinValue;
    private static float Upper(float value) => float.IsFinite(value) ? value : float.MaxValue;
}
