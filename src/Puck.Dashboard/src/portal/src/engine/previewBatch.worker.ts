import { runMonteCarloRollout, runWorldScenario } from "./scenarioRunner";
self.onmessage = ({ data }) => {
  try {
    const result = data.kind === "rollout" ? runMonteCarloRollout(...data.args as Parameters<typeof runMonteCarloRollout>)
      : Object.fromEntries(data.scenarios.map((scenario: any) => [scenario.id, runWorldScenario(scenario, data.rules, data.topologies, data.initial)]));
    self.postMessage({ result });
  }
  catch(error) {
    self.postMessage({ error: (error as Error).message });
  }
};
