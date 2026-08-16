// AgbMachineFork/AgbMachineInstance are Puck.GamingBricks' shared generic instance/fork/pool machinery closed over
// this core's own machine and configuration types, aliased to the bare names every consumer already spells.
global using AgbMachineFork = Puck.GamingBricks.MachineFork<Puck.AdvancedGamingBrick.AdvancedGamingBrickMachine, Puck.AdvancedGamingBrick.AgbMachineConfiguration>;
global using AgbMachineInstance = Puck.GamingBricks.MachineInstance<Puck.AdvancedGamingBrick.AdvancedGamingBrickMachine, Puck.AdvancedGamingBrick.AgbMachineConfiguration>;
