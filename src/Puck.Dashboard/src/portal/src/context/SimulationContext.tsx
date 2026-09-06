import { createActorContext } from "@xstate/react";
import { worldSimulationMachine } from "../machines/worldSimulationMachine";

export const SimulationContext = createActorContext(worldSimulationMachine);
