namespace Puck.SdfVm.Tests;

internal static class DressedRows {
    public static IReadOnlyList<SdfMeshDraw> DressedDraws(this SdfFrame frame) => frame.MeshRows!.Live().Select(pair => pair.Draw).ToArray();
}
