namespace Puck.DirectX.Messages;

/// <summary>
/// Describes an HLSL shader to compile to bytecode.
/// </summary>
/// <param name="HlslSource">The HLSL source text.</param>
/// <param name="SourceName">A name for the source, used in compiler diagnostics.</param>
/// <param name="EntryPoint">The entry-point function name (e.g. <c>VSMain</c>).</param>
/// <param name="Target">The shader-model target profile (e.g. <c>vs_5_0</c>, <c>ps_5_0</c>).</param>
public readonly record struct DirectXShaderCompileRequest(
    string HlslSource,
    string SourceName,
    string EntryPoint,
    string Target
);
