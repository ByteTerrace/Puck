namespace Puck.Shaders.Study;

/// <summary>One compiler diagnostic, mapped back to the author's own <c>.glsl</c> file — <see cref="Line"/> is
/// 1-based in that file, with <see cref="StudyPrelude.PreludeLineCount"/> already subtracted from whatever line the
/// toolchain reported against the prelude-wrapped source it actually compiled.</summary>
public readonly record struct StudyDiagnostic(int Line, string Message, bool IsError);
