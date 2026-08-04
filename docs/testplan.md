# ptk test plan

Drive ptk at its limits and report what breaks. Each item below is a test to
run deliberately, not a thing to notice in passing.

Run every test you can. Record the exact script you sent and the verbatim
response for each. A paraphrase is not reproducible.

## 1. Object shaping

ptk compresses PowerShell objects before they are formatted. `[active member
not evaluated]` in a response means a value was dropped.

1.1 Emit a plain framework object: `Get-Culture`, `[System.TimeZoneInfo]::Local`,
`Get-Item .`, `Get-Process -Id $PID`. Did every property you would need come
back?

1.2 Emit objects from a module: `Get-Service`, certificates from `Cert:\`, AD or
Exchange objects if you have them. Same question.

1.3 `Select-Object` a subset of properties from each of the above. Do the
selected values survive?

1.4 Nest: `[pscustomobject]@{ Inner = Get-Culture; List = @(Get-Process | Select -First 3) }`.

1.5 Emit 1, 40, 500, and 5000 objects. Where does the response change shape,
and is the change explained in the output?

1.6 Emit a mixed stream: objects and strings interleaved in one pipeline.

1.7 Define a class with `Add-Type` whose `ToString()` or a property getter
writes to a global variable, emit an instance, then read the variable. It must
be untouched — capture must never run your code. Try it again with your class
deriving from a framework type.

1.8 Emit a type whose `ToString()` is enormous — a `StringBuilder` holding a
megabyte. Is the response bounded?

## 2. Text and streams

2.1 A native command writing only to stdout.

2.2 A native command writing only to stderr.

2.3 `2>&1` on a command that writes to both. Did the stderr text survive?

2.4 A command that exits non-zero. Is the exit code reported?

2.5 A command producing no output at all.

2.6 `throw` from PowerShell. Then throw an exception type defined by
`Add-Type`. Compare the error text you get back.

2.7 Text with embedded ANSI colour codes, tabs, CRLF, and non-ASCII
characters. Anything mangled?

2.8 Output several megabytes of text.

## 3. Recovery handles

3.1 Produce output large enough to be truncated inline. Note the handle.

3.2 Read the handle back in chunks with `offset` and `maxBytes`. Does the
content reassemble into what the command actually produced?

3.3 Search the handle for a string you know is only in the truncated portion.

3.4 Ask for `status` and `list`.

3.5 Read a handle from an earlier invocation, after other calls have run.

3.6 Read a handle after resetting the session that produced it.

3.7 Invent a plausible-looking handle that does not exist. Is the refusal
clear?

## 4. Sessions

4.1 Open a named session. Set a variable, import a module, change directory.
Confirm all three survive the next invocation.

4.2 Open a second session. Set the same variable name to a different value.
Confirm neither leaks into the other.

4.3 Run slow work in two sessions simultaneously. Did they actually run at the
same time?

4.4 Keep opening sessions until you are refused. How many did you get, and is
the refusal clear?

4.5 Close a session, then invoke against it.

4.6 Open a session, close it, open it again under the same name. Is the old
state gone?

4.7 Invoke against a session name you never opened.

## 5. Failure and recovery

5.1 Time out an invocation: `Start-Sleep -Seconds 60` with a two-second
budget. What does the response say?

5.2 Immediately invoke again in that session. Then keep retrying. How long
until it works, and does the interim response explain itself?

5.3 Was the warm state from 4.1 still there afterwards? Should it have been?

5.4 Repeat the timeout-and-recover cycle five times in one session. Does it
degrade?

5.5 Kill the worker process for a session from outside, then invoke.

5.6 `ptk_reset` a session, then invoke.

5.7 Run something that consumes memory hard, or spawns many children, and see
what containment does.

5.8 Run a command that waits on stdin or expects a TTY.

5.9 Run something that spawns a background process outliving the call, then
check whether the session can be closed.

## 6. Routing

ptk runs PowerShell itself and sends native commands to rtk for filtering.

6.1 A bare native command: `git status --short`. Was the output compressed?

6.2 The same command with an argument rtk will not recognise. Compare.

6.3 A compound: `git status && git log --oneline -5`.

6.4 A native command inside a PowerShell pipeline:
`git log --oneline -20 | Select-Object -First 3`.

6.5 The same script with `route=pwsh`, then `route=rtk`. Do the responses
differ, and does either mislabel what happened?

6.6 Define `function git { 'MINE' }` in a session, then invoke `git status`.
Which ran? If the real git ran, that is a command other than the one you sent.

6.7 A command whose arguments contain spaces, single quotes, double quotes,
and a literal `$`. Did they arrive intact? Verify by having the command echo
its own arguments.

6.8 A script containing the literal token `rtk` in an argument.

## 7. Dialect boundary

It is PowerShell 7, not bash. For each, report whether it worked, failed
loudly, or silently did something other than what the syntax means.

7.1 `ls -la`

7.2 `cat file | head -20`

7.3 `foo && bar` and `foo || bar`

7.4 `export FOO=bar` and `FOO=bar somecommand`

7.5 `$(date)` and backtick substitution

7.6 A heredoc

7.7 `grep -r pattern .`

7.8 `rm -rf` on a temp directory you created

7.9 `bash -lc '...'` wrapping a bash script whole

7.10 A path with a space, unquoted and quoted

## 8. Scale

8.1 Fifty invocations in a row in one session. Does latency drift?

8.2 One invocation producing 100k lines.

8.3 One invocation producing 50k objects.

8.4 A deeply nested object graph, five levels down.

8.5 A pipeline that emits steadily for two minutes.

8.6 The largest single script you can reasonably send.

## 9. Tool descriptions

9.1 Read every tool's description and parameter list. List anything that is
untrue, ambiguous, or missing.

9.2 List anything you had to learn by trial that the descriptions should have
told you.

9.3 List every parameter you never found a use for, and every one you wanted
and did not find.

## Reporting

One GitHub issue.

```powershell
gh issue create `
  --repo AlsoBeltrix/PowerShell-Token-Killer `
  --title "Test report: <platform>" `
  --body-file report.md
```

Lead with the failures. For each: the test number, the exact script, the
verbatim response, and what you expected instead.

Then list what passed, by number. Then what you could not run, and why.

State your platform and the ptk version from `ptk_state`.

Do not fix anything you find, and do not modify ptk. If a report already
exists for this version, comment on it rather than opening another.
