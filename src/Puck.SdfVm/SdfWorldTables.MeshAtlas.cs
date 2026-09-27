using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// The mesh atlases: every textured mesh a frame draws (SdfMesh.Textures) packed by SdfMeshAtlas into one image per usage,
// bound in the tables' one World-group set, which every compute pass of every view binds at group 1. The atlases are
// repacked only when the distinct texture sets the frame's draws name change, which a bake landing or leaving does, and a
// repack first waits the device idle, since every view samples them; the set is then rewritten in place. A frame whose
// draws name no texture set binds the sampled filler at every atlas, and a set of textures too large to pack draws its
// meshes untextured.
public sealed partial class SdfWorldTables {
    private readonly IGpuSurfaceUpload?[] m_meshAtlasUploads = new IGpuSurfaceUpload?[SdfMeshTextures.Usages.Count];
    private readonly List<SdfMeshTextures> m_meshTextureScratch = [];

    // The atlases the World set binds, the texture sets they were packed from, in the order the draws first name them,
    // and whether a set too large to pack was reported.
    private SdfMeshAtlas? m_meshAtlas;
    private SdfMeshTextures[] m_meshAtlasSources = [];
    private bool m_meshAtlasRefusalReported;

    /// <summary>Gets the mesh atlases the latest frame binds, or <see langword="null"/> when its draws name no texture
    /// set.</summary>
    public SdfMeshAtlas? MeshAtlas => m_meshAtlas;

    // Gets the World-group set every compute pass binds at group 1: the mesh atlases.
    internal nint WorldSet => m_worldSet;

    // Returns the atlases a draw list's textured meshes sample, repacking and rebinding them when the distinct texture sets
    // it names differ from the ones last packed.
    private SdfMeshAtlas? StageMeshAtlas(IReadOnlyList<SdfMeshDraw> draws) {
        m_meshTextureScratch.Clear();

        for (var draw = 0; (draw < draws.Count); draw++) {
            if ((draws[draw].Mesh.Textures is { } textures) && !ContainsReference(list: m_meshTextureScratch, item: textures)) {
                m_meshTextureScratch.Add(item: textures);
            }
        }

        if (SameReferences(left: m_meshTextureScratch, right: m_meshAtlasSources)) {
            return m_meshAtlas;
        }

        SdfMeshAtlas? atlas = null;

        if (m_meshTextureScratch.Count > 0) {
            try {
                atlas = SdfMeshAtlas.Pack(textures: m_meshTextureScratch);
            } catch (ArgumentException exception) {
                if (!m_meshAtlasRefusalReported) {
                    m_meshAtlasRefusalReported = true;
                    Console.Error.WriteLine(value: $"[sdf.world: the mesh atlases were not packed, so their meshes draw untextured: {exception.Message}]");
                }
            }
        }

        // Every view's submissions sample the atlases, so they are replaced only once the device is idle.
        m_deviceContext.TryWaitIdle();

        var views = new nint[SdfMeshTextures.Usages.Count];

        if (atlas is not null) {
            for (var usage = 0; (usage < views.Length); usage++) {
                m_meshAtlasUploads[usage] ??= m_gpu.SurfaceTransferFactory.CreateUpload();
                views[usage] = m_meshAtlasUploads[usage]!.Upload(
                    format: SdfMeshTextures.FormatOf(usage: SdfMeshTextures.Usages[usage]),
                    height: ((uint)atlas.Height),
                    levels: ((uint)atlas.Levels),
                    pixels: atlas.Chains[usage],
                    width: ((uint)atlas.Width)
                );
            }
        }

        WriteMeshAtlases(views: views);
        m_meshAtlas = atlas;
        m_meshAtlasSources = [.. m_meshTextureScratch];

        return atlas;
    }
    // Writes each atlas's view into the World set, or the sampled filler where it has none.
    private void WriteMeshAtlases(ReadOnlySpan<nint> views) {
        for (var usage = 0; (usage < SdfWorldPackage.MeshAtlases.Count); usage++) {
            m_bindings.WriteSampledImage(
                arrayElement: 0,
                binding: SdfWorldInterfaces.WorldGroupBindingOf(member: SdfWorldPackage.MeshAtlases[usage]),
                descriptorSetHandle: m_worldSet,
                imageViewHandle: (((usage < views.Length) && (views[usage] != 0))
                    ? views[usage]
                    : m_sampledFiller.ImageViewHandle)
            );
        }
    }
    private void DisposeMeshAtlases() {
        foreach (var upload in m_meshAtlasUploads) {
            upload?.Dispose();
        }
    }

    private static bool ContainsReference(List<SdfMeshTextures> list, SdfMeshTextures item) {
        foreach (var candidate in list) {
            if (ReferenceEquals(objA: candidate, objB: item)) {
                return true;
            }
        }

        return false;
    }
    private static bool SameReferences(List<SdfMeshTextures> left, SdfMeshTextures[] right) {
        if (left.Count != right.Length) {
            return false;
        }

        for (var index = 0; (index < right.Length); index++) {
            if (!ReferenceEquals(objA: left[index], objB: right[index])) {
                return false;
            }
        }

        return true;
    }
}
