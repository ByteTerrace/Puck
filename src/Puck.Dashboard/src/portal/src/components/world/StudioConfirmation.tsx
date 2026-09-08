import React, { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import { Button, Group, Modal, Text } from "@mantine/core";
type Confirm = (message: string) => Promise<boolean>;
const ConfirmationContext = createContext<Confirm>(async () => false);
export const useStudioConfirmation = () => useContext(ConfirmationContext);
export function StudioConfirmation({ children }: {
  children: React.ReactNode;
}) {
  const [message, setMessage] = useState<string | null>(null);
  const pending = useRef<((accepted: boolean) => void) | null>(null);
  const confirm = useCallback<Confirm>(text => {
    pending.current?.(false);
    setMessage(text);
    return new Promise(resolve => { pending.current = resolve; });
  }, []);
  const finish = (accepted: boolean) => {
    pending.current?.(accepted);
    pending.current = null;
    setMessage(null);
  };
  useEffect(() => () => pending.current?.(false), []);
  return <ConfirmationContext.Provider
    value={confirm}>
    {children}
    <Modal
      opened={message !== null}
      onClose={() => finish(false)}
      title="Confirm document change"
      zIndex={400}
      closeButtonProps={{ "aria-label": "Cancel document change" }}>
      <Text
        size="sm">{message}</Text>
      <Group
        justify="flex-end"
        mt="lg">
        <Button
          data-autofocus
          variant="default"
          onClick={() => finish(false)}>Keep current document</Button>
        <Button
          color="red"
          onClick={() => finish(true)}>Continue</Button>
      </Group>
    </Modal>
  </ConfirmationContext.Provider>;
}
