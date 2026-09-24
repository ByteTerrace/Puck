namespace Puck.Scripting;

/// <summary>The addon ABI's channel kind wire values (byte 0 of a 16-byte channel descriptor), defined
/// independently of any consumer enum. A descriptor naming any other byte refuses at decode as an undefined kind,
/// through the <c>Enum.IsDefined</c> check.</summary>
public enum AddonChannelKind : byte {
    /// <summary>The guest's declared input-source table: <c>Act</c> cells carry the addon's own virtual input device.</summary>
    Input = 1,

    /// <summary>The closed numeric query vocabulary a guest speaks <c>Act</c> and <c>Ask</c> cells through.</summary>
    Request = 2,

    /// <summary>The host-written answer channel paired with <see cref="Request"/>; the guest never writes it.</summary>
    Response = 3,
}
