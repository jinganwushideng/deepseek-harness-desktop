# Contributing

Thank you for helping improve DeepSeek Harness Desktop.

## Before opening a change

1. Search existing Issues and Pull Requests.
2. Open an Issue first for large behavior or data-format changes.
3. Never commit API keys, `.dsh` data, backups, logs, runtime archives, installers, or local SDK/tool directories.

## Development

Requirements: Windows x64 and .NET 10 SDK. `runtime.seed.zip` is not required for normal compilation and unit tests.

```powershell
dotnet restore .\DeepSeekHarnessDesktop.Tests\DeepSeekHarnessDesktop.Tests.csproj
dotnet test .\DeepSeekHarnessDesktop.Tests\DeepSeekHarnessDesktop.Tests.csproj -c Release
```

To build the offline installer, install NSIS 3.12 and run:

```powershell
.\scripts\prepare-runtime.ps1
.\Installer\build-installer.ps1
```

## Pull requests

- Keep changes focused and explain user-visible behavior.
- Add tests for state, parsing, backup, plugin, Skill, or security-sensitive logic.
- Preserve the current-user, localhost-only, and no-plaintext-secret guarantees.
- Include screenshots for visual changes, using clean test data without personal paths or sessions.
- Confirm `dotnet test` passes before requesting review.

By contributing, you agree that your contribution is provided under the MIT License.
