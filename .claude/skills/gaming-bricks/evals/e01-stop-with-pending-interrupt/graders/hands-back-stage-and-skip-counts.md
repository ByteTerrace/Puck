---
type: llm
weight: 1
---

The response says the result is shown by what the battery printed, not by its exit code alone: report the stages that ran and the skips the report lists (oracle-conflict skips, or stages skipped for missing assets).

PASS example: "Report the stage and skip counts from stdout; exit 0 alone doesn't prove a stage ran. The `sst-sm83` report should list the `10` and `fb` skips."

FAIL if it treats a zero exit code as proof, or never says what to read or report from the run. Example: "Run the battery; if it exits 0, the fix works."
