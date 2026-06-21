using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Gamepad;

/// <summary>The four rumble-motor intensities (0..1) accepted by <see cref="IGameInputDevice.SetRumbleState"/>.</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct GameInputRumbleParams
{
    public float LowFrequency;
    public float HighFrequency;
    public float LeftTrigger;
    public float RightTrigger;
}

/// <summary>A GameInput gamepad reading (buttons bitmask, 0..1 triggers, -1..1 thumbsticks).</summary>
[StructLayout(layoutKind: LayoutKind.Sequential)]
public struct GameInputGamepadState
{
    public uint Buttons;
    public float LeftTrigger;
    public float RightTrigger;
    public float LeftThumbstickX;
    public float LeftThumbstickY;
    public float RightThumbstickX;
    public float RightThumbstickY;
}

/// <summary>The GameInput device connect/disconnect callback (CALLBACK = stdcall).</summary>
/// <param name="callbackToken">The <c>GameInputCallbackToken</c> identifying this callback registration.</param>
/// <param name="context">The opaque context passed at registration (unused here).</param>
/// <param name="device">The device whose status changed (a raw <c>IGameInputDevice*</c>).</param>
/// <param name="timestamp">The change timestamp.</param>
/// <param name="currentStatus">The device's new status flags.</param>
/// <param name="previousStatus">The device's prior status flags.</param>
[UnmanagedFunctionPointer(callingConvention: CallingConvention.StdCall)]
internal delegate void GameInputDeviceCallback(ulong callbackToken, nint context, nint device, ulong timestamp, uint currentStatus, uint previousStatus);

/// <summary>The <c>gameinput.dll</c> exports we use.</summary>
public static partial class GameInputNative
{
    /// <summary>The <c>GameInputKindGamepad</c> input-kind flag.</summary>
    public const uint GameInputKindGamepad = 0x00040000u;

    /// <summary>The <c>GameInputDeviceConnected</c> status flag.</summary>
    public const uint GameInputDeviceConnected = 0x00000001u;

    /// <summary>The <c>GameInputAsyncEnumeration</c> kind (enumerate connected devices without blocking).</summary>
    public const uint GameInputAsyncEnumeration = 1u;

    // GameInputCreate marshals a COM interface out-param, which LibraryImport cannot source-generate, so this
    // one stays a classic DllImport.
    [DllImport("gameinput.dll", PreserveSig = true)]
    public static extern int GameInputCreate([MarshalAs(UnmanagedType.Interface)] out IGameInput gameInput);
}

/// <summary>
/// The root GameInput interface (v0). Only the leading vtable slots we call are declared; every method is
/// <see cref="PreserveSigAttribute"/> so the exact native return type and vtable slot are preserved.
/// </summary>
[ComImport]
[Guid("11BE2A7E-4254-445A-9C09-FFC40F006918")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IGameInput
{
    [PreserveSig] ulong GetCurrentTimestamp();
    [PreserveSig] int GetCurrentReading(
        uint inputKind,
        [MarshalAs(UnmanagedType.Interface)] IGameInputDevice? device,
        [MarshalAs(UnmanagedType.Interface)] out IGameInputReading reading
    );
    // Placeholder slots (5..8) so RegisterDeviceCallback lands on slot 9 and UnregisterCallback on slot 13.
    [PreserveSig] int GetNextReading(nint referenceReading, uint inputKind, nint device, out nint reading);
    [PreserveSig] int GetPreviousReading(nint referenceReading, uint inputKind, nint device, out nint reading);
    [PreserveSig] int GetTemporalReading(ulong timestamp, nint device, out nint reading);
    [PreserveSig] int RegisterReadingCallback(nint device, uint inputKind, float analogThreshold, nint context, nint callbackFunc, out ulong callbackToken);
    [PreserveSig] int RegisterDeviceCallback(
        [MarshalAs(UnmanagedType.Interface)] IGameInputDevice? device,
        uint inputKind,
        uint statusFilter,
        uint enumerationKind,
        nint context,
        nint callbackFunc,
        out ulong callbackToken
    );
    [PreserveSig] int RegisterGuideButtonCallback(nint device, nint context, nint callbackFunc, out ulong callbackToken);
    [PreserveSig] int RegisterKeyboardLayoutCallback(nint device, nint context, nint callbackFunc, out ulong callbackToken);
    [PreserveSig] void StopCallback(ulong callbackToken);
    [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool UnregisterCallback(ulong callbackToken, ulong timeoutInMicroseconds);
}

/// <summary>A single GameInput reading. We call only <c>GetGamepadState</c> (slot 22); the earlier slots are declared purely as placeholders to land it at the correct vtable offset.</summary>
[ComImport]
[Guid("2156947A-E1FA-4DE0-A30B-D812931DBD8D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IGameInputReading
{
    [PreserveSig] uint GetInputKind();
    [PreserveSig] ulong GetSequenceNumber(uint inputKind);
    [PreserveSig] ulong GetTimestamp();
    [PreserveSig] void GetDevice([MarshalAs(UnmanagedType.Interface)] out IGameInputDevice device);
    // Placeholder slots (7..21) so GetGamepadState lands on its correct vtable slot (22).
    [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool GetRawReport(out nint report);
    [PreserveSig] uint GetControllerAxisCount();
    [PreserveSig] uint GetControllerAxisState(uint stateArrayCount, nint stateArray);
    [PreserveSig] uint GetControllerButtonCount();
    [PreserveSig] uint GetControllerButtonState(uint stateArrayCount, nint stateArray);
    [PreserveSig] uint GetControllerSwitchCount();
    [PreserveSig] uint GetControllerSwitchState(uint stateArrayCount, nint stateArray);
    [PreserveSig] uint GetKeyCount();
    [PreserveSig] uint GetKeyState(uint stateArrayCount, nint stateArray);
    [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool GetMouseState(nint state);
    [PreserveSig] uint GetTouchCount();
    [PreserveSig] uint GetTouchState(uint stateArrayCount, nint stateArray);
    [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool GetMotionState(nint state);
    [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool GetArcadeStickState(nint state);
    [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool GetFlightStickState(nint state);
    [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool GetGamepadState(out GameInputGamepadState state);
}

/// <summary>
/// A GameInput device. The leading force-feedback/haptic slots are declared as placeholders purely to land
/// <see cref="SetRumbleState"/> on its correct vtable slot (10); only <see cref="SetRumbleState"/> is called.
/// </summary>
[ComImport]
[Guid("31DD86FB-4C1B-408A-868F-439B3CD47125")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IGameInputDevice
{
    [PreserveSig] nint GetDeviceInfo();
    [PreserveSig] int GetDeviceStatus();
    [PreserveSig] void GetBatteryState(nint state);
    [PreserveSig] int CreateForceFeedbackEffect(uint motorIndex, nint effectParams, out nint effect);
    [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool IsForceFeedbackMotorPoweredOn(uint motorIndex);
    [PreserveSig] void SetForceFeedbackMotorGain(uint motorIndex, float masterGain);
    [PreserveSig] int SetHapticMotorState(uint motorIndex, nint hapticParams);
    [PreserveSig] void SetRumbleState(in GameInputRumbleParams rumbleParams);
}
