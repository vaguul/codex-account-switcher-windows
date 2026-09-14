# Security policy

## Supported versions

Security fixes are applied to the latest published release.

## Reporting

Open a GitHub security advisory rather than a public issue when the report involves credential exposure, path traversal, process confusion, or rollback failure. Do not upload a real `auth.json`, access token, refresh token, DPAPI vault file, account identifier, or diagnostic archive containing them.

## Security boundary

- Saved snapshots and recovery backups are encrypted with Windows DPAPI `CurrentUser` semantics and protected by a current-user ACL.
- DPAPI does not protect against malware already executing as the same Windows user.
- The active Codex `auth.json` remains plaintext in the Codex-owned directory because Codex must read it.
- The application has no telemetry, cloud sync, remote server, proxy, updater, or background network polling.
- The executable is currently unsigned. Verify release hashes and download only from this repository.
