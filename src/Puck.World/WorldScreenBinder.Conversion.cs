using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // The image source format of CPU pixels a capture or camera tier hands over, or null for one no conversion reads.
    private static ImagePixelFormat? PixelFormatOf(GpuPixelFormat format) => format switch {
        GpuPixelFormat.B8G8R8A8Unorm => ImagePixelFormat.B8G8R8A8Unorm,
        GpuPixelFormat.R8G8B8A8Unorm => ImagePixelFormat.R8G8B8A8Unorm,
        _ => null,
    };
    // Converts one CPU surface through a tier's conversion, when the runtime runs and a conversion reads its pixels.
    private bool TryConvert(ConvertedPixels pixels, in FrameContext context, in Surface surface) {
        if (
            (Runtime is not { } runtime) ||
            !surface.IsCpuPixels ||
            (0U == surface.Width) ||
            (0U == surface.Height) ||
            (PixelFormatOf(format: surface.Format) is not { } format)
        ) {
            return false;
        }

        var byteCount = checked((int)((surface.Width * surface.Height) * 4U));

        return ((surface.Pixels.Length >= byteCount) && pixels.TryConvert(
            context: in context,
            format: format,
            height: surface.Height,
            planes: surface.Pixels.Span[..byteCount],
            runtime: runtime,
            width: surface.Width
        ));
    }

    // A CPU tier's pixels — a camera's or a desktop capture's, or a capture fill — converted through the image source
    // conversion their format names (RenderGraphRuntime.CreateConverter) into the image a frame samples. A frame acquires
    // the image under a counted lease, so a converter the pixels' new extent or format replaced, or one retired with its
    // owner, is disposed only once no submitted frame samples it; a replaced converter's image is shown until its
    // replacement has converted.
    private sealed class ConvertedPixels {
        private readonly ImageContentClass m_content;
        private readonly string m_name;
        private readonly string m_producer;
        private readonly Action<int> m_release;
        // Converters the pixels no longer convert through, each disposed when no frame holds its image.
        private readonly List<Entry> m_retiring = [];

        private Entry? m_current;
        private int m_nextToken;
        private bool m_retired;
        // The converter whose image a frame acquires: the current one once it has converted, else the one before it.
        private Entry? m_shown;

        public ConvertedPixels(string name, string producer, ImageContentClass content) {
            m_content = content;
            m_name = name;
            m_producer = producer;
            m_release = Release;
        }

        // The image-view handle a frame samples, for a read that submits no GPU work; zero before the first conversion.
        public nint Handle => (m_shown?.Converter.ImageViewHandle ?? 0);

        private void Release(int token) {
            var entry = ((m_current?.Token == token)
                ? m_current
                : ((m_shown?.Token == token)
                    ? m_shown
                    : m_retiring.Find(match: retiring => (retiring.Token == token))));

            if (entry is null) {
                return;
            }

            --entry.Outstanding;

            if (
                entry.Retired &&
                (0 == entry.Outstanding)
            ) {
                _ = m_retiring.Remove(item: entry);
                entry.Converter.Dispose();
            }
        }
        private void Retire(Entry entry) {
            entry.Retired = true;

            if (0 == entry.Outstanding) {
                entry.Converter.Dispose();
            } else {
                m_retiring.Add(item: entry);
            }
        }

        // Acquires the image a frame samples, held until that frame's submission has retired.
        public GpuImageLease Acquire() {
            if (
                m_retired ||
                (m_shown is not { } shown) ||
                (shown.Converter.ImageViewHandle == 0)
            ) {
                return 0;
            }

            ++shown.Outstanding;

            return new GpuImageLease(
                ImageViewHandle: shown.Converter.ImageViewHandle,
                Release: m_release,
                ReleaseToken: shown.Token
            );
        }
        // Drops every converter's device objects after a device loss; each converts again on the recreated device.
        public void OnDeviceLost() {
            m_current?.Converter.OnDeviceLost();

            if (!ReferenceEquals(
                objA: m_shown,
                objB: m_current
            )) {
                m_shown?.Converter.OnDeviceLost();
            }

            foreach (var entry in m_retiring) {
                entry.Converter.OnDeviceLost();
            }
        }
        // Retires every converter with the pixels' owner.
        public void Retire() {
            if (m_retired) {
                return;
            }

            m_retired = true;

            if (m_current is { } current) {
                Retire(entry: current);
            }

            if (
                (m_shown is { } shown) &&
                !ReferenceEquals(
                    objA: shown,
                    objB: m_current
                )
            ) {
                Retire(entry: shown);
            }

            m_current = null;
            m_shown = null;
        }
        // Converts one image of the given format and extent, making a converter for it when the pixels change shape.
        public bool TryConvert(RenderGraphRuntime runtime, in FrameContext context, ImagePixelFormat format, uint width, uint height, ReadOnlySpan<byte> planes) {
            if (m_retired) {
                return false;
            }

            if (
                (m_current is not { } current) ||
                (current.Format != format) ||
                (current.Width != width) ||
                (current.Height != height)
            ) {
                // A converter replaced again before it ever converted was never shown.
                if (
                    (m_current is { } unshown) &&
                    !ReferenceEquals(
                        objA: unshown,
                        objB: m_shown
                    )
                ) {
                    Retire(entry: unshown);
                }

                current = new Entry(
                    converter: runtime.CreateConverter(
                        descriptor: new ImageSourceDescriptor(
                            Cadence: ImageSourceCadence.Tick,
                            Color: ImageColorEncoding.Srgb,
                            Content: m_content,
                            Format: format,
                            Height: height,
                            Producer: m_producer,
                            Transport: ImageSourceTransport.Imported,
                            Width: width
                        ),
                        name: m_name
                    ),
                    format: format,
                    height: height,
                    token: m_nextToken++,
                    width: width
                );
                m_current = current;
            }

            if (!current.Converter.TryConvert(
                context: in context,
                planes: planes
            )) {
                return false;
            }

            if (!ReferenceEquals(
                objA: m_shown,
                objB: current
            )) {
                var previous = m_shown;

                m_shown = current;

                if (previous is not null) {
                    Retire(entry: previous);
                }
            }

            return true;
        }

        // One converter and the frames still holding its image.
        private sealed class Entry(RenderGraphSourceConverter converter, ImagePixelFormat format, uint width, uint height, int token) {
            public RenderGraphSourceConverter Converter { get; } = converter;
            public ImagePixelFormat Format { get; } = format;
            public uint Height { get; } = height;

            public int Outstanding { get; set; }
            public bool Retired { get; set; }

            public int Token { get; } = token;
            public uint Width { get; } = width;
        }
    }
}
