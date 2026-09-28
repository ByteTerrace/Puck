// The emulator's own ITimer (in the Interfaces namespace) collides by simple name with System.Threading.ITimer,
// which the SDK's ImplicitUsings pulls into every file. The core never uses the threading timer, so alias the bare
// name project-wide to the emulator interface; this keeps `ITimer` unambiguous without touching every consumer.
global using ITimer = Puck.HumbleGamingBrick.Interfaces.ITimer;
// MachineFork/MachineInstance are Puck.Machines' shared generic instance/fork/pool machinery closed over this
// core's own machine and configuration types, aliased to the bare names every consumer already spells.
global using MachineFork = Puck.Machines.MachineFork<Puck.HumbleGamingBrick.Machine, Puck.HumbleGamingBrick.MachineConfiguration>;
global using MachineInstance = Puck.Machines.MachineInstance<Puck.HumbleGamingBrick.Machine, Puck.HumbleGamingBrick.MachineConfiguration>;
