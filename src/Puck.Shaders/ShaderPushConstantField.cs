using System.Text.Json;

namespace Puck.Shaders;

/// <summary>One field of a <see cref="ShaderSetManifest"/>'s push-constant block, in declaration order — the order
/// the shader's own push-constant struct declares its members, which is what fixes each field's byte offset
/// (<see cref="ShaderPushConstantLayout"/>).</summary>
/// <param name="Name">The field's name; unique within the block.</param>
/// <param name="Type">The field's value type.</param>
/// <param name="Source">Where the value comes from, one of: <c>config.&lt;field&gt;</c> (a bound config value of the
/// same type); <c>tick</c> (the fixed-step simulation clock, <c>uint</c> or <c>uint2</c> low/high, optionally
/// quantized by <paramref name="QuantizeHz"/>); <c>resolution</c> (the pass's width and height, <c>float2</c> or
/// <c>uint2</c>); <c>frame</c> (the pass's own produced-frame counter, <c>uint</c> — pacing-dependent, presentation
/// only).</param>
/// <param name="QuantizeHz">For a <c>tick</c> source: the rate the tick is quantized to before it is written — either
/// a positive integer literal or a <c>config.&lt;field&gt;</c> reference to a <c>uint</c> config value. The written
/// value is <c>ticks / (EngineTicks.PerSecond / hz)</c>, so every frame inside one period sees the same value on every
/// run, machine, and backend; the rate must divide <c>EngineTicks.PerSecond</c> exactly.</param>
public sealed record ShaderPushConstantField(
    string Name,
    ShaderValueType Type,
    string Source,
    JsonElement? QuantizeHz = null
);
/// <summary>A <see cref="ShaderSetManifest"/>'s push-constant block: which stages read it and its fields in
/// declaration order.</summary>
/// <param name="Stages">The stages that read the block: any of <c>vertex</c>, <c>fragment</c>, <c>compute</c>.</param>
/// <param name="Fields">The fields, in the shader struct's declaration order.</param>
public sealed record ShaderPushConstantBlock(
    IReadOnlyList<string> Stages,
    IReadOnlyList<ShaderPushConstantField> Fields
);
