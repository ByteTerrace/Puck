namespace Puck.ShaderVm;

using System.Numerics;

/// <summary>The generic execution inputs one Shader VM evaluation reads.</summary>
/// <param name="Coordinate">The caller-defined four-lane evaluation coordinate.</param>
/// <param name="Time">The elapsed time, in seconds.</param>
/// <param name="SampleIndex">The deterministic sample index.</param>
public readonly record struct ShaderContext(Vector4 Coordinate, float Time = 0f, uint SampleIndex = 0u);
