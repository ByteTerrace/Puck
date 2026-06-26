// .NET 8 introduced System.Threading.ITimer. With ImplicitUsings importing System.Threading, the bare name
// "ITimer" collides with this project's own Interfaces.ITimer (CS0104). Every use in this project means the
// emulator's timer interface, so alias the bare name to it project-wide to resolve the ambiguity.
global using ITimer = Puck.HumbleGamingBrick.Interfaces.ITimer;
