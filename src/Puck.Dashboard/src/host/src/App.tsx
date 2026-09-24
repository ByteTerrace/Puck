import { Component, lazy, Suspense, useContext, useState, type ReactNode } from "react";
import type { PortalModule } from "../../shared/interfaces";
import { HostContext } from "./HostProvider";
import { LoadingScreen } from "./LoadingScreen";
import { loadPortal } from "./remote";

interface PortalBoundaryProps {
  children: ReactNode;
  onRetry: () => void;
}

/** Turns a failed portal load (network, a stale deployment, a share mismatch) into a screen with a retry. */
class PortalBoundary extends Component<PortalBoundaryProps, { error: Error | null }> {
  override state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  override componentDidCatch(error: Error): void {
    console.error("The portal failed to load:", error);
  }

  override render() {
    if (!this.state.error) {
      return this.props.children;
    }

    return (
      <div className="host-screen" role="alert">
        <img alt="" height={48} src="/puck-dark-64.png" width={48} />
        <h1>Puck could not load</h1>
        <p>{this.state.error.message}</p>
        <button
          onClick={() => {
            this.setState({ error: null });
            this.props.onRetry();
          }}
          type="button"
        >
          Try again
        </button>
      </div>
    );
  }
}

/** The host's frame around the federated portal. `initialLoad` is the load `main.tsx` started before sign-in. */
function App({ initialLoad }: { initialLoad: Promise<PortalModule> }) {
  const context = useContext(HostContext);
  // A lazy component caches its outcome, so a retry needs a new one over a new load.
  const [Portal, setPortal] = useState(() => lazy(() => initialLoad));

  return (
    <PortalBoundary onRetry={() => setPortal(() => lazy(loadPortal))}>
      <Suspense fallback={<LoadingScreen />}>
        <Portal context={context} />
      </Suspense>
    </PortalBoundary>
  );
}

export default App;
