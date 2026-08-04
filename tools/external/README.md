# External tool policy

The alpha starts with built-in .NET, Win32/WinRT, installed Windows SDK tools,
and Windows Performance Recorder. Add an executable tool only after documenting
a concrete capability failure.

Before downloading a tool, register its exact version, canonical source URL,
SHA-256, SPDX license, purpose, and supported platform in `tools.lock.json`.
Binaries go under ignored `tools/external/installed/`; never download or update
them at application runtime.

## Registered tools

The authoritative registry is `tools.lock.json` (9 tools: x64dbg, System
Informer, ReClass.NET, ILSpy, Ghidra, Cursor Agent CLI, OpenCode,
PSScriptAnalyzer, Grok Build). The remainder of this section documents the
PSScriptAnalyzer integration because it is part of the repo's quality gates.

PowerShell scripts in this repo must pass the PSScriptAnalyzer gate before
landing. `scripts/install-psscriptanalyzer.ps1` downloads the pinned, hash-
verified module into `tools/external/installed/`; `scripts/invoke-scriptanalyzer.ps1`
runs the gate (settings + repo custom rules). Both are wired into
`scripts/validate.ps1` and CI.
