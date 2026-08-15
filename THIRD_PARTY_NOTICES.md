# Third-party notices

DeepSeek Harness Desktop bundles or references third-party software. Those components are not relicensed by this repository's MIT license.

## Bundled runtime

- **Node.js 24.18.0** — distributed under the Node.js license. The complete `node/LICENSE` file, including bundled third-party notices, is preserved inside `runtime.seed.zip` and the extracted runtime.
- **DeepSeek Harness / `@deepseek-ai/dsh` 0.1.0-rc.6** — MIT License, Copyright © 2026 DeepSeek. Source: <https://github.com/deepseek-ai/deepseek-harness>.
- **pnpm 11.19.0** — MIT License. Source: <https://github.com/pnpm/pnpm>.
- Packages installed below the runtime's `node_modules` retain the license files and package metadata shipped by their publishers.

## Application dependency

- **Microsoft WebView2 SDK 1.0.4129.50** — Copyright © Microsoft Corporation. The SDK package is used under the license included in the NuGet package. The WebView2 Runtime is a separately installed Microsoft system component. Project page: <https://aka.ms/webview>.

## Optional featured skins bundled in the installer

The following payloads are included for offline first-run selection. They are installed only when the user keeps or enables them, are disabled by default, and retain their upstream licenses:

- **deep-whale-day-night-theme 0.1.1** — CC BY-NC-SA 4.0; commercial use is not permitted by that license. Source: <https://github.com/GGBond2424648901/deep-whale-day-night-theme>.
- **DeepSeek Harness Themes / `@dshthemes/ui` and `@dshthemes/core` 0.2.0** — MIT License. Source: <https://github.com/orxz/deepseek-harness-themes>.
- **clsx 2.1.1** — MIT License; bundled only as the offline dependency required by `@dshthemes/ui`. Source: <https://github.com/lukeed/clsx>.

The first-run screen also allows either featured skin payload to be removed instead of installed. At most one featured skin is enabled at a time.

## Packaging tools

- **.NET 10** is used to build the self-contained application and is licensed by Microsoft under its applicable .NET licenses.
- **NSIS 3.12** is used to create the Windows installer and is not included in the source repository.

The original license files distributed with third-party binaries remain in their corresponding packages. If this notice conflicts with an upstream license, the upstream license controls for that component.
