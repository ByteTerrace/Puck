---
type: llm
weight: 1
---

The response says the formatter is run on the files this change touched (the three C# files) and not on the rest of `src`.

PASS if it says to format the three touched files only. Example: "Format only the three files you changed and leave the rest of `src` alone."

FAIL if it says to run the formatter over `src` or a whole directory in this change, or to format nothing at all. Example: "Run the formatter over `src` before you commit so everything is consistent."
