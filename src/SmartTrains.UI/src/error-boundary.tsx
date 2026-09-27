import { Component, ReactNode } from "react";

interface Props {
  /** Shown instead of the children after an error; nothing if left out. */
  fallback?: ReactNode;
  children?: ReactNode;
}

/**
 * Keeps an error in Smart Trains' components inside Smart Trains. Without it,
 * React unmounts the whole tree on a render error, and the game's UI shares
 * that tree with every mod: one broken panel would remove the complete
 * in-game interface.
 */
export class ErrorBoundary extends Component<Props, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch(error: unknown) {
    console.error("Smart Trains: the panel failed and was hidden.", error);
  }

  render() {
    return this.state.failed ? this.props.fallback ?? null : this.props.children;
  }
}
