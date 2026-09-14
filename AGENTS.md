# Repository guidance

- `README.md` owns product scope and user-facing behavior.
- `docs/ARCHITECTURE.md` owns the switching protocol and security boundaries.
- The switcher may replace only Codex's active `auth.json`. It must not copy, edit, or delete sessions, skills, memories, configuration, logs, or projects.
- Saved authentication snapshots and recovery backups must remain DPAPI-encrypted at rest.
- A change to switching, storage, process handling, or recovery requires a focused regression test.
- Never add real authentication files, tokens, account identifiers, or captured user data to tests, docs, logs, releases, or issues.
