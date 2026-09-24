import { Alert, Button, Stack, Text } from "@mantine/core";
import { RiErrorWarningLine } from "@remixicon/react";
import { Component, type ErrorInfo, type ReactNode } from "react";

interface ErrorBoundaryProps {
  children: ReactNode;
  /** What failed, in the reader's terms: "World Studio", "the audit trail". */
  subject: string;
}

interface ErrorBoundaryState {
  error: Error | null;
}

/** Contains a render or lazy-load failure to its own section, naming it, with a retry. */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  override state: ErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error(`${this.props.subject} failed to render`, error, info.componentStack);
  }

  override render() {
    if (this.state.error === null) {
      return this.props.children;
    }

    return (
      <Alert color="red" icon={<RiErrorWarningLine size={18} />} title={`${this.props.subject} could not load`} variant="light">
        <Stack align="flex-start" gap="sm">
          <Text size="sm">{this.state.error.message}</Text>
          <Button onClick={() => this.setState({ error: null })} size="xs" variant="default">
            Try again
          </Button>
        </Stack>
      </Alert>
    );
  }
}
