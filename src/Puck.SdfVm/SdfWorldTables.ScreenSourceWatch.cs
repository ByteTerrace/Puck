using Puck.Abstractions.Collections;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // What a lighting pass read from screen images at its last render, and whether what it reads now differs: per screen,
    // whether it was read, the source publication, whether that source held unfilled external content, and the
    // screen's mapping bytes. A screen read now or then is compared; one read neither time cannot change the pass.
    // Each pass decides which screens it reads and what a source with no known publication means.
    private sealed class ScreenSourceWatch(SdfWorldTables tables) {
        private readonly ReferenceRevision[] m_sources = new ReferenceRevision[MaxScreenSurfaces];
        private readonly bool[] m_tainted = new bool[MaxScreenSurfaces];
        private readonly bool[] m_renderedScreens = new bool[MaxScreenSurfaces];
        private readonly ReferenceRevision[] m_renderedSources = new ReferenceRevision[MaxScreenSurfaces];
        private readonly bool[] m_renderedTainted = new bool[MaxScreenSurfaces];
        private readonly byte[] m_renderedMappings = new byte[(MaxScreenSurfaces * ScreenMappingStride)];

        // The screens this observation reads.
        public bool[] Screens { get; } = new bool[MaxScreenSurfaces];
        // Whether a source this observation read held unfilled external content.
        public bool Tainted => (Array.IndexOf(array: m_tainted, value: true) >= 0);

        public void Clear() {
            Array.Clear(array: Screens);
            Array.Clear(array: m_sources);
            Array.Clear(array: m_tainted);
        }
        public void Read(int screen) => Screens[screen] = true;
        public void Observe(int screen, ReferenceRevision publication, bool tainted) {
            m_sources[screen] = publication;
            m_tainted[screen] |= tainted;
        }
        public bool Changed() {
            var mappings = tables.m_screenMappingRegion.Contents;

            for (var screen = 0; (screen < MaxScreenSurfaces); screen++) {
                if (!Screens[screen] && !m_renderedScreens[screen]) { continue; }
                var offset = (screen * ScreenMappingStride);

                if (
                    (Screens[screen] != m_renderedScreens[screen]) ||
                    (m_sources[screen] != m_renderedSources[screen]) ||
                    (m_tainted[screen] != m_renderedTainted[screen]) ||
                    !mappings.Slice(length: ScreenMappingByteLength, start: offset).SequenceEqual(other: m_renderedMappings.AsSpan(length: ScreenMappingByteLength, start: offset))
                ) {
                    return true;
                }
            }
            return false;
        }
        public void Commit() {
            Screens.CopyTo(array: m_renderedScreens, index: 0);
            m_sources.CopyTo(array: m_renderedSources, index: 0);
            m_tainted.CopyTo(array: m_renderedTainted, index: 0);
            tables.m_screenMappingRegion.Contents[..m_renderedMappings.Length].CopyTo(destination: m_renderedMappings);
        }
    }
}
