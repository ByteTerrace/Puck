// The emulator's own ITimer (in the Interfaces namespace) collides by simple name with System.Threading.ITimer,
// which the SDK's ImplicitUsings pulls into every file. The core never uses the threading timer, so alias the bare
// name project-wide to the emulator interface; this keeps `ITimer` unambiguous without touching every consumer.
global using ITimer = Puck.HumbleGamingBrick.Interfaces.ITimer;
