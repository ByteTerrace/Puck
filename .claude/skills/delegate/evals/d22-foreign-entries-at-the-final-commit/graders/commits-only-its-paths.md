---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply's commit takes only the lane's three files (`src/Puck.Commands/BindingPageView.cs`, `src/Puck.Commands/BindingSession.cs`, `worlds/standard/bindings/bar.puck`), for example `git commit -- <those paths>` or `git commit --only <those paths>`, so neither generator file goes into it.
Example PASS: "`git commit -m \"commands: ...\" -- src/Puck.Commands/BindingPageView.cs src/Puck.Commands/BindingSession.cs worlds/standard/bindings/bar.puck`"

FAIL if the reply's commit would include `docs/reference/schema.md` or `src/Puck.Schema/Generated/SchemaMembers.g.cs` (`git add -A`, `git add .` or `git commit -a` followed by a plain commit, or a plain `git commit` after staging its own files beside them).
Example FAIL: "`git add -A && git commit -m \"commands: bar slot count comes from the layout\"`"
