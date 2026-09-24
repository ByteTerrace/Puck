import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";

// The portal served on its own: World Studio without the host's account and store.
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
