# Contributing

## Before opening an issue

- Read [the troubleshooting guide](docs/TROUBLESHOOTING.zh-CN.md).
- Reproduce the problem with the latest source build.
- Remove cookies, authorization headers, signed query strings, private hosts, media identifiers, and encryption keys from logs.

## Pull requests

1. Keep the application compatible with .NET Framework 4.8 and C# 5.
2. Avoid new runtime or NuGet dependencies unless the benefit is substantial.
3. Preserve direct native process invocation; do not pass user input through a shell.
4. Add or update self-tests for parsing, quoting, settings, or secret-handling changes.
5. Run:

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipDesktopCopy
   ```

6. Update documentation when behavior or user-visible controls change.

Do not add real media URLs, access tokens, cookies, authorization headers, or keys to tests and examples.
