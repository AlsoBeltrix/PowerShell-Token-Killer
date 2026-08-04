# ptk test plan

You are the user of this tool. Other agents are the only users it has. Use ptk
for real work, notice where it fails you, and report that.

Use the ptk tools already available in your session. There is nothing to
install and nothing to set up.

## What to report

Anything that made ptk harder to use than doing the work another way. In
particular:

- **Wrong output.** A value that came back missing, truncated, mislabelled, or
  different from what the command actually produced.
- **A command that did something other than what you submitted.**
- **State you expected to survive that did not**, or state that survived when
  you expected it gone.
- **A response you could not act on** — an error that did not say what to do,
  a marker you could not interpret, a handle that did not resolve.
- **A tool description that told you something untrue**, or omitted something
  you had to discover by trial.
- **Anything you had to work around**, including workarounds that succeeded.
  Say what you tried first and why it failed.

Report what worked as well. If a hard thing was easy, that is data.

## Ground to cover

Do real work, not synthetic probes, wherever you can. The point is usage data.

**Ordinary use.** Run the commands you would actually run: inspect files,
query git, run a build or a test suite, filter logs, call native tools. Judge
whether the output you got back was enough to act on without rerunning
anything.

**Objects.** PowerShell returns objects, and ptk compresses them before they
are formatted. Pipe cmdlets that return rich objects — services, processes,
files, certificates, culture and time zone info, anything from a module you
have. Check whether the values you needed survived. Look for
`[active member not evaluated]`, which means a value was dropped.

**Text.** Native command output, logs, stderr redirected with `2>&1`,
multi-megabyte output. Check text arrived as text and that redirection did not
lose it.

**Large output.** Push past whatever the inline limit is. You should get a
recovery handle. Use it. Read it in chunks, search it. Confirm you can reach
content the inline response omitted, and that it matches what the command
produced.

**Sessions.** Open your own named sessions. Import a module, connect to
something, set variables, change directory — then come back later and see what
is still there. Run different work in two sessions at once and check they did
not contaminate each other. Find the session limit and see what happens at it.

**Recovery.** Time an invocation out. Kill something. Reset a session. Then
keep working in it. The question is whether you can continue, and whether ptk
told you clearly what state you were in.

**Long work.** Run something slow enough to need a raised timeout. Run
something that produces output steadily for a while. Run something
interactive or that expects a TTY and see what happens.

**Routing.** ptk sends native commands to rtk for filtering and runs
PowerShell itself. Notice when output was compressed and when it was not, and
whether the route it chose matched what you wanted. Try `route=pwsh` when you
want exact PowerShell. Define a function whose name shadows a native command
and check which one ran.

**The dialect boundary.** It is PowerShell 7, not bash. Try bash-shaped things
— pipelines into `head`, heredocs, `&&` chains, `$(...)`, globs, quoting with
embedded spaces. Report what silently did something different rather than
failing loudly.

**Failure modes.** Commands that exit non-zero, throw, write only to stderr,
produce nothing, or hang. Check the exit code and the error text reached you
intact.

## How to report

One GitHub issue per session's worth of work.

```powershell
gh issue create `
  --repo AlsoBeltrix/PowerShell-Token-Killer `
  --title "Usage report: <what you were doing>" `
  --body-file report.md
```

Lead with what got in your way. For each item give the exact script you sent,
what came back, and what you expected instead. Verbatim output — a paraphrase
of a defect is not reproducible.

Include your platform, and the ptk version from `ptk_state`.

Say how much of your work you could complete through ptk, and where you fell
back to something else. That number matters more than a list of passes.

If a report already exists for this version, comment on it instead of opening
another.
