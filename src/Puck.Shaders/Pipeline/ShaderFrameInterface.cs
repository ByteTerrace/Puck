using System.Numerics;

namespace Puck.Shaders;

/// <summary>
/// The pass interface a pipeline pass reads its frame data through: the frame group's members every pass shares, then
/// the pass's config fields in ordinal name order. The engine derives it from the pass declaration, generates the
/// declarations a source includes as <c>&lt;interface&gt;.interface.hlsli</c> (<see cref="IncludeFileName"/>), and writes
/// the block each frame through <see cref="ShaderPipelineParameterLayout.WriteFrame"/>. The block is delivered as push
/// constants (<see cref="ShaderInterface.PushConstants"/>), and every member is a value, so a pass reads
/// <c>frameGroup.time</c>, <c>frameGroup.extent</c> or a config field such as <c>frameGroup.decay</c>.
/// </summary>
public static class ShaderFrameInterface {
    /// <summary>The frame member holding the pass's output extent in pixels, width then height (<c>uint2</c>).</summary>
    public const string Extent = "extent";
    /// <summary>The frame member holding the pointer's position during its most recent press, in the pass's pixels with
    /// the origin at the top-left corner, or zero before the first press (<c>float2</c>).</summary>
    public const string Pointer = "pointer";
    /// <summary>The frame member holding the deterministic engine tick the frame presents, low word then high word
    /// (<c>uint2</c>).</summary>
    public const string Tick = "tick";
    /// <summary>The frame member holding the presentation time in seconds (<c>float</c>).</summary>
    public const string Time = "time";
    /// <summary>The frame member holding the presentation seconds since the previous frame (<c>float</c>).</summary>
    public const string TimeDelta = "timeDelta";
    /// <summary>The frame member holding how many frames the pass's node has submitted before this one
    /// (<c>uint</c>).</summary>
    public const string Frame = "frame";
    /// <summary>The frame member holding the number of engine ticks in one second, the rate <see cref="Tick"/> counts in
    /// (<c>uint</c>).</summary>
    public const string TickRate = "tickRate";
    /// <summary>The frame member holding one while the pointer is pressed and zero otherwise (<c>uint</c>).</summary>
    public const string PointerDown = "pointerDown";
    /// <summary>The frame member holding how many presses the pointer has made over the pass (<c>uint</c>).</summary>
    public const string PointerPresses = "pointerPresses";
    /// <summary>The frame member holding the paired camera's position (<c>float3</c>).</summary>
    public const string CameraPosition = "cameraPosition";
    /// <summary>The frame member holding the paired camera's vertical field of view in radians, or zero when the pass has
    /// no paired camera (<c>float</c>).</summary>
    public const string CameraFov = "cameraFov";
    /// <summary>The frame member holding the point the paired camera looks at (<c>float3</c>).</summary>
    public const string CameraTarget = "cameraTarget";
    /// <summary>The frame member holding the paired camera's up direction (<c>float3</c>).</summary>
    public const string CameraUp = "cameraUp";

    /// <summary>Gets the frame group's members, in declaration order. A pass's config fields follow them.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> Members { get; } = [
        Value(name: Extent, type: ShaderValueType.Uint2),
        Value(name: Pointer, type: ShaderValueType.Float2),
        Value(name: Tick, type: ShaderValueType.Uint2),
        Value(name: Time, type: ShaderValueType.Float),
        Value(name: TimeDelta, type: ShaderValueType.Float),
        Value(name: Frame, type: ShaderValueType.Uint),
        Value(name: TickRate, type: ShaderValueType.Uint),
        Value(name: PointerDown, type: ShaderValueType.Uint),
        Value(name: PointerPresses, type: ShaderValueType.Uint),
        Value(name: CameraPosition, type: ShaderValueType.Float3),
        Value(name: CameraFov, type: ShaderValueType.Float),
        Value(name: CameraTarget, type: ShaderValueType.Float3),
        Value(name: CameraUp, type: ShaderValueType.Float3),
    ];

    /// <summary>Creates the interface of a pass: the frame members, then each config field as a frame-group value in
    /// ordinal name order, with the frame group pushed.</summary>
    /// <param name="name">The interface's name (<see cref="NameOf"/>).</param>
    /// <param name="config">The pass's config schema, or <see langword="null"/> when it has none.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="InvalidDataException"><paramref name="name"/> is not an interface name, or a config field's name
    /// is not an identifier or repeats a frame member's.</exception>
    public static ShaderInterface For(string name, IReadOnlyDictionary<string, ShaderConfigField>? config) {
        var members = new List<ShaderInterfaceMember>(collection: Members);

        if (config is not null) {
            foreach (var (field, declared) in config.OrderBy(
                comparer: StringComparer.Ordinal,
                keySelector: static pair => pair.Key
            )) {
                members.Add(item: Value(
                    name: field,
                    type: declared.Type
                ));
            }
        }

        return new ShaderInterface(
            members: members,
            name: name,
            pushConstants: ShaderInterfaceGroup.Frame
        );
    }
    /// <summary>Returns the file name a pass source includes to read its interface: the interface name followed by
    /// <c>.interface.hlsli</c>, resolved beside the source.</summary>
    /// <param name="interfaceName">The interface's name.</param>
    /// <returns>The file name.</returns>
    public static string IncludeFileName(string interfaceName) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: interfaceName);

        return (interfaceName + ".interface.hlsli");
    }
    /// <summary>Returns the interface name a pass source's file names: its file name up to the first period, so
    /// <c>ink-simulation.hlsl</c> names <c>ink-simulation</c> and <c>sdf-film-grain.frag.hlsl</c> names
    /// <c>sdf-film-grain</c>.</summary>
    /// <param name="sourcePath">The source's path.</param>
    /// <returns>The name, which a valid interface requires to be lowercase ASCII words joined by hyphens.</returns>
    public static string NameOf(string sourcePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);

        var fileName = Path.GetFileName(path: sourcePath);
        var dot = fileName.IndexOf(value: '.');

        return ((dot < 0)
            ? fileName
            : fileName[..dot]);
    }

    private static ShaderInterfaceMember Value(string name, ShaderValueType type) =>
        ShaderInterfaceMember.Value(
            group: ShaderInterfaceGroup.Frame,
            name: name,
            type: type
        );
}
/// <summary>The frame values a host supplies to a pipeline pass each frame. The node adds the rest of the frame group
/// itself: the pass's extent, the engine tick from the frame's <c>FrameContext</c>, the tick rate, and its own frame
/// count.</summary>
/// <param name="Time">The presentation time, in seconds.</param>
/// <param name="TimeDelta">The presentation seconds since the previous frame.</param>
/// <param name="Pointer">The pointer's position during its most recent press, in the pass's pixels with the origin at
/// the top-left corner, or zero before the first press.</param>
/// <param name="PointerDown">Whether the pointer is pressed.</param>
/// <param name="PointerPresses">How many presses the pointer has made over the pass.</param>
/// <param name="CameraPosition">The paired camera's position, or zero with no paired camera.</param>
/// <param name="CameraTarget">The point the paired camera looks at, or zero with no paired camera.</param>
/// <param name="CameraUp">The paired camera's up direction, or zero with no paired camera.</param>
/// <param name="CameraFov">The paired camera's vertical field of view in radians, or zero with no paired camera, which
/// a pass reads as having none.</param>
public readonly record struct ShaderFrameValues(
    double Time,
    double TimeDelta,
    Vector2 Pointer,
    bool PointerDown,
    uint PointerPresses,
    Vector3 CameraPosition,
    Vector3 CameraTarget,
    Vector3 CameraUp,
    float CameraFov
);
