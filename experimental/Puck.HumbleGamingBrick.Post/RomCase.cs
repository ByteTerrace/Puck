namespace Puck.HumbleGamingBrick.Post;

/// <summary>One reference-ROM test case: the group it belongs to, its display name, the ROM's full path, the console
/// model to run it on, and the frame ceiling before it is declared inconclusive.</summary>
/// <param name="Group">The group (e.g. "cpu-instrs") the case belongs to.</param>
/// <param name="Name">The ROM's display name (its file name without extension).</param>
/// <param name="FullPath">The absolute path to the ROM image.</param>
/// <param name="Model">The console model to run the ROM on.</param>
/// <param name="FrameCap">The maximum number of frames to run before declaring the case inconclusive.</param>
internal sealed record RomCase(string Group, string Name, string FullPath, ConsoleModel Model, int FrameCap);
