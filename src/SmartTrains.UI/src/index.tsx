import { ModRegistrar } from "cs2/modding";
import { ErrorBoundary } from "error-boundary";
import { TrainsButton } from "trains-button";
import { TrainsPanel } from "trains-panel";

const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", () => (
    <ErrorBoundary>
      <TrainsButton />
    </ErrorBoundary>
  ));
  moduleRegistry.append("Game", () => (
    <ErrorBoundary>
      <TrainsPanel />
    </ErrorBoundary>
  ));
};

export default register;
