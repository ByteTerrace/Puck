// AgbMachineFork/AgbMachineInstance are Puck.GamingBricks' shared generic instance/fork/pool machinery closed over
// Puck.AdvancedGamingBrick's own machine and configuration types, aliased to the bare names every consumer already
// spells (name resolution across a project reference does not see the brick project's own aliases).
global using AgbMachineFork = Puck.GamingBricks.MachineFork<Puck.AdvancedGamingBrick.AdvancedGamingBrickMachine, Puck.AdvancedGamingBrick.AgbMachineConfiguration>;
global using AgbMachineInstance = Puck.GamingBricks.MachineInstance<Puck.AdvancedGamingBrick.AdvancedGamingBrickMachine, Puck.AdvancedGamingBrick.AgbMachineConfiguration>;
