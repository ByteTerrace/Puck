namespace Puck.Forge.Framework;

internal static class MinimalArt {
    public static byte[] BuildBlankTile() {
        var indices = new byte[64];

        Array.Fill(array: indices, value: (byte)1);

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }
}
