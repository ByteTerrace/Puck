using Puck.Abstractions.Collections;
using Puck.Maths;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // What a lighting pass read from screen images at its last render, and whether what it reads now differs: per screen,
    // whether it was read, the source publication, whether that source held unfilled external content, and the
    // screen's mapping bytes. A screen read now or then is compared; one read neither time cannot change the pass, so the
    // comparison walks only the members of the two read masks.
    // Each pass decides which screens it reads and what a source with no known publication means.
    private sealed class ScreenSourceWatch(SdfWorldTables tables) {
        private readonly ReferenceRevision[] m_sources = new ReferenceRevision[MaxScreenSurfaces];
        private readonly ReferenceRevision[] m_renderedSources = new ReferenceRevision[MaxScreenSurfaces];
        private readonly byte[] m_renderedMappings = new byte[(MaxScreenSurfaces * ScreenMappingStride)];
        private UInt128 m_tainted;
        private UInt128 m_renderedScreens;
        private UInt128 m_renderedTainted;

        // The screens this observation reads, a bit a screen.
        public UInt128 Screens { get; private set; }
        // Whether a source this observation read held unfilled external content.
        public bool Tainted => (m_tainted != UInt128.Zero);

        public void Clear() {
            foreach (var screen in BitWords.Members(mask: Screens)) { m_sources[screen] = default; }
            Screens = UInt128.Zero;
            m_tainted = UInt128.Zero;
        }
        public void Read(int screen) => Screens |= BitWords.Bit(index: screen);
        public void Observe(int screen, ReferenceRevision publication, bool tainted) {
            m_sources[screen] = publication;
            if (tainted) { m_tainted |= BitWords.Bit(index: screen); }
        }
        // The screens, a bit each, whose reading differs from the last render's.
        public UInt128 ChangedScreens() {
            var mappings = tables.m_screenMappingRegion.Contents;
            var changed = ((Screens ^ m_renderedScreens) | (m_tainted ^ m_renderedTainted));

            foreach (var screen in BitWords.Members(mask: (Screens & m_renderedScreens & ~changed))) {
                var offset = (screen * ScreenMappingStride);

                if (
                    (m_sources[screen] != m_renderedSources[screen]) ||
                    !mappings.Slice(length: ScreenMappingByteLength, start: offset).SequenceEqual(other: m_renderedMappings.AsSpan(length: ScreenMappingByteLength, start: offset))
                ) {
                    changed |= BitWords.Bit(index: screen);
                }
            }
            return changed;
        }
        public void Commit() {
            foreach (var screen in BitWords.Members(mask: (Screens | m_renderedScreens))) {
                var offset = (screen * ScreenMappingStride);

                m_renderedSources[screen] = m_sources[screen];
                tables.m_screenMappingRegion.Contents.Slice(length: ScreenMappingByteLength, start: offset).CopyTo(destination: m_renderedMappings.AsSpan(start: offset));
            }
            m_renderedScreens = Screens;
            m_renderedTainted = m_tainted;
        }
    }
}
