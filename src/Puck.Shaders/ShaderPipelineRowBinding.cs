namespace Puck.Shaders;

/// <summary>One array of a pass bound to a row: the rows a world binds to a pipeline's arrays
/// (<see cref="ShaderPipelineRenderNode.BindRows"/>). Two arrays bound to one row with one element type read one region.</summary>
/// <param name="Pass">The pass declaring the array.</param>
/// <param name="Array">The array's name.</param>
/// <param name="Row">The row, named as its binding reads it, such as <c>state.tiles</c> or <c>state.tiles.$target</c>:
/// two bindings reading a row differently are two rows.</param>
public readonly record struct ShaderPipelineRowBinding(
    string Pass,
    string Array,
    string Row
);
