using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Assets;
using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>The ways a seat displays its held frame beside its current view.</summary>
public enum WorldCompareMode : byte {
    /// <summary>Shows the live view alone.</summary>
    Off,
    /// <summary>Shows held pixels to the left of the wipe and live pixels to its right.</summary>
    Wipe,
    /// <summary>Fits the whole held and live views into the left and right halves.</summary>
    Split,
    /// <summary>Shows the absolute RGB difference.</summary>
    Diff,
}
/// <summary>A seat's held display pixels and session-only comparison controls. The image belongs to this snapshot;
/// replacing a hold leaves any upload still reading its predecessor valid.</summary>
public sealed class WorldCompareSnapshot {
    /// <summary>Creates a held frame.</summary>
    /// <param name="image">The owned display pixels.</param>
    /// <param name="tick">The state tick shown by the image.</param>
    /// <param name="sequence">The hold's session ordinal.</param>
    public WorldCompareSnapshot(PngImage image, ulong tick, ulong sequence) {
        Image = image;
        Tick = tick;
        Sequence = sequence;
    }

    /// <summary>Gets the seat's tightly packed, display-referred RGBA8 pixels.</summary>
    public PngImage Image { get; }
    /// <summary>Gets the state tick the captured frame presents.</summary>
    public ulong Tick { get; }
    /// <summary>Gets the hold's session ordinal.</summary>
    public ulong Sequence { get; }
    /// <summary>Gets the current presentation mode.</summary>
    public WorldCompareMode Mode { get; internal set; }
    /// <summary>Gets the wipe's position across the seat's view, from zero to one.</summary>
    public float Wipe { get; internal set; } = 0.5f;
    /// <summary>Gets the most recent requested comparison against this hold.</summary>
    public RgbaFrameDifference? Difference { get; internal set; }
}
/// <summary>Per-seat held frames. Captures and comparisons use the displayed bytes and the shared canary difference
/// metric; no held image or comparison control enters the authored document or simulation state.</summary>
public sealed class WorldFrameComparison {
    /// <summary>The registered upload package for held display pixels.</summary>
    public const string SourcePackage = (RenderGraphInstance.SourcePackagePrefix + SourceProducer);
    /// <summary>The ordinary image-source producer id of a held comparison image.</summary>
    public const string SourceProducer = "editorCompare";

    private readonly WorldCompareSnapshot?[] m_seats = new WorldCompareSnapshot[PlayerRoster.MaxSlots];

    private ulong m_sequence;

    /// <summary>Gets the revision of the set of active uploads and comparison passes.</summary>
    public ulong Revision { get; private set; }
    /// <summary>Gets whether any seat currently displays a comparison.</summary>
    public bool Active => m_seats.Any(predicate: static seat => (seat is { Mode: not WorldCompareMode.Off }));

    /// <summary>Gets one seat's hold, or null before it holds a frame.</summary>
    /// <param name="slot">The zero-based seat.</param>
    public WorldCompareSnapshot? Seat(int slot) => m_seats[slot];
    /// <summary>Holds the captured seat viewport, replacing its previous hold only after all bounds are checked.</summary>
    /// <param name="slot">The zero-based seat.</param>
    /// <param name="frame">The ordinary capture's full display image.</param>
    /// <param name="view">The viewport published for exactly that captured frame.</param>
    /// <param name="tick">The capture's state tick.</param>
    public void Hold(int slot, PngImage frame, WorldSeatView view, ulong tick) {
        var image = Crop(frame: frame, view: view);
        var previous = m_seats[slot];

        m_seats[slot] = new WorldCompareSnapshot(image: image, tick: tick, sequence: ++m_sequence) {
            Mode = (previous?.Mode ?? WorldCompareMode.Off),
            Wipe = (previous?.Wipe ?? 0.5f),
            Difference = new RgbaFrameDifference(ChangedPixels: 0, MaxDelta: 0),
        };
        Revision++;
    }
    /// <summary>Sets a held frame's display mode and optional wipe position.</summary>
    /// <param name="slot">The zero-based seat.</param>
    /// <param name="mode">The requested comparison display.</param>
    /// <param name="wipe">The position in zero to one, or null to retain the previous position.</param>
    /// <exception cref="InvalidOperationException">The seat has no hold and requests an active mode.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The mode or wipe position is invalid.</exception>
    public void SetMode(int slot, WorldCompareMode mode, float? wipe = null) {
        if (!Enum.IsDefined(value: mode)) { throw new ArgumentOutOfRangeException(paramName: nameof(mode)); }
        if ((wipe is { } position) && (!float.IsFinite(f: position) || (position < 0f) || (position > 1f))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(wipe));
        }
        var held = m_seats[slot];

        if (held is null) {
            if (mode == WorldCompareMode.Off) { return; }
            throw new InvalidOperationException(message: "hold a frame before choosing a comparison mode");
        }
        if ((held.Mode == WorldCompareMode.Off) != (mode == WorldCompareMode.Off)) { Revision++; }
        held.Mode = mode;
        if (wipe is { } value) { held.Wipe = value; }
    }
    /// <summary>Counts changed pixels against a current ordinary capture of the same seat extent.</summary>
    /// <param name="slot">The zero-based seat.</param>
    /// <param name="frame">The current root capture.</param>
    /// <param name="view">The viewport published for that captured frame.</param>
    /// <returns>The shared RGB difference metric.</returns>
    /// <exception cref="InvalidOperationException">The seat has no hold, no visible viewport or a different crop extent.</exception>
    public RgbaFrameDifference Measure(int slot, PngImage frame, WorldSeatView view) {
        var held = (m_seats[slot] ?? throw new InvalidOperationException(message: "hold a frame before comparing"));
        var current = Crop(frame: frame, view: view);

        if ((current.Width != held.Image.Width) || (current.Height != held.Image.Height)) {
            throw new InvalidOperationException(message: "the seat extent changed; hold a new frame before counting differences");
        }
        var difference = RgbaFrameDifference.Measure(before: held.Image.RgbaPixels, after: current.RgbaPixels);

        held.Difference = difference;
        return difference;
    }
    /// <summary>Crops a seat using place's pixel-edge rounding. Its normalized viewport is applied to the captured
    /// root's extent, which may differ from the display extent. An absent or empty viewport is refused.</summary>
    /// <param name="frame">The captured root image.</param>
    /// <param name="view">The viewport published for that captured frame.</param>
    /// <returns>An owned, tightly packed RGBA8 image of the seat.</returns>
    /// <exception cref="InvalidOperationException">The capture or seat viewport is absent or empty.</exception>
    public static PngImage Crop(PngImage frame, WorldSeatView view) {
        if (!view.Present || (frame.Width <= 0) || (frame.Height <= 0) ||
            (frame.RgbaPixels.Length != checked(((frame.Width * frame.Height) * 4)))) {
            throw new InvalidOperationException(message: "the captured frame has no matching seat viewport");
        }
        var rect = view.Region;

        static int Edge(float fraction, int extent) => Math.Clamp(value: ((int)MathF.Floor(x: ((fraction * extent) + 0.5f))), min: 0, max: extent);
        var left = Edge(fraction: rect.X, extent: frame.Width);
        var top = Edge(fraction: rect.Y, extent: frame.Height);
        var right = Edge(fraction: (rect.X + rect.Width), extent: frame.Width);
        var bottom = Edge(fraction: (rect.Y + rect.Height), extent: frame.Height);

        if ((right <= left) || (bottom <= top)) { throw new InvalidOperationException(message: "the seat viewport covers no pixels"); }
        var width = (right - left);
        var height = (bottom - top);
        var pixels = new byte[checked(((width * height) * 4))];

        for (var y = 0; (y < height); y++) {
            frame.RgbaPixels.AsSpan(start: ((((top + y) * frame.Width) + left) * 4), length: (width * 4))
                .CopyTo(destination: pixels.AsSpan(start: ((y * width) * 4)));
        }
        return new PngImage(Height: height, RgbaPixels: pixels, Width: width);
    }
    /// <summary>Opens an immutable hold through the runtime's ordinary uploaded-image conversion.</summary>
    /// <param name="snapshot">The hold retained for the upload's lifetime.</param>
    /// <returns>The ordinary static image-source upload.</returns>
    public static IRenderGraphSourceUpload Open(WorldCompareSnapshot snapshot) => new Upload(snapshot: snapshot);

    private sealed class Upload(WorldCompareSnapshot snapshot) : IRenderGraphSourceUpload {
        public ImageSourceDescriptor Descriptor { get; } = new(
            Producer: SourceProducer, Transport: ImageSourceTransport.Uploaded,
            Width: ((uint)snapshot.Image.Width), Height: ((uint)snapshot.Image.Height),
            Format: ImagePixelFormat.R8G8B8A8Unorm,
            // The ordinary RGBA source pass copies display-referred bytes without decoding their transfer again.
            Color: ImageColorEncoding.Srgb, Cadence: ImageSourceCadence.Static, Content: ImageContentClass.Presentation);
        public string? Fault => null;

        public bool TryWrite(long tick, GpuRegion region) {
            _ = region.Write(bytes: snapshot.Image.RgbaPixels, offset: ImageSourceUploadLayout.HeaderBytes);
            return true;
        }
        public void Dispose() { }
    }
}
