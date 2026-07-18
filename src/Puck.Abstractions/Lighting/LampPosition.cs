namespace Puck.Abstractions.Lighting;

/// <summary>
/// A lamp's position within its array, normalized to <c>0..1</c> along each axis of the device's bounding box
/// (the HID LampArray reports raw positions in micrometers within a bounding box; the platform layer normalizes
/// them so a consumer can lay out an effect without knowing physical dimensions). <see cref="X"/> runs left to
/// right, <see cref="Y"/> top to bottom, <see cref="Z"/> front to back.
/// </summary>
/// <param name="X">The horizontal position, 0 (leftmost) to 1 (rightmost).</param>
/// <param name="Y">The vertical position, 0 (topmost) to 1 (bottommost).</param>
/// <param name="Z">The depth position, 0 (frontmost) to 1 (backmost).</param>
public readonly record struct LampPosition(float X, float Y, float Z);
