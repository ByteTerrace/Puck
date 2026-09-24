/**
 * The quiet branded screen shown while the portal loads. `index.html` carries the same markup inside `#root`, so the
 * screen is already up while sign-in initializes, before React renders anything.
 */
export function LoadingScreen() {
  return (
    <div className="host-screen" role="status">
      <img alt="" height={48} src="/puck-dark-64.png" width={48} />
      <p>Loading Puck</p>
    </div>
  );
}
