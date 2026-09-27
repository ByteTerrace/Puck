using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// What a view's pass binds of the tables: the World set of the ring slot the frame's upload wrote, holding every table's
// buffer in that slot, the brick pool, the samplers, the glyph atlas and the mesh atlases; and what it binds at a member
// of its pass set whose storage it does not touch. The tables own one World set per ring slot and write both once. They
// write them again only when what they bind moves (the binding revision), which happens only in a frame's upload or a
// host call between frames, before any view records the frame; the rewrite waits for the device to go idle, since every
// view's submission in flight binds them.
public sealed partial class SdfWorldTables {
    // The World set per ring slot, and the binding revision both were last written at, or -1 before the first write.
    private readonly nint[] m_worldSets = new nint[FrameRingSize];
    private long m_worldSetsRevision = -1;

    // Gets the buffer a pass binds at a buffer member it does not touch.
    internal IGpuBuffer DummyBuffer => m_dummyBuffer;
    // Gets the image a pass binds at a sampled member it does not touch, resting shader-readable.
    internal IGpuImage SampledFiller => m_sampledFiller;
    // Gets the image a pass binds at a storage member it does not touch, resting General.
    internal IGpuImage StorageFiller => m_storageFiller;
    // Gets the device's bindings, counted under the tables.
    internal IGpuBindings Bindings => m_bindings;

    // Returns the World set every pass of every view binds for a ring slot. Writes both slots' sets the first time and
    // after the binding revision moves, waiting for the device to go idle before a rewrite.
    internal nint WorldSet(int slot) {
        if (m_worldSetsRevision != m_bindingRevision) {
            if (m_worldSetsRevision >= 0) {
                m_deviceContext.TryWaitIdle();
            }

            for (var ring = 0; (ring < FrameRingSize); ring++) {
                WriteWorldSet(
                    set: m_worldSets[ring],
                    slot: ring
                );
            }

            m_worldSetsRevision = m_bindingRevision;
        }

        return m_worldSets[slot];
    }
    // Returns the binding, in its group, of a resource of the world interface.
    internal static uint WorldBinding(string member) =>
        SdfWorldInterfaces.BindingOf(
            layout: SdfWorldInterfaces.WorldLayout,
            member: member
        );
    // Writes a buffer at a resource of the world interface in a set of its group.
    internal void WriteWorldBuffer(nint set, string member, IGpuBuffer buffer) =>
        WriteBuffer(
            buffer: buffer,
            layout: SdfWorldInterfaces.WorldLayout,
            member: member,
            set: set
        );

    // Writes a ring slot's World set: each table's buffer in the slot, the brick pool, the samplers, the glyph atlas and
    // the mesh atlases, with the sampled filler at an image not set.
    private void WriteWorldSet(nint set, int slot) {
        WriteWorldBuffer(buffer: m_programRegion.Buffer(slot: slot), member: SdfWorldPackage.ProgramWords, set: set);
        WriteWorldBuffer(buffer: m_dynamicTransformRegion.Buffer(slot: slot), member: SdfWorldPackage.DynamicTransforms, set: set);
        WriteWorldBuffer(buffer: m_instanceGridRegion.Buffer(slot: slot), member: SdfWorldPackage.FrameInstanceGrid, set: set);
        WriteWorldBuffer(buffer: m_screenSurfaceRegion.Buffer(slot: slot), member: SdfWorldPackage.ScreenSurfaces, set: set);
        WriteWorldBuffer(buffer: m_screenMappingRegion.Buffer(slot: slot), member: SdfWorldPackage.ScreenMappings, set: set);
        WriteWorldBuffer(buffer: m_screenLightRegion.Buffer(slot: slot), member: SdfWorldPackage.ScreenLights, set: set);
        WriteWorldBuffer(buffer: m_decalRegion.Buffer(slot: slot), member: SdfWorldPackage.DecalCells, set: set);
        WriteWorldBuffer(buffer: m_volumeRegion.Buffer(slot: slot), member: SdfWorldPackage.Volumes, set: set);
        WriteWorldBuffer(buffer: m_meshRegion.Buffer(slot: slot), member: SdfWorldPackage.MeshRegion, set: set);
        WriteWorldBuffer(buffer: m_brickPoolBuffer, member: SdfWorldPackage.BrickPool, set: set);

        var samplers = WorldBinding(member: SdfWorldPackage.Samplers);

        for (var sampler = 0; (sampler < m_samplers.Length); sampler++) {
            m_bindings.WriteSampler(
                arrayElement: ((uint)sampler),
                binding: samplers,
                descriptorSetHandle: set,
                samplerHandle: m_samplers[sampler]
            );
        }

        m_bindings.WriteSampledImage(
            arrayElement: 0,
            binding: WorldBinding(member: SdfWorldPackage.GlyphAtlas),
            descriptorSetHandle: set,
            imageViewHandle: ((m_glyphAtlasView != 0)
                ? m_glyphAtlasView
                : m_sampledFiller.ImageViewHandle)
        );

        for (var usage = 0; (usage < SdfWorldPackage.MeshAtlases.Count); usage++) {
            m_bindings.WriteSampledImage(
                arrayElement: 0,
                binding: WorldBinding(member: SdfWorldPackage.MeshAtlases[usage]),
                descriptorSetHandle: set,
                imageViewHandle: ((m_meshAtlasViews[usage] != 0)
                    ? m_meshAtlasViews[usage]
                    : m_sampledFiller.ImageViewHandle)
            );
        }
    }

    // Writes a ring slot's mesh region into a mesh pass set of the mesh interface.
    internal void WriteMeshTables(nint set, int slot) =>
        WriteBuffer(buffer: m_meshRegion.Buffer(slot: slot), layout: SdfWorldInterfaces.MeshLayout, member: SdfWorldPackage.MeshRegion, set: set);
}
