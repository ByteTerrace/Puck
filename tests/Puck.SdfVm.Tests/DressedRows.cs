namespace Puck.SdfVm.Tests;

internal static class DressedRows {
    public static IReadOnlyList<SdfMeshDraw> DressedDraws(this SdfFrame frame) => frame.MeshRows!.Live().Select(selector: pair => pair.Draw).ToArray();
}
