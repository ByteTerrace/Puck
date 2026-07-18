namespace Puck.AdvancedGamingBrick.Post;

/// <summary>One reference-ROM test case: the group it belongs to, its display name, and the ROM's full path on disk. The
/// AGB is a single hardware model, so — unlike the DMG / CGB corpus — there is no per-case console-model
/// dimension.</summary>
/// <param name="Group">The group (e.g. "conformance-cpu") the case belongs to.</param>
/// <param name="Name">The ROM's display name.</param>
/// <param name="FullPath">The absolute path to the ROM image.</param>
internal sealed record RomCase(string Group, string Name, string FullPath);
