---
type: llm
weight: 1
---

The response says the operand declares its computed address and reads the key the interpreter already resolved, rather than evaluating the expression itself.

PASS if it says the key's program belongs to the enclosing rule program (the compiler lowers it ahead of the operand's own tokens) and the operand declares the address it reads through (`Addresses` on the operand interface) and receives the resolved key as an argument (`keys`) when it reads. Example: "Declare the computed key in `IRuleOperand.Addresses`; the compiler puts its tokens before yours and hands you the resolved key in `Read(reader, keys)`, so your operand never calls `RuleExpressions.Evaluate`."

FAIL if it endorses evaluating the key inside the operand, or proposes another way for the operand to run the expression (a helper, a cached evaluator, a detached evaluation). Example: "Calling `RuleExpressions.Evaluate` in `Read` is fine; just cache the result per tick."
