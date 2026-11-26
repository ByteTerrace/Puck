namespace Puck.Commands;

/// <summary>The kind of device a pointing contact comes from. A world rule reads it as a number (mouse 1, touch 2,
/// pen 3), so the same screen can answer a finger and a mouse through one door and still tell them apart.</summary>
public enum ContactDevice : byte {
    /// <summary>A mouse: the pointer while its primary button is held.</summary>
    Mouse = 1,
    /// <summary>A finger on a touchscreen.</summary>
    Touch = 2,
    /// <summary>A pen's tip on a digitizer.</summary>
    Pen = 3,
}
/// <summary>Where a pointing contact is in its life. A world rule reads it as a number (down 1, move 2, up 3,
/// cancel 4).</summary>
public enum ContactPhase : byte {
    /// <summary>The contact touched: a finger or pen tip met the glass, or the mouse's primary button went down.</summary>
    Down = 1,
    /// <summary>The contact moved while it stayed down.</summary>
    Move = 2,
    /// <summary>The contact lifted.</summary>
    Up = 3,
    /// <summary>The contact ended without lifting: the platform took the touch stream (a system gesture), the window
    /// lost focus, or the contact's seat stopped seeing it. A rule treats it as the contact gone, never as a tap.</summary>
    Cancel = 4,
}
/// <summary>One contact event as it reaches the simulation: which device and contact, its phase, and the world-space
/// ray its point casts through the seat's camera. The ray is fixed point from the moment it is cast
/// (<see cref="SourceRay.Through"/>), so the authority maps it through each screen row it may land on
/// (<see cref="SourceMapping.MapRay"/>) and every run maps the same contact to the same surface point.</summary>
/// <param name="Device">The device kind.</param>
/// <param name="Id">The contact's number, stable while it is down: 0 for the mouse, and from 1 for touch and pen
/// contacts, the lowest number free when the contact went down.</param>
/// <param name="Phase">The phase.</param>
/// <param name="Ray">The ray the contact's point casts through its seat's view.</param>
public readonly record struct SurfaceContact(ContactDevice Device, byte Id, ContactPhase Phase, SourceRay Ray) {
    /// <summary>The most contact events one seat carries into one tick. More are dropped at the seat, oldest kept, and
    /// the authority refuses a batch that carries more.</summary>
    public const int MaximumPerTick = 32;
    /// <summary>The highest touch or pen contact number: ten fingers.</summary>
    public const int MaximumContacts = 10;

    /// <summary>Returns whether a contact's fields lie in their domains: a known device and phase, and a contact number
    /// within its device's range (only the mouse is 0).</summary>
    /// <returns><see langword="true"/> when the contact is well formed.</returns>
    public bool IsWellFormed() => (
        Enum.IsDefined(value: Device) &&
        Enum.IsDefined(value: Phase) &&
        ((Device == ContactDevice.Mouse)
            ? (Id == 0)
            : ((Id >= 1) && (Id <= MaximumContacts)))
    );
}
