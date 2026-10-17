using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// What a view's pass binds of the tables: the World set of the ring slot the frame's upload wrote, holding every table's
// buffer in that slot (the lights and the sky's among them), the brick pool, the samplers, the glyph atlas and the mesh atlases; and what it binds at a member
// of its pass set whose storage it does not touch. The tables own one World set per ring slot and write each when a view
// first binds it after what they bind moved (the binding revision), which happens only in a frame's pack or upload or a
// host call between frames. A view binds only the set of the slot the frame's upload took, and that upload waited the
// previous one's fence, which one queue signals after every view that bound the slot's set before, so a set is rewritten
// without waiting for the device; what it bound before is kept until no view can read it (Retire), or was replaced
// once the device was idle.
public sealed partial class SdfWorldTables {
    // The World set per ring slot, and the binding revision each was last written at, or -1 before its first write.
    private readonly nint[] m_worldSets = new nint[FrameRingSize];
    private readonly long[] m_worldSetRevisions = Enumerable.Repeat(count: FrameRingSize, element: -1L).ToArray();

    // Gets the buffer a pass binds at a buffer member it does not touch.
    internal IGpuBuffer DummyBuffer => m_dummyBuffer;
    // Gets the image a pass binds at a sampled member it does not touch, resting shader-readable.
    internal IGpuImage SampledFiller => m_sampledFiller;
    // Gets the image a pass binds at a storage member it does not touch, resting General.
    internal IGpuImage StorageFiller => m_storageFiller;
    // Gets the device's bindings, counted under the tables.
    internal IGpuBindings Bindings => m_bindings;

    // Returns the World set every pass of every view binds for the ring slot the latest upload took, written first when
    // the binding revision moved since it was last written.
    internal nint WorldSet(int slot) {
        if (m_worldSetRevisions[slot] != m_bindingRevision) {
            WriteWorldSet(
                set: m_worldSets[slot],
                slot: slot
            );
            m_worldSetRevisions[slot] = m_bindingRevision;
        }

        return m_worldSets[slot];
    }
    // Returns the binding, in its group, of a resource of the world interface.
    internal static uint WorldBinding(string member) =>
        SdfKernelInterfaces.BindingOf(
            layout: SdfWorldInterfaces.WorldLayout,
            member: member
        );
    // Writes a buffer at a resource of the world interface in a set of its group.
    internal void WriteWorldBuffer(nint set, string member, IGpuBuffer buffer) =>
        WriteInterfaceBuffer(
            buffer: buffer,
            layout: SdfWorldInterfaces.WorldLayout,
            member: member,
            set: set
        );

    // Writes a ring slot's World set: each table's buffer in the slot, the brick pool, the samplers, the glyph atlas and
    // the mesh and impostor atlases, with the sampled filler at an image not set.
    private void WriteWorldSet(nint set, int slot) {
        WriteWorldBuffer(buffer: m_programRegion.Buffer(slot: slot), member: SdfWorldPackage.ProgramWords, set: set);
        WriteWorldBuffer(buffer: m_dynamicTransformRegion.Buffer(slot: slot), member: SdfWorldPackage.DynamicTransforms, set: set);
        WriteWorldBuffer(buffer: m_previousDynamicTransforms, member: SdfWorldPackage.PreviousDynamicTransforms, set: set);
        WriteWorldBuffer(buffer: m_previousMeshTransforms, member: SdfWorldPackage.PreviousMeshTransforms, set: set);
        WriteWorldBuffer(buffer: m_screenSurfaceRegion.Buffer(slot: slot), member: SdfWorldPackage.ScreenSurfaces, set: set);
        WriteWorldBuffer(buffer: m_screenMappingRegion.Buffer(slot: slot), member: SdfWorldPackage.ScreenMappings, set: set);
        WriteWorldBuffer(buffer: ScreenEmission, member: SdfWorldPackage.ScreenLights, set: set);
        WriteWorldBuffer(buffer: m_decalRegion.Buffer(slot: slot), member: SdfWorldPackage.DecalCells, set: set);
        WriteWorldBuffer(buffer: m_volumeRegion.Buffer(slot: slot), member: SdfWorldPackage.Volumes, set: set);
        WriteWorldBuffer(buffer: m_meshRegion.Buffer(slot: slot), member: SdfWorldPackage.MeshRegion, set: set);
        WriteWorldBuffer(buffer: m_meshBakePointRegion.Buffer(slot: slot), member: SdfWorldPackage.MeshBakePoints, set: set);
        WriteWorldBuffer(buffer: m_meshIrradianceRegion.Buffer(slot: slot), member: SdfWorldPackage.MeshIrradiance, set: set);
        WriteWorldBuffer(buffer: m_brickPoolBuffer, member: SdfWorldPackage.BrickPool, set: set);
        WriteLightAndSkySet(set: set, slot: slot);

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
                imageViewHandle: ((MeshAtlasView(usage: usage) != 0)
                    ? MeshAtlasView(usage: usage)
                    : m_sampledFiller.ImageViewHandle)
            );
        }

        for (var usage = 0; (usage < SdfWorldPackage.ImpostorAtlases.Count); usage++) {
            m_bindings.WriteSampledImage(
                arrayElement: 0,
                binding: WorldBinding(member: SdfWorldPackage.ImpostorAtlases[usage]),
                descriptorSetHandle: set,
                imageViewHandle: ((ImpostorAtlasView(usage: usage) != 0)
                    ? ImpostorAtlasView(usage: usage)
                    : m_sampledFiller.ImageViewHandle)
            );
        }
    }

    // Writes a ring slot's mesh region into a mesh pass set of the mesh interface.
    internal void WriteMeshTables(nint set, int slot) =>
        WriteInterfaceBuffer(buffer: m_meshRegion.Buffer(slot: slot), layout: SdfWorldInterfaces.MeshLayout, member: SdfWorldPackage.MeshRegion, set: set);
    // Writes the impostor depth atlas a mesh pass's card fragments search into a mesh pass set, with the sampled filler
    // while the frame draws no impostor.
    internal void WriteMeshImpostorDepth(nint set) {
        var view = ImpostorAtlasView(usage: ImpostorDepthUsage);

        m_bindings.WriteSampledImage(
            arrayElement: 0,
            binding: SdfKernelInterfaces.BindingOf(layout: SdfWorldInterfaces.MeshLayout, member: SdfWorldPackage.ImpostorDepth),
            descriptorSetHandle: set,
            imageViewHandle: ((view != 0)
                ? view
                : m_sampledFiller.ImageViewHandle)
        );
    }
    // Writes the work counters a mesh pass's fragments count into, at the mesh interface's binding.
    internal void WriteMeshWorkCounters(nint set, IGpuBuffer counters) =>
        WriteInterfaceBuffer(buffer: counters, layout: SdfWorldInterfaces.MeshLayout, member: ShaderWorkCounters.Buffer, set: set);
}
