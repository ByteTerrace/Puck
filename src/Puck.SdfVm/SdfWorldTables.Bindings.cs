using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// What a view's pass binds of the tables: every table's buffer in the ring slot the frame's upload wrote, the brick pool,
// the samplers and the glyph atlas, each at its member of the world interface; and what it binds at a member whose
// storage it does not touch. The handles change only with the ring slot and the binding revision, so a pass rewrites
// them only when either moved.
public sealed partial class SdfWorldTables {
    // Gets the buffer a pass binds at a buffer member it does not touch.
    internal IGpuBuffer DummyBuffer => m_dummyBuffer;
    // Gets the image a pass binds at a sampled member it does not touch, resting shader-readable.
    internal IGpuImage SampledFiller => m_sampledFiller;
    // Gets the image a pass binds at a storage member it does not touch, resting General.
    internal IGpuImage StorageFiller => m_storageFiller;
    // Gets the device's bindings, counted under the tables.
    internal IGpuBindings Bindings => m_bindings;

    // Returns the binding of a member of the world interface's pass group.
    internal static uint WorldBinding(string member) =>
        SdfWorldInterfaces.BindingOf(
            layout: SdfWorldInterfaces.WorldLayout,
            member: member
        );
    // Writes a buffer at a member of the world interface in a pass set.
    internal void WriteWorldBuffer(nint set, string member, IGpuBuffer buffer) =>
        WriteBuffer(
            buffer: buffer,
            layout: SdfWorldInterfaces.WorldLayout,
            member: member,
            set: set
        );
    // Writes what every pass of a view binds of the tables into its pass set: each table's buffer in a ring slot, the brick
    // pool, the samplers and the glyph atlas, or the sampled filler while none is set, or another image in its place.
    internal void WriteShared(nint set, int slot, nint glyphAtlas = 0) {
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
            imageViewHandle: ((glyphAtlas != 0)
                ? glyphAtlas
                : ((m_glyphAtlasView != 0)
                    ? m_glyphAtlasView
                    : m_sampledFiller.ImageViewHandle))
        );
    }
    // Writes a ring slot's mesh region into a mesh pass set of the mesh interface.
    internal void WriteMeshTables(nint set, int slot) =>
        WriteBuffer(buffer: m_meshRegion.Buffer(slot: slot), layout: SdfWorldInterfaces.MeshLayout, member: SdfWorldPackage.MeshRegion, set: set);
}
