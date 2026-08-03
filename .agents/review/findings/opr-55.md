# opr-55: RTK planning merges native parameter arguments

**Severity**: MEDIUM — an RTK-eligible native invocation can receive different argument boundaries than the same script executed directly by PowerShell, changing target command behavior solely because routing selected RTK.

**Status**: Accepted; unplanned. Product changes are blocked until an approved plan restores PowerShell-native argument boundaries or conservatively keeps the affected shape on PowerShellDirect.

**Source**: Complete-source Claude Opus 5 review of `server/PtkMcpServer/Execution/ExecutionPlanner.cs` at `3548eb8` (blob `234c3833b6be6a22d6d984058090fd252f7244ba`), integrated with `ExecutionPlan`, `RtkProcessRunner`, `RunspaceHost`, focused tests, AST inspection, and real native argv probes.

## Evidence

`ExecutionPlanner.TryCreateRtkArgumentVector` handles a `CommandParameterAst` with a constant argument by concatenating `parameter.Extent.Text[..prefixLength]` and the argument value into one immutable-vector element. This covers quoted and unquoted attached values and the whitespace-separated colon form: for `-foo: bar`, the parameter extent is exactly `-foo: bar`, its argument starts at `bar`, and concatenation freezes one element containing the embedded space. `RtkProcessRunner.CreateStartInfo` then adds each vector element once to `ProcessStartInfo.ArgumentList`.

PowerShell 7 native probes against `node` showed both supported `PSNativeCommandArgumentPassing` modes preserve two target argv elements: `-x:"joined value"` becomes `['-x:', 'joined value']`, `-x:value` becomes `['-x:', 'value']`, and `-foo: bar` becomes `['-foo:', 'bar']`, under both `Standard` and `Windows`. A second unquoted probe encoded every received argument as UTF-8 Base64; both modes returned exactly `["LXg6","dmFsdWU="]`, which decodes to `['-x:', 'value']`. The planner instead freezes one element for every form. Existing `ExecutionPlannerTests.Rtk_argument_vector_preserves_constant_native_semantics` explicitly expects an incorrect merged element and therefore guards the defect rather than native equivalence.

## Predicted observable failure

A warm invocation routes a constant application command containing attached PowerShell parameter syntax through RTK. A target that distinguishes option and operand boundaries, positional count, or a literal joined token sees a different `argc`/`argv` than PowerShellDirect and can reject the command, select a different option, or consume later operands differently.

## Required repair

At the `CommandParameterAst { Argument: ConstantExpressionAst }` branch, preserve the separate parameter-prefix and argument boundaries proved by both supported native passing modes, including whitespace-separated colon syntax, or return ineligible when exact reconstruction cannot be proved. Retain the expansion exclusion and prefix-bound checks. If the repair emits two vector entries, adjust the builder capacity/finalization invariant rather than calling `MoveToImmutable` with count no longer equal to capacity. Do not change valueless parameters, string constants, or numeric extent spelling.

Correct the existing merged-vector assertion and add independent Standard/Windows guards for unquoted, quoted, and whitespace-separated colon values, plus a target-visible argc/argv integration guard through `RtkProcessRunner`. Temporarily revert only the repair, prove the guards fail, restore it, then run focused planner/dispatch/runner tests and the repository verification entry point.

## Reviewer

Claude Opus 5 (`claude/@gcp-vertexai-us-global-integration/anthropic.claude-opus-5/max/frontier`) accepted this as a distinct current MEDIUM finding after bounded source review and focused adjudication. `s3-rtk-preference-isolation` isolates execution from mutable warm preferences; it does not cover planner-created argument-boundary changes. No product or test file changed in this finding slice.
