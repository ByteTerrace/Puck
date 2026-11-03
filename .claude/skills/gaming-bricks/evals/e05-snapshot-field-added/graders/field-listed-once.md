---
type: llm
weight: 1
---

The response says the new field is added once, to the component's `TransferState` list that both save and load run through.

PASS if it says to add the field to the component's `TransferState` (or `TransferRegisters` for a mapper) and nowhere else for the layout, because save and load share that one list. Example: "Add the field once to `TransferState`; save and load both run through it, so nothing else needs a layout edit."

FAIL if it says to add the field separately to save and to load, to a layout file or a recorded fingerprint, or does not name the list. Example: "Add the field to the serializer and to the deserializer, and update the layout hash."
