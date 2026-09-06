import { loadRemote } from "@module-federation/runtime";
import { lazy, Suspense, useContext } from "react";
import { Provider } from "react-redux";
import { HostContextValue } from "../../shared/interfaces";
import { HostContext } from "./HostProvider";

const Portal = lazy(
  async () =>
    (await loadRemote<{
      default: React.ComponentType<{
        context: HostContextValue;
      }>;
    }>("portal/portal-app"))!
);

function App() {
  const context = useContext(HostContext);

  return (
    <>
      <Provider store={context.store.value}>
        <Suspense fallback="loading...">
          <Portal context={context} />
        </Suspense>
      </Provider>
    </>
  );
}

export default App;
