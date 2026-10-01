using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh atlases and the impostor atlases: every textured mesh (SdfMesh.Textures) and every impostor (SdfMeshDraw.Impostor)
// a frame draws, packed by SdfMeshAtlas into one image per usage, bound in the tables' World sets
// (SdfWorldTables.Bindings.cs), which every compute pass of every view binds at group 1, and the impostor depth atlas in each
// mesh pass's set. An atlas is repacked only when the distinct texture sets the frame's draws name change, which a bake
// landing or leaving does, and a repack first waits the device idle, since every view samples them; it then moves the
// binding revision, so the sets are rewritten before any pass binds them. A frame whose draws name no texture set binds the
// sampled filler at every atlas, and a set of textures too large to pack draws its meshes untextured, or its impostors not
// at all.
public sealed partial class SdfWorldTables {
    private readonly AtlasSlot m_meshAtlasSlot = new(usages: SdfMeshTextures.Usages.Count);
    private readonly AtlasSlot m_impostorAtlasSlot = new(usages: SdfMeshImpostor.Usages.Count);
    private readonly List<SdfTextureSet> m_atlasScratch = [];

    // The usage of the impostor atlases that holds the depth, which the mesh pass's card fragments search.
    private static readonly int ImpostorDepthUsage = SdfMeshImpostor.Usages.ToList().IndexOf(item: Puck.SignedDistance.Baking.SdfBakeTextureUsage.Depth);

    /// <summary>Gets the mesh atlases the latest frame binds, or <see langword="null"/> when its draws name no texture
    /// set.</summary>
    public SdfMeshAtlas? MeshAtlas => m_meshAtlasSlot.Atlas;
    /// <summary>Gets the impostor atlases the latest frame binds, or <see langword="null"/> when its draws name no
    /// impostor.</summary>
    public SdfMeshAtlas? ImpostorAtlas => m_impostorAtlasSlot.Atlas;

    // One more whenever the impostor atlases are repacked, which a mesh pass set binding their depth atlas follows.
    internal long ImpostorAtlasRevision => m_impostorAtlasSlot.Revision;

    // The view an impostor atlas usage binds, or zero for the sampled filler.
    internal nint ImpostorAtlasView(int usage) => m_impostorAtlasSlot.Views[usage];
    // The view a mesh atlas usage binds, or zero for the sampled filler.
    internal nint MeshAtlasView(int usage) => m_meshAtlasSlot.Views[usage];

    // Returns the mesh atlases a draw list's textured meshes sample, repacking and rebinding them when the distinct texture
    // sets it names differ from the ones last packed.
    private SdfMeshAtlas? StageMeshAtlas(IReadOnlyList<SdfMeshDraw> draws) {
        m_atlasScratch.Clear();

        for (var draw = 0; (draw < draws.Count); draw++) {
            if (draws[draw].Mesh.Textures is { } textures) {
                AddDistinct(item: textures);
            }
        }

        return Stage(slot: m_meshAtlasSlot, what: "the mesh atlases were not packed, so their meshes draw untextured");
    }
    // Returns the impostor atlases a draw list's card draws sample, as StageMeshAtlas does the mesh atlases.
    private SdfMeshAtlas? StageImpostorAtlas(IReadOnlyList<SdfMeshDraw> draws) {
        m_atlasScratch.Clear();

        for (var draw = 0; (draw < draws.Count); draw++) {
            if (draws[draw].Impostor is { } impostor) {
                AddDistinct(item: impostor);
            }
        }

        return Stage(slot: m_impostorAtlasSlot, what: "the impostor atlases were not packed, so their impostors are not drawn");
    }
    private void AddDistinct(SdfTextureSet item) {
        foreach (var candidate in m_atlasScratch) {
            if (ReferenceEquals(objA: candidate, objB: item)) {
                return;
            }
        }

        m_atlasScratch.Add(item: item);
    }
    private SdfMeshAtlas? Stage(AtlasSlot slot, string what) {
        if (SameReferences(left: m_atlasScratch, right: slot.Sources)) {
            return slot.Atlas;
        }

        SdfMeshAtlas? atlas = null;

        if (m_atlasScratch.Count > 0) {
            try {
                atlas = SdfMeshAtlas.Pack(textures: m_atlasScratch);
            } catch (ArgumentException exception) {
                if (!slot.RefusalReported) {
                    slot.RefusalReported = true;
                    Console.Error.WriteLine(value: $"[sdf.world: {what}: {exception.Message}]");
                }
            }
        }

        // Every view's submissions sample the atlases, so they are replaced only once the device is idle.
        m_deviceContext.WaitIdle();

        var usages = ((atlas is null) ? 0 : m_atlasScratch[0].UsageOrder.Count);
        var views = new nint[slot.Views.Length];
        var uploads = new IGpuSurfaceUpload?[views.Length];
        using var scope = new GpuCreationScope();

        if (atlas is not null) {
            for (var usage = 0; (usage < usages); usage++) {
                uploads[usage] = scope.Own(created: m_gpu.SurfaceTransferFactory.CreateUpload());
                views[usage] = uploads[usage]!.Upload(
                    format: SdfTextureSet.FormatOf(usage: m_atlasScratch[0].UsageOrder[usage]),
                    height: ((uint)atlas.Height),
                    levels: ((uint)atlas.Levels),
                    pixels: atlas.Chains[usage],
                    width: ((uint)atlas.Width)
                );
            }
        }

        scope.Complete();
        slot.DisposeUploads();
        uploads.CopyTo(array: slot.Uploads, index: 0);
        slot.Atlas = atlas;
        slot.Revision++;
        slot.Sources = [.. m_atlasScratch];
        views.CopyTo(array: slot.Views, index: 0);
        m_bindingRevision++;

        return atlas;
    }
    private void DisposeMeshAtlases() {
        m_meshAtlasSlot.DisposeUploads();
        m_impostorAtlasSlot.DisposeUploads();
    }
    private static bool SameReferences(List<SdfTextureSet> left, SdfTextureSet[] right) {
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

    // One kind of atlas: what it was packed from, the packed atlases, each usage's upload and the view a World set binds
    // (zero for the sampled filler).
    private sealed class AtlasSlot(int usages) {
        internal SdfMeshAtlas? Atlas;
        internal bool RefusalReported;
        internal long Revision;

        internal SdfTextureSet[] Sources = [];
        internal IGpuSurfaceUpload?[] Uploads = new IGpuSurfaceUpload?[usages];
        internal nint[] Views = new nint[usages];

        internal void DisposeUploads() {
            foreach (var upload in Uploads) {
                upload?.Dispose();
            }
        }
    }
}
