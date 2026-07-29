# ssu-8: Retention does not promise operator-selectable rollback

**Intake verdict:** DECLINED.

In this plan, version pinning describes process lifetime: an already-running
server remains pinned to the payload it opened while new launches follow the
new activation record (`.agents/plans/mcp-side-by-side-upgrade.md:210` and
`:383-389`). Retaining old directories is required for those running processes;
the plan does not promise an operator `activate`, `pin`, or rollback command.
The predicted inability to use such a command therefore assumes an unsupported
feature interpretation. Slice 6's wording at `:356` should be read and documented
as process/runtime pinning plus disk retention, not as a new maintenance verb.

Source openreview: Claude Code `2.1.220` /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / `max` /
frontier, owner-selected inline for this session. Reviewed
`c4bd2af884faecda81af6eeb9bb3b698d5141bb7..caf467e423105a621b1431302575b242f77791ac`.
