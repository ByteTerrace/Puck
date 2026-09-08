import { createContext, useContext } from "react";
import { createGraphClient } from "./graphClientApi";

type GraphClientProviderProperties = {
  children: React.ReactNode;
  graphClient: ReturnType<typeof createGraphClient>;
};

const GraphClientContext = createContext<ReturnType<typeof createGraphClient>>(undefined!);

export function useGraphClient() {
  const context = useContext(GraphClientContext);

  if (!context) {
    throw new Error("useGraphClient must be used within a GraphClientProvider.");
  }

  return context;
}

export default function GraphClientProvider({
  children,
  graphClient,
}: GraphClientProviderProperties) {
  return (
    <GraphClientContext.Provider value={graphClient}>
      {children}
    </GraphClientContext.Provider>
  );
}
