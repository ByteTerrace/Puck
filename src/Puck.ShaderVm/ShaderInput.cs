namespace Puck.ShaderVm;

/// <summary>Identifies a generic value supplied by the shader execution context.</summary>
public enum ShaderInput : byte {
    /// <summary>The caller-defined four-lane evaluation coordinate.</summary>
    Coordinate = 0,
    /// <summary>The elapsed time in seconds, replicated to every lane.</summary>
    Time = 1,
    /// <summary>The deterministic sample index, converted to a float and replicated to every lane.</summary>
    SampleIndex = 2,
}
