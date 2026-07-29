# muc-4: Versioned payload directories conflict with one PTK home

**Intake verdict:** DECLINED.

`.agents/review/mcp-upgrade-continuity-options.md:73` does not require multiple
install roots; immutable version directories can live below the approved single
`~/.ptk` home. The predicted release-plan conflict therefore does not follow
from the option as written. A future plan should still make the
`~/.ptk/versions/<version>`-style containment explicit.

Source openreview: Claude Code `2.1.220` /
`@gcp-vertexai-us-global-integration/anthropic.claude-opus-5` / `max` /
frontier, owner-selected inline for this session. Reviewed
`d32f2a06e451df02cfa9b63d5c0dda570d1262ec..a308bdadbbda954340f48d63772e72787e9f6990`.
