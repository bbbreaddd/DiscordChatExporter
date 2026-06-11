# AGENTS.md

Guidance for AI agents working in this repository.

## Overview

DiscordChatExporter exports Discord message history to HTML, TXT, CSV, or JSON.
It ships as a CLI (`DiscordChatExporter.Cli`) and a desktop GUI (`DiscordChatExporter.Gui`),
both built on top of a shared core library (`DiscordChatExporter.Core`).

## Solution layout

- `DiscordChatExporter.Core` — Discord API client, exporting/rendering pipeline,
  markdown parsing, models. Platform-agnostic; no UI/CLI dependencies.
- `DiscordChatExporter.Cli` — CliFx-based command-line app (`Commands/`).
- `DiscordChatExporter.Gui` — Avalonia + CommunityToolkit.Mvvm desktop app
  (`ViewModels/`, `Views/`, `Services/`, `Framework/`, `Localization/`).
- `DiscordChatExporter.Cli.Tests` — xUnit tests for the CLI/Core, organized under `Specs/`.

## Stack & conventions

- .NET 10 / C# 14 (`Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`).
- Avalonia GUI uses CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`,
  `partial` properties).
- HTTP resilience via Polly (`Core/Utils/Http.cs`).
- Localization: `LocalizationManager` exposes `Get([CallerMemberName])`; per-language
  dictionaries fall back to `LocalizationManager.English.cs` for missing keys, so new
  strings only strictly need an English entry.

## Build, format, test

```
dotnet build                                   # build everything
dotnet build -t:CSharpierFormat                # apply CSharpier formatting
dotnet test                                    # run tests (needs DISCORD_TOKEN, see below)
```

**Important:** `dotnet build` automatically runs CSharpier (`CSharpier.MsBuild`, pinned in
`Directory.Packages.props`) and rewrites files in place to match its formatting rules.
After building, run `git status`/`git diff` and check whether unrelated files were
reformatted — this can happen if the repo's existing formatting drifts from the pinned
CSharpier version. Revert any such incidental changes (`git checkout -- <file>`) so your
diff only contains intentional edits. To build without triggering this, pass
`-p:CSharpier_Bypass=true`.

CI (`.github/workflows/main.yml`) runs `dotnet build -p:CSharpier_Bypass=true` followed by
`dotnet build -t:CSharpierFormat` to verify formatting separately from compilation.

### Tests

`DiscordChatExporter.Cli.Tests` makes live calls to the Discord API and requires a
`DISCORD_TOKEN` (via `dotnet user-secrets` or the `DISCORD_TOKEN` environment variable) —
see `Infra/Secrets.cs`. Without it, tests that touch the API throw
`InvalidOperationException("Discord token not provided for tests.")`. This is pre-existing
and unrelated to most changes; don't try to "fix" it.

## Docs

User-facing docs live in `.docs/` (CLI usage, GUI usage, tokens/IDs, scheduling, etc.) and
`Readme.md`. Update these when changing user-visible behavior (new CLI options, settings,
commands).
