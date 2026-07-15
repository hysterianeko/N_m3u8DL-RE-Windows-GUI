# Security

## Sensitive data

Playlist URLs can contain short-lived access tokens. HLS keys, cookies, authorization headers, and signed URLs must be treated as secrets.

- Do not include real media URLs, query tokens, cookies, or keys in GitHub issues.
- Redact private hosts and identifiers before sharing logs.
- Rotate a token or key immediately if it was posted publicly.
- Do not commit local `.key`, `.iv`, settings, logs, or build output.

The application does not persist the current media URL or GUI log. A manually supplied HLS key or IV is converted into a 16-byte temporary file protected for the current Windows user and `SYSTEM`. The temporary file is removed when the task exits, is cancelled, fails to start, or when the application closes. A crash can leave a protected temporary file; files older than one day are removed on the next launch.

Automatic dependency setup runs only after user confirmation. It downloads pinned HTTPS GitHub Release assets by direct connections that do not use the Windows system proxy, checks fixed lengths and SHA-256 digests, extracts only expected payloads, and commits them under the current user's LocalAppData directory only after verification. Release packages do not contain N_m3u8DL-RE or FFmpeg binaries.

## Reporting

For a potential vulnerability, do not open a public issue containing exploit details or secrets. Use [GitHub private vulnerability reporting](https://github.com/hysterianeko/N_m3u8DL-RE-Windows-GUI/security/advisories/new) and provide a minimal, redacted reproduction.
