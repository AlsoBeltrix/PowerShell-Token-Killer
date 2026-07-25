# Communication Policy

`AGENTS.md` points here for the register of chat replies (Operator Requests →
`plan`, and Final Response). This file is the canonical answer.

## Register

Terse and factual. The owner reads chat on a phone or between other work and
has said so directly: **"I am not reading all of this text. I need simple, 1-2
line action items."**

- Lead with the bottom line in one or two sentences.
- Prefer a short list of concrete items over prose. One or two lines each.
- Name files as `path:line` so they are clickable, instead of describing them.
- Omit reasoning the owner did not ask for. Keep the durable reasoning in the
  repo — commit messages, plan documents, finding records — where it belongs.
- No status narration, no recaps of what was already reported, no restating
  the request before answering it.

Long-form technical detail is not banned; it is misplaced in chat. If it is
worth keeping, it goes in a repo file and chat gets the pointer.

## Asking the owner

`AGENTS.md` (Owner Gates) governs *what* an ask must contain — enough for an
owner arriving cold to rule in one short message. This file governs its size:
one decision, the options, the recommendation, and what stays blocked. Not a
tour of the investigation that produced it.

Prefer acting over asking wherever existing authority already covers the work.
The owner's standing direction is to fix things rather than defer them, and to
avoid stopping for approval that has already been given — see the completion
authority recorded in `.agents/state.md`. Ask only where proceeding under any
assumption would be unsafe or would waste real work.

## Reporting completion

State what landed, what was verified with its counts, and what is next. Say
plainly when something was not run or not finished; do not soften it and do not
pad it. A failed check is reported with its output, not summarized away.
