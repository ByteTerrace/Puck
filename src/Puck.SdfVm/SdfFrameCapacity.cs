namespace Puck.SdfVm;

/// <summary>The capacities an <see cref="SdfWorldEngine"/> sizes its frame buffers for
/// (<see cref="SdfWorldEngine.FrameBufferBytes"/>). Construction fixes every one of them except
/// <see cref="Instances"/>, which a program upload grows.</summary>
/// <param name="Width">The composited output width in pixels.</param>
/// <param name="Height">The composited output height in pixels.</param>
/// <param name="Viewports">The viewport slots provisioned.</param>
/// <param name="Instances">The program instances provisioned.</param>
/// <param name="BrickPoolVoxels">The carve-bake brick pool's voxel capacity; zero allocates a one-voxel filler.</param>
public readonly record struct SdfFrameCapacity(uint Width, uint Height, uint Viewports, int Instances, int BrickPoolVoxels) {
    /// <summary>Gets the tile columns of one viewport.</summary>
    public uint TileGridX => ((Width + (SdfWorldEngine.TileSize - 1)) / SdfWorldEngine.TileSize);
    /// <summary>Gets the tile rows of one viewport.</summary>
    public uint TileGridY => ((Height + (SdfWorldEngine.TileSize - 1)) / SdfWorldEngine.TileSize);
    /// <summary>Gets the tiles of one viewport.</summary>
    public ulong Tiles => (((ulong)TileGridX) * TileGridY);
}
