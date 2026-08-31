// MachineInstance is Puck.GamingBricks' shared generic instance/fork/pool machinery closed over
// Puck.HumbleGamingBrick's own machine and configuration types, aliased to the bare name every consumer already
// spells (name resolution across a project reference does not see the brick project's own aliases).
global using MachineInstance = Puck.GamingBricks.MachineInstance<Puck.HumbleGamingBrick.Machine, Puck.HumbleGamingBrick.MachineConfiguration>;
global using Puck.GamingBricks.Post;
