# RoslynMcpServer

[🇷🇺 Читать на русском (Russian Version)](#russian-version)

**Why use this over standard FileSystem MCPs?**
Unlike basic file-reading servers, this Model Context Protocol (MCP) server leverages **Roslyn**. Your AI agent (Cursor, Cline, etc.) doesn't just read plain text—it sees C# code through the eyes of the compiler. It can get precise diagnostics without a full rebuild, **apply built-in Roslyn code fixes** (add using, implement interface, fix typos), find symbol references, and perform safe semantic refactoring, drastically reducing LLM hallucinations.

It is designed for AI-driven C# development (with secondary Python support), focusing on:
- Parsing and analyzing `*.sln`/`*.slnx`/`*.csproj` via Roslyn.
- Safe, context-aware file read/write operations.
- CLI integration: running `dotnet build`, `dotnet test`, and custom commands.
- Compact responses optimized for LLM context windows, backed by detailed server-side logging.

## ⚠️ Security Disclaimer
This server grants the AI agent read, write, and execution privileges (via the `dotnet` CLI) within your workspace. **Always use source control (Git)** to track changes. Do not run the server as Administrator.

## Prerequisites
- Strictly **.NET 10 SDK** installed on your machine. *(Earlier SDK versions are not supported and will fail to build).*
- An MCP-compatible IDE or client (e.g., Cursor, OpenCode, VS Code with Cline).

## Setup & Connection (Local)

It is highly recommended to run the **published executable**. This ensures instant startup without the overhead of `dotnet run`. The project is configured with `ReadyToRun` enabled and `PublishSingleFile` disabled (to ensure Roslyn can dynamically load MSBuild and analyzer dependencies).

### 1. Publish the Server (Run once or after code updates)
From the repository root (replace the RID if you are not on Windows x64):

```bash
dotnet publish RoslynMcpServer.csproj -c Release -r win-x64
```

Your compiled binary will be at:
- Windows x64: `bin/Release/net10.0/win-x64/publish/RoslynMcpServer.exe`
- Linux/macOS: Same relative path under your specified `-r` flag.

### 2. Configure the MCP Client

**Option A: Via Cursor UI**
1. Go to **Cursor Settings** -> **Features** -> **MCP** -> **+ Add New MCP Server**.
2. Type: `stdio`.
3. Command: Enter the **absolute path** to your published executable (e.g., `D:\Devel\RoslynMcpServer\bin\Release\net10.0\win-x64\publish\RoslynMcpServer.exe`).

**Option B: Via `mcp.json`**
Add the absolute path to your configuration file (see the provided `mcp.json` and `.cursor/mcp.json` examples in the repo).
*Optional:* Pass `env` with `ROSLYN_MCP_WORKSPACE` set to the **repository root** (folder containing `global.json`) so MSBuild.Locator and `run_dotnet_build` use the same SDK as your solution.

**Option C: OpenCode (`install2opencode.ps1`)**

After `dotnet publish`, the publish folder contains [`install2opencode.ps1`](install2opencode.ps1) and [`AGENTS.md.sample`](AGENTS.md.sample) next to `RoslynMcpServer.exe`.

From the root of the **project you want to configure** (your application repo):

```powershell
cd D:\Devel\YourApp
& "D:\Devel\RoslynMcpServer\bin\Release\net10.0\win-x64\publish\install2opencode.ps1"
```

The script:

- Creates or updates **`opencode.json`** in the target directory, registering `RoslynMcpServer` as a local MCP server (`type: local`, `enabled: true`, **`timeout`: `600000`** ms = 10 minutes).
- Creates or updates **`AGENTS.md`**: if Roslyn MCP behavioral rules are not already present, merges content from the bundled `AGENTS.md.sample` (creates a new file or appends to an existing one).

**OpenCode MCP host timeout:** OpenCode’s default for MCP `tools/call` is about **60 seconds**. That is **not** the tool argument `timeoutSeconds` on `run_dotnet_test` / `run_dotnet_run`. Without raising the host timeout, long build/test calls fail with `McpError: MCP error -32001: Request timed out` (~60s) even if you pass `timeoutSeconds: 900`. Set `"timeout": 600000` (or higher) on the server entry — see [`opencode.json.sample`](opencode.json.sample). `install2opencode.ps1` writes this for you. If an older OpenCode build ignores per-server `timeout`, also try `"experimental": { "mcp_timeout": 600000 }` in `opencode.json`.

Example server block:

```json
"roslyn-mcp-server": {
  "type": "local",
  "command": ["C:/path/to/RoslynMcpServer.exe"],
  "timeout": 600000,
  "enabled": true
}
```

| Parameter | Description |
| --- | --- |
| `-BinaryPath` | Optional. Absolute path to `RoslynMcpServer.exe`. When omitted, the script uses the binary next to itself. |
| `-ProjectPath` | Optional. Target project root. Defaults to the current working directory. |

Restart OpenCode or reload MCP servers after running the script.

## Config (`RoslynMcp.jsonc`)

Workspace and search output are configured via a `RoslynMcp.jsonc` file (JSONC = JSON with comments). It is read from **two places at startup and merged**, exactly like the `TfsMcp`/`MCP` servers: the **exe directory** first, then the **current working directory** (cwd wins for conflicting keys). If no config file is found, the workspace falls back to `ROSLYN_MCP_WORKSPACE` (or cwd) solution discovery — semantic tools then guide you toward source candidates.

Relative `workspace-path` resolves against the MCP process current directory (not the exe directory) — prefer absolute paths.

Keys (flat kebab-case):

| Key | Default | Description |
| --- | --- | --- |
| `workspace-path` | — | `.sln` / `.slnx` / `.csproj` to load **lazily** (the first semantic call after start loads it and can take minutes). |
| `configuration` | — | MSBuild `Configuration` global property (e.g. `Sit-Debug`). Inherited by `run_dotnet_build`/`run_dotnet_test`/`run_specific_test` when their arg is omitted. |
| `platform` | — | MSBuild `Platform` global property (e.g. `AnyCPU`). Inherited by build/test when their arg is omitted. |
| `target-framework` | — | MSBuild `TargetFramework` (inner TFM, e.g. `net10.0`) when the solution uses `TargetFrameworks`. |
| `max-results` | `50` | Unified cap for `find_*`/`search_code` results; per-call argument overrides it. |
| `preview` | `false` | Default for the `preview` argument of the `find_*` search tools (include source line text). |

Example `RoslynMcp.jsonc` — the repo ships [`RoslynMcp.jsonc.sample`](RoslynMcp.jsonc.sample) with all keys and comments; copy it to the exe directory for global defaults / to the project root for per-project overrides (cwd wins):

```jsonc
{
  // Workspace: path + MSBuild global properties (lazy loading)
  "workspace-path": "F:/src/MyApp/MyApp.sln", // .sln / .slnx / .csproj
  "configuration": "Sit-Debug",               // optional
  "platform": "AnyCPU",                       // optional
  "target-framework": "net10.0",              // optional (TargetFrameworks → inner TFM)

  // Search output (optional)
  "max-results": 50,  // unified cap of search methods (default 50)
  "preview": false    // default for preview (false — the model decides)
}
```

Use `reload` after changing these values (or to re-load a different `workspace-path` when the solution changes).

If the config is **not** set, the agent can still open a workspace explicitly with **`load_workspace`** (an absolute `.sln`/`.slnx`/`.csproj` path, no server restart needed) — the configured workspace otherwise loads lazily on the first semantic call. **`reset_workspace`** disposes the in-process `MSBuildWorkspace` and drops the cached solution (frees memory / clean state, e.g. before switching solutions or parameters via `load_workspace`).

## Agent tools by version

Tracks MCP tools relevant to [`AGENTS.md.sample`](AGENTS.md.sample) (copy into app repos as `AGENTS.md`). Current server version: see `RoslynMcpServer.csproj`.

### v1.3.0

- **`load_workspace` / `reset_workspace` restored** — alongside config + lazy load + `reload` (42 tools): `load_workspace` opens a `.sln`/`.slnx`/`.csproj` explicitly **without restarting the server** (no config set, or a different solution/parameters); `reset_workspace` disposes the workspace and drops the cache (frees memory / clean state, before switching solutions/parameters).
- **Conditional "no workspace" guidance** — when `workspace-path` is set in `RoslynMcp.jsonc`, the agent is **not** directed to `load_workspace` (primary recommendation: config / `reload`); `load_workspace` is only an option to open a different path.
- **Host abort during lazy load** — a host timeout/cancel mid lazy load now returns **Workspace Load Cancelled (client abort)** (with the `timeout ≥ 600000` hint) instead of the misleading "No active workspace".
- **Broken `workspace-path`** — a configured path that does not exist on disk is a warning + fallback as if the config were absent: file-scoped tools fall back to walk-up, solution-wide tools report "configured workspace … not found".
- **FQN overloads documented** — FQN does not distinguish method overloads (all same-name overloads in the same type are reported); the `find_usages` summary table now carries a `First` (line:col) column. `get_call_graph` `maxNodes` is the cap on the **total** node count; `find_implementations` error branches carry the disk-sync note; `ToOffset` validates `column` against the line length.

### v1.2.0

- **Workspace via config + lazy load** — `load_workspace` / `reset_workspace` are removed; the workspace comes from `RoslynMcp.jsonc` `workspace-path` and is loaded **lazily** on the first semantic call (which can take minutes — set the host MCP `timeout` ≥ 600000 ms). The single **`reload`** tool re-loads the workspace (dispose + load) from the config or explicit arguments; call it after `dotnet build` (generated `obj`) / `.csproj`/`.sln`/`Directory.Build.props` edits.
- **−21 methods** (duplicates of OpenCode host tools + AST noise). Owned file read/write/edit (`get_file_content`, `read_file_range`, `apply_patch`, `update_file_content`, `read_log_tail`, `tail_tool_log`, `get_changed_files`, `list_directory_tree`) and shell/proc (`execute_dotnet_command`, `manage_agent_scratchpad`) and AST extras (`add_using`, `remove_using`, `remove_member`, `add_type_to_class_bases`, `move_type_to_new_file`, `generate_test_method_stub`) are gone — use the host `read`/`write`/`edit`/`bash`/`git` tools.
- **`add_*_to_class` → `add_member`** — `add_method_to_class` + `add_property_to_class` + `add_field_to_class` became a single `add_member(filePath, className, memberSource)`.
- **FQN + position in `find_*`** — `find_symbol_definition`/`find_usages`/`find_implementations` accept a fully-qualified name (exact match); `find_symbol_references`/`get_call_graph`/`rename_symbol` accept a 1-based `line`/`column` to select the exact symbol.
- **`line:col` always** — search results return **1-based line:column** regardless of `preview`; `preview` only adds the source line text (default: positions only).
- **`preview`** — unified parameter on `find_*` search tools (default `false`; overridable by config `preview`).
- **cap 50 → temp file** — results exceeding the cap (config `max-results`, default 50) are written in full to `%Temp%\roslyn-mcp\<yyyyMMdd-HHmmss-<short-guid>>\result.md`; the response returns the count + path instead of silently truncating.

> **Migration for existing `AGENTS.md` with `load_workspace`:** *(superseded by v1.3.0 — `load_workspace` / `reset_workspace` are back; see above.)* replace `load_workspace <path>` / `reset_workspace` with the `reload` tool (or by setting `workspace-path` in `RoslynMcp.jsonc`). The workspace loads lazily from the config — remove any startup `load_workspace` calls; the first semantic call after server start may take minutes (host timeout ≥ 600000 ms). Drop references to the removed file/shell tools and use the host `read`/`write`/`edit`/`bash`/`git` tools instead.

### v1.1.0

- **Disk sync for saved `.cs`** — after `load_workspace`, a `FileSystemWatcher` records dirty source paths (not every keystroke: unsaved editor buffers are ignored). Before `find_symbol_*` / `find_usages` / `get_class_skeleton` / test discovery, only those files are read and applied with one `TryApplyChanges`. Host/git/`dotnet format` edits of existing files show up without `reset_workspace`. New `.cs` under a project folder are `AddDocument`’d; deleted files are removed. `.csproj`/`.sln`/`Directory.Build.props` set a graph-stale hint and skip the `load_workspace` cache — still no automatic `OpenSolutionAsync` from the watcher. `reset_workspace` remains for generated `obj` files after build. Linux uses inotify (watch-limit errors log and degrade; they do not crash the process).

### v1.0.35

- **VS 2026 / MSBuild 18 BuildHost (`XMakeElements`)** — `Microsoft.CodeAnalysis.*` **5.9.0** (Roslyn AppDomain isolation for the net472 BuildHost). `load_workspace` / `find_symbol_*` no longer die with a raw `TypeInitializationException` on machines with Visual Studio 2026; residual crashes return dedicated **Workspace Load Failed (VS 2026 / MSBuild 18 BuildHost)** and are **not** `MCP_MSBUILD_SDK_MISMATCH`. Workaround: load a single SDK-style `.csproj`. `search_code` / host Grep still work.

### v1.0.34

- **`load_workspace` design-time MSBuild warnings** — MSBuildWorkspace wraps ASP.NET/SDK deprecation (`IncludeOpenAPIAnalyzers` / ASPDEPR007), processor-architecture mismatch (MSB3270), and analyzer-project metadata refs as `Msbuild failed when processing the file` with `Failure` kind, often **without** a `warning XXXX` prefix. Those no longer fail load when projects opened; remapped to **Warning (MSBuild design-time)**. Wrapped messages without `error NU|MSB|NETSDK` or a known-hard inner text (`could not be loaded`, missing `Compile`, empty TFM, SDK not found) are warnings. Explicit errors and unloadable projects still fail.

### v1.0.33

- **`load_workspace` `targetFramework`** — Optional MSBuild `TargetFramework` global property (same idea as `dotnet build -f`). Needed when `Directory.Build.props` / csproj sets `TargetFrameworks`: the CrossTargeting outer evaluation has no `Compile` target and Roslyn cannot load. Dedicated failure **Workspace Load Failed (missing Compile target)** lists TFMs from the nearest props and tells the agent to retry with one inner TFM (e.g. `net10.0`). Not inherited by `run_dotnet_build`. `get_code_skeleton` / host Grep remain usable without a workspace.

### v1.0.32

- **`run_specific_test` slow-test duration** — VSTest console lines for long runs use `[1 s]` / `[1 m 28 s]`, not only `[12 ms]`. Parser now accepts those units so a passing filtered test is not reported as **no matching tests**.

### v1.0.31

- **`run_dotnet_test` / `run_specific_test` pre-test build** — When `noBuild=false`, compile with a separate incremental `dotnet build` (same `-c` / `-p:Platform`), then `dotnet test --no-build --no-restore`. VSTest summary is parsed from the test process only, so MSBuild warning dumps no longer produce **`Status: partial`**. Build failure/timeout is reported and tests are not started. `noBuild=true` still skips the extra compile. Shared `timeoutSeconds` covers both processes.

### v1.0.30

- **`apply_patch` replaceAll hang** — `replaceAll=true` no longer rescans the inserted `newString`. When `newString` contains `oldString` (typical rename `Foo` → `Ns.Foos`, production case `AllSoftNotificationHelper` → `HelperContainer.Eis.AllSoftNotificationHelpers`) the old `while (IndexOf)` loop grew the file forever and never returned — OpenCode showed a freeze with nothing useful in logs. Matching now advances past each insert (same as `string.Replace`); logs `ApplyPatch start/matched/wrote` with lengths, `newContainsOld`, replacement count, and elapsed ms.

### v1.0.29

- **`load_workspace` configuration / platform** — Optional MSBuild global properties (same names as the VS active solution config). Cached for `run_dotnet_build` / `run_dotnet_test` / `run_specific_test` when those tools omit `-c` / `-p:Platform`. Empty `TargetFramework` (`ResolvePackageAssets`) stays a load failure, with a dedicated report (retry with IDE config, or Bazel-generated csproj are not evaluable).

### v1.0.28

- **`run_dotnet_test` / `run_specific_test` .slnx summary** — VSTest/MSBuild console may omit `Passed:` when tests fail (and omit `Failed:` on success). Parser infers `Passed = Total − Failed − Skipped` so a fail-only `Total tests` block is not reported as **partial**.

### v1.0.27

- **`load_workspace` NU1701 / MSBuild wrapper** — Package TFM-compat restore warnings (`NU1701`, netfx assets in a netcore/net10 project) remapped to warnings — do not fail load when other projects opened. The word `failed` in Roslyn's `Msbuild failed when processing the file` wrapper is no longer treated as fatal by itself; explicit `error NU|MSB|NETSDK` and unloadable projects still fail load.

### v1.0.26

- **Agent diagnostics for workspace/test discovery** — `load_workspace` cancelled by the MCP host returns **Workspace Load Cancelled (client abort)** (not MSBuild failure) with OpenCode timeout guidance; `get_test_list` with `count: 0` explains wrong `.csproj` scope; `run_specific_test` no-match reports Roslyn vs `dotnet test` path mismatch and name-suffix fallback.

### v1.0.25

- **`run_dotnet_build` trust** — Default `noIncremental=true` (`--no-incremental`) so MSBuild up-to-date cache cannot report a fake success after edits; set `false` only for large monorepos that accept incremental risk. Effective exit uses the **last** `dotnet build` step (restore exit 0 cannot mask a failed build with no rebuild). Success/failure metadata includes `Configuration` and `NoIncremental`.

### v1.0.24

- **SDK env metadata** — `run_dotnet_build` / `run_dotnet_test` / `run_specific_test` report inherited `MSBuildSDKsPath`, strip vs `global.json` pin action, and SDK version inferred from MSBuild/NETSDK log paths

### v1.0.23

- **`dotnet` child env** — Without `global.json` pin, strip inherited `MSBuildSDKsPath` / `MSBUILD_EXE_PATH` / `DOTNET_MSBUILD_SDK_RESOLVER_*` (MSBuildLocator / IDE pollution) so `run_dotnet_build` / `run_dotnet_test` use the host SDK instead of e.g. 9.x → `NETSDK1045` on net10

### v1.0.22

- **`run_specific_test` method filter** — VSTest-safe filters: Roslyn method FQN without `()`; always `FullyQualifiedName~` (not `=`); no bogus leading `.` when `methodName` is already a dotted FQN; escape `\ ( ) & | = ! ~`

### v1.0.21

- **`load_workspace` soft prune** — Unused `PackageReference` / NuGet prune advisories (`will not be pruned`, prune package data) remapped to warnings — do not fail load when projects opened

### v1.0.20

- **`configuration`** — Optional `configuration` (`dotnet -c`) on `run_dotnet_build`, `run_dotnet_test`, `run_specific_test` for multi-config solutions (`Sit-Debug`, `Dit-Debug`, …)

### v1.0.19

- **`.slnx` support** — `load_workspace`, `run_dotnet_build`, `run_dotnet_test` / `run_specific_test`, discovery, and NuGet/format paths accept `.slnx`; prefer solution files for multi-config repos
- **OpenCode host timeout** — Document + `opencode.json.sample` / `install2opencode.ps1`: `"timeout": 600000` ms — avoids MCP `-32001` (~60s); separate from tool `timeoutSeconds`

### v1.0.18

- **Tool descriptions** — Accurate agent-facing `[Description]` across build/test/decompile/NuGet/navigation/AST params

### v1.0.17

- **`run_dotnet_test` / `run_specific_test`** — Optional `noBuild` / `noRestore` (`--no-build` / `--no-restore`); after build use `noBuild=true` for faster re-runs. Default `noBuild=false` compiles in a separate `dotnet build` then tests with `--no-build`.

### v1.0.16

- **`run_dotnet_test` / `run_specific_test`** — `timeoutSeconds` default **300**; kill process tree on timeout/cancel
- **`run_dotnet_build` probe** — Overall wall-clock budget (~300s) + per-step timeout; skip escalate when budget exhausted
- **`execute_dotnet_command`** — Same default timeout + kill on cancel
- **Silent fail UX** — Hints for zombie `dotnet` / locked `obj` when exit≠0 and no parsed diagnostics

### v1.0.15

- **`search_code`** — `caseSensitive` (default `false`); leftover branding → `caseSensitive=true`
- **No-workspace UX** — Semantic tools list candidate `.sln`/`.slnx` under `ROSLYN_MCP_WORKSPACE` / cwd (no auto-load)
- **`rename_project`** — SDK-style dir+csproj+ProjectReference+.sln/.slnx; `dryRun`; no namespace chain
- **Branding recipe** — Documented in `AGENTS.md.sample` (hybrid MCP + host edit)

### v1.0.14

- **`run_dotnet_run`** — SDK-pinned `dotnet run`, separate stdout/stderr, timeout, truncated output (stderr tail for progress)
- **`run_nuget_audit`** — Structured vulnerability table from `dotnet list package --vulnerable`
- **`get_changed_files`** — Git porcelain status + suggested test projects (no diff body)
- **`load_workspace`** — **Workspace health** block: SDK/global.json, restore assets, tool count
- **`execute_dotnet_command`** — SDK pinning + truncated stdout/stderr
- **`find_usages` / `find_symbol_references`** — **find_references** family; prefer `find_usages` when only `symbolName` is known
- **`get_project_graph` / `list_projects`** — Project dependency graph
- **`rename_symbol`** — `previewOnly=true` default workflow; **C# symbols only**
- **`run_format`** — `dotnet format` wrapper

### v1.0.13

- **`AssemblyReferenceResolver`** — Exact `{name}.dll`; deps.json + NuGet fallback
- **`DecompilerHost`** — NuGet / BCL / runtime pack resolver for ILSpy tools

### v1.0.10–v1.0.12

Build/test SDK pinning, VSTest parser, decompiler `assemblyPath`, `MCP_MSBUILD_SDK_MISMATCH` — see git history.

### Not implemented (see AGENTS.md.sample — Secrets)

- Read-only TFS / HTTP probe MCP tools
- MCP «secret configured: yes/no» without reading values
- Unified diff in `get_changed_files` (use host/shell `git diff` when allowed)

## Agent Initialization (How to force tool usage)

Even if the MCP is active, AI clients don't always load the tools into the current chat context. Put the **session policy** in the app repo so the agent actually uses Roslyn MCP.

**Canonical file:** [`AGENTS.md.sample`](AGENTS.md.sample) — copy into the **application** repository as `AGENTS.md` (or merge into `.cursor/rules`). It is policy only (when / MCP vs host / bans); per-tool parameters live in MCP tool Descriptions and in **Reference: MCP Tools** below.

**OpenCode:** `install2opencode.ps1` (see **Option C** above) writes or merges the sample into `AGENTS.md` automatically.

**Maintainers:** when tools or agent-visible behavior change, update **`AGENTS.md.sample` and this README** («Agent tools by version» + Reference) together. Do not paste the full AGENTS body into README — link the sample.

Policy summary (full text in the sample):

- Workspace is taken from `RoslynMcp.jsonc` (`workspace-path`) and loaded lazily — the first semantic call after start can take minutes; use `reload` after build / `.csproj` changes; prefer `.sln` / `.slnx`
- Missing `Compile` target on load → retry with `targetFramework` (inner TFM); not SDK mismatch
- VS 2026 BuildHost / `XMakeElements` → not SDK mismatch; need MCP 1.0.35+ or a single SDK-style `.csproj`
- C# identifiers → MCP first; plain text → host Grep; never shell `grep` / `dotnet build|test`
- Saved `.cs` (v1.1.0+) sync into symbol search automatically; unsaved editor buffers are ignored; `reload` only after build / generated `obj` / `.csproj` edits
- File read/write/edit → host tools (read/write/edit/bash); AST edits/insertions → MCP `add_member` / `update_method_body` / `organize_usings` / `implement_interface` / `extract_interface`
- Secrets: never paste PAT/passwords; app README must document run target / sample args

## Logs
- **Main log:** `logs/mcp-*.log` (relative to `AppContext.BaseDirectory`).
- Global incoming JSON-RPC logging is enabled by default.
- **Tool output:** logged as a one-line summary plus separate warning/error lines (not a duplicated full MCP response). Set `ROSLYN_MCP_LOG_TOOL_OUTPUT=full` to log entire tool responses at Information level.
- Environment Variables:
  - `ROSLYN_MCP_WORKSPACE` — repo root for MSBuild/SDK discovery at startup (see MCP config above).
  - `ROSLYN_MCP_LOG_TOOL_OUTPUT=full` — verbose tool response logging.
  - `MCP_LOG_INCOMING_RPC=0` (disable incoming RPC logging).
  - `MCP_LOG_INCOMING_RPC_MAX_CHARS=<N>` (limit payload log length, `0` = unlimited).

## Reference: MCP Tools

**Parameter Naming Rules:**
- `filePath` — a single `.cs` file (semantic/navigation/AST edit).
- `path` — a `.cs` file or directory for `get_code_skeleton` (absolute path; disk-based, no workspace required).
- `includeExtensions` — optional extension filter for `search_code` (`.cs` by default; `*` = all files).
- `caseSensitive` — optional for `search_code` (default `false`; use `true` for leftover branding checks).
- `workspacePath` — `.sln` / `.slnx` / `.csproj` (and sometimes a directory): `run_dotnet_test`, `run_specific_test`, `run_format`, `list_nuget_packages`, `run_nuget_audit`, `list_outdated_packages`, optional reload for `list_projects` / `get_project_graph`. **`run_dotnet_build` / `run_dotnet_run` accept only a `.csproj`, `.sln`, or `.slnx` file path, not a directory.** Prefer `.sln`/`.slnx` for multi-config solutions.
- `symbolName` — C# identifier for `find_symbol_definition`, `find_symbol_references`, `find_usages`, and `find_implementations` (exact name; matching is case-insensitive for definition/usages/implementations).
- `diagnosticId` — compiler/analyzer id from `get_diagnostics_for_file` (e.g. `CS0246`) for `get_code_fixes` / `apply_code_fix`.
- `fixIndex` — 0-based index from `get_code_fixes` for `apply_code_fix`.

When a tool accepts `filePath`, relative values are resolved against the loaded workspace root; if no workspace is loaded, the fallback is `Environment.CurrentDirectory`. The workspace is taken from the `RoslynMcp.jsonc` config (`workspace-path`) and loaded **lazily** — the first semantic call after server start can take minutes (set the host MCP `timeout` to ≥ 600000 ms).

There are **42** registered tools (see list below) and **1** MCP prompt (`RefactoringAssistantPrompt`).

### Workspace

<details>
<summary><code>reload</code> — Disposes and reloads the MSBuildWorkspace from the config or explicit arguments.</summary>

**Parameters:**
- `workspacePath: string?` — optional `.sln`, `.slnx`, or `.csproj` file (not a directory). Omit to use config `workspace-path`.
- `configuration: string?` — optional MSBuild `Configuration` global property (e.g. `Sit-Debug`). Omit to use config `configuration`.
- `platform: string?` — optional MSBuild `Platform` (`Any CPU` → `AnyCPU`). Omit to use config `platform`.
- `targetFramework: string?` — optional MSBuild `TargetFramework` (e.g. `net10.0`). Omit to use config `target-framework`.

**Behavior:** Path and MSBuild properties default to `RoslynMcp.jsonc`; arguments override the config. Call after `dotnet build` (generated `obj`), `.csproj`/`.sln`/`Directory.Build.props` edits, or project switching. Ordinary `.cs` saves do not need a reload (disk-sync). The first load of a large solution can take minutes — raise the host MCP timeout.
</details>

<details>
<summary><code>load_workspace</code> — Explicitly loads a <code>.sln</code>/<code>.slnx</code>/<code>.csproj</code> without restarting the MCP server.</summary>

**Parameters:**
- `workspacePath: string` — **required** absolute path to a `.sln`, `.slnx`, or `.csproj` file (not a directory). The config `workspace-path` is **not** substituted here (that is the role of `reload`).
- `configuration: string?` / `platform: string?` / `targetFramework: string?` — optional MSBuild global properties (omit for the SDK/solution default).

**Behavior:** Use when `workspace-path` is not set in `RoslynMcp.jsonc` (the configured workspace otherwise loads lazily and this tool is not needed), to load a different solution than the config, or to override `configuration`/`platform`/`target-framework`. Same path + properties returns the cache unless the project graph is stale; a different path or properties replaces the currently loaded workspace. Large solutions can take minutes — host timeout ≥ 600000. After a successful load, saved `.cs` sync from disk automatically; a changed `.csproj`/`.sln` needs `reload` (or `reset_workspace` + `load_workspace`).
</details>

<details>
<summary><code>reset_workspace</code> — Disposes the in-process MSBuildWorkspace and drops the cached solution.</summary>

**Parameters:** none.

**Behavior:** Frees memory / gives a clean state. Use while developing this server, or before switching solutions/parameters via `load_workspace`. Ordinary `.cs` edits and generated `obj` after build do **not** require reset — use `reload`. Does not restart the MCP process — use `stop_mcp_server` if the server binary itself was rebuilt.
</details>

### Semantics / Navigation

<details>
<summary><code>find_symbol_definition</code> — Semantic lookup: where a type or member is declared (FQN + file:line:col) in the loaded solution.</summary>

**Parameters:**
- `symbolName: string` — class, interface, struct, enum, or member identifier, or an exact FQN.
- `maxResults: int?`, `preview: bool?`

**Behavior:** A `symbolName` containing `.` is treated as an exact FQN (no fallback to the simple name); on no match the error lists the candidate FQNs. Returns the symbol display string, **fully-qualified name (FQN)**, and **1-based file:line:col**. FQN does not distinguish method overloads (all overloads with the same name in the same type are reported); to select one overload use 1-based `line`/`column` in `find_symbol_references` or `rename_symbol`. Do **not** answer “where is X **declared**?” with plain-text search or shell `grep`/`findstr`/`Select-String`. For arbitrary text search use your environment’s built-in **`grep`** tool.
</details>

<details>
<summary><code>find_usages</code> — Solution-wide references for a declared name: file, 1-based line:col (and optional line text).</summary>

**Parameters:**
- `symbolName: string` — declared name of the type or member, or an exact FQN.
- `maxResults: int?`, `preview: bool?`

**Behavior:** Applies saved `.cs` from disk first. Returns **1-based line:column** per reference, grouped by file. A `symbolName` containing `.` is an exact FQN (no fallback). If several declarations share a name, all are reported (a summary table groups references by FQN, with a `First` line:col column); narrow the name or pass an FQN to disambiguate. FQN does not distinguish method overloads (all overloads with the same name in the same type are reported); to select one overload use 1-based `line`/`column` in `find_symbol_references` or `rename_symbol`. By default only positions are returned (no line text); pass `preview=true` for the source line text.
</details>

<details>
<summary><code>find_symbol_references</code> — Finds usages of a class/interface/method when you know the declaring `.cs` file.</summary>

**Parameters:**
- `filePath: string`
- `symbolName: string?` — declaration name (class/interface/method/property/field/event/constructor); ignored when `line`/`column` are provided
- `line: int?` / `column: int?` — 1-based position on the declaration *or* a usage (both required together) — selects the exact symbol
- `maxResults: int?`, `preview: bool?`

**Behavior:** Without a position and with several same-named declarations in the file, returns an error listing the candidates (FQN + line:col) — no blind first match. Returns **1-based line:column** per reference, grouped by file.
</details>

<details>
<summary><code>find_implementations</code> — Find classes implementing an interface or derived from a base type.</summary>

**Parameters:**
- `symbolName: string` — interface or base class name or exact FQN (e.g. `IRepository`, `BaseController`)
- `transitive: bool = true` — when `true`, includes indirect implementations / derived types in the hierarchy
- `maxResults: int?`, `preview: bool?`

**Behavior:** For **interfaces** uses Roslyn `FindImplementationsAsync`; for **classes/structs** uses `FindDerivedClassesAsync`. Each result is reported as `path:line:col`. Do not use text search or `find_usages` for “who implements X?” / “what inherits from Y?”.
</details>

<details>
<summary><code>get_call_graph</code> — Callers and callees for a method (workspace; saved <code>.cs</code> applied first).</summary>

**Parameters:**
- `filePath: string` — `.cs` file containing the method (or the file with the `line`/`column` position)
- `className: string?` / `methodName: string?` — required together unless `line`/`column` are provided
- `line: int?` / `column: int?` — 1-based position on the method declaration or an invocation (both required together) — alternative to `className`+`methodName`
- `maxNodes: int = 25` — cap; when the total node count exceeds it, the full graph is written to a temp file
- `includeExternalCallees: bool = false` — include BCL / external calls

**Behavior:** Uses `SymbolFinder.FindCallersAsync` and invocation analysis. Use for bug investigation instead of loading many bodies via `get_method_body`.
</details>

<details>
<summary><code>get_diagnostics_for_file</code> — Returns Roslyn compiler diagnostics for a single file (Warning and Error only; saved <code>.cs</code> applied first).</summary>

**Parameters:**
- `filePath: string`

**Output:** each line includes severity, **line**, **column** (1-based), diagnostic id (e.g. `CS0246`, `IDE0001`), and message. Use these values with `get_code_fixes`.
</details>

<details>
<summary><code>get_code_fixes</code> — Lists Roslyn CodeAction fixes available for a diagnostic at a specific location.</summary>

**Parameters:**
- `filePath: string`
- `diagnosticId: string` — from `get_diagnostics_for_file` (e.g. `CS0246`)
- `line: int` — 1-based line where the diagnostic starts
- `column: int = 1` — 1-based column (from diagnostics output)

**Returns:** numbered fixes (`fixIndex` 0-based) with titles such as “Add using …”, “Implement interface”, etc. Up to 20 fixes per call.

**Workflow:** `get_diagnostics_for_file` → `get_code_fixes` → `apply_code_fix`. Do not invent fix code when Roslyn provides a fix.
</details>

<details>
<summary><code>apply_code_fix</code> — Applies a Roslyn CodeAction selected from <code>get_code_fixes</code>.</summary>

**Parameters:**
- `filePath: string`
- `diagnosticId: string`
- `fixIndex: int` — index from `get_code_fixes` (0-based)
- `line: int` — same as in `get_code_fixes`
- `column: int = 1`
- `previewOnly: bool = false` — when `true`, returns a diff preview without writing files

**Behavior:** writes changed files to disk and updates the in-memory workspace. Re-run `get_diagnostics_for_file` to verify remaining issues. `fixIndex` is valid only for the same `filePath` / `diagnosticId` / `line` / `column` pair in the same session.
</details>

<details>
<summary><code>rename_symbol</code> — Semantic C# symbol rename via Roslyn with preview capability.</summary>

**Parameters:**
- `filePath: string`
- `symbolName: string`
- `newName: string`
- `line: int?` / `column: int?` — 1-based position on the declaration or a usage (both required together) — selects the exact symbol
- `scope: string = "project"` — allowed: `project` | `solution`
- `previewOnly: bool = true`

**Behavior:** Without a position and with several same-named declarations in the file, returns an error listing the candidates (FQN + line:col) — no blind first match. C# symbols only (types/members/namespaces). For project folder / `.csproj` / solution graph use `rename_project`. For README/rules/URLs use host Grep/edit.
</details>

### Skeletons / Reading

<details>
<summary><code>get_class_skeleton</code> — Returns the C# file structure without method bodies (workspace index after applying saved <code>.cs</code>).</summary>

**Parameters:**
- `filePath: string`

Returns namespaces, types, properties, and method signatures (bodies omitted). For raw disk with no workspace (or to skip the index) use `get_code_skeleton`; for NuGet/DLL types use `get_decompiled_class_skeleton`.
</details>

<details>
<summary><code>get_code_skeleton</code> — Parses `.cs` from disk and returns a structural syntax skeleton with bodies stripped (no workspace required).</summary>

**Parameters:**
- `path: string` — absolute path to one `.cs` file or a folder to scan recursively (up to 20 files; skips `bin`/`obj`/`Test`/`Tests` path segments).

**Note:** This tool is file/folder-only. Do **not** pass `assemblyName` / `typeName`; for external/NuGet assemblies use `decompile_type` / `get_decompiled_class_skeleton`.
</details>

<details>
<summary><code>get_method_body</code> — Returns the source of a specific method within a named class (reads disk).</summary>

**Parameters:**
- `filePath: string`
- `className: string`
- `methodName: string`

**Path resolution:** `filePath` may be absolute or workspace-relative. First match wins (no overload selection) — use `update_method_body` with `parameterTypes` when overloads matter. Prefer over reading the whole file for large sources.
</details>

### Decompile (ILSpy)

<details>
<summary><code>explore_assembly</code> — Decompiles a referenced external assembly (NuGet/third-party DLL) via ILSpy and returns namespaces with visible top-level classes/interfaces.</summary>

**Parameters:**
- `assemblyName: string?` — simple name without `.dll` (resolved via workspace MetadataReferences → deps.json → NuGet).
- `assemblyPath: string?` — absolute path to a `.dll` (e.g. NuGet cache). Provide **one** of the two.
</details>

<details>
<summary><code>decompile_type</code> — Decompiles one specific type from a referenced assembly and returns C# source code (circuit breaker: max 500 lines).</summary>

**Parameters:**
- `assemblyName: string?` / `assemblyPath: string?` — same as `explore_assembly` (one required).
- `fullTypeName: string` (e.g. `Microsoft.AspNetCore.Mvc.ControllerBase`)

**Behavior note:** if decompiled output exceeds 500 lines, the tool returns an error instructing to use `get_decompiled_class_skeleton` and `get_decompiled_method_body`.
</details>

<details>
<summary><code>get_decompiled_class_skeleton</code> — Returns a signatures-only skeleton (public/protected fields/properties/methods) for one type from a referenced assembly.</summary>

**Parameters:**
- `assemblyName: string?` / `assemblyPath: string?` — same as `explore_assembly` (one required).
- `fullTypeName: string` (e.g. `Microsoft.AspNetCore.Mvc.ControllerBase`)
</details>

<details>
<summary><code>get_decompiled_method_body</code> — Decompiles only matching method overload(s) from one type in a referenced assembly.</summary>

**Parameters:**
- `assemblyName: string?` / `assemblyPath: string?` — same as `explore_assembly` (one required).
- `fullTypeName: string` (e.g. `Microsoft.AspNetCore.Mvc.ControllerBase`)
- `methodName: string`
</details>

### Editing (AST)

<details>
<summary><code>add_member</code> — Insert a method, property, or field into a class via Roslyn DocumentEditor.</summary>

**Parameters:**
- `filePath: string`
- `className: string` — top-level class name
- `memberSource: string` — full member declaration: method (modifiers, signature, body), property, or field

Parses the member declaration, inserts it after the last member of the class (inside that `#region`, when the last member is inside one) via Roslyn `DocumentEditor.AddMember`, formats the file. Only method / property / field are supported (no events, constructors, records, or nested types). Workspace is from the config (`workspace-path`), loaded lazily. Prefer over a host patch for new members.
</details>

<details>
<summary><code>update_method_body</code> — Replace a method body via Roslyn AST.</summary>

**Parameters:**
- `filePath: string` — absolute path to `.cs` file
- `className: string` — class containing the method (top-level or nested)
- `methodName: string`
- `newBody: string` — statements only, or a full `{ ... }` block
- `parameterTypes: string[]?` — e.g. `["string", "int"]` to disambiguate overloads; **required** when multiple overloads exist

**Behavior:** Finds `MethodDeclarationSyntax`, parses `newBody` into a `BlockSyntax`, replaces block or expression-bodied body, validates syntax errors before write, formats the file. Use with `get_method_body` — prefer over host edit for body-only edits.
</details>

<details>
<summary><code>organize_usings</code> — Sort and optionally remove unused usings.</summary>

**Parameters:** `filePath`, `removeUnused: bool = true`
</details>

<details>
<summary><code>implement_interface</code> — Add interface to class and generate NotImplemented stubs.</summary>

**Parameters:** `filePath`, `className`, `interfaceName`
</details>

<details>
<summary><code>extract_interface</code> — Extract a public interface from a class.</summary>

**Parameters:**
- `filePath: string`
- `className: string`
- `interfaceName: string?` — default `I` + className
- `createNewFile: bool = true` — write `{InterfaceName}.cs` in the same folder
- `previewOnly: bool = false`

**Behavior:** Collects public instance methods, properties, and events declared on the class; generates interface signatures; adds `: IInterface` to the class. Supports block and file-scoped namespaces. Partial classes are rejected.
</details>

### Build / Test

<details>
<summary><code>run_dotnet_build</code> — Runs dotnet build and returns a compact diagnostic summary.</summary>

**Parameters:**
- `workspacePath: string` — must be an existing **`.csproj`, `.sln`, or `.slnx` file** (not a directory).
- `configuration: string? = null` — optional `dotnet build -c` (e.g. `Sit-Debug`, `Dit-Debug`). Omit to inherit the config `configuration`.
- `noIncremental: bool = true` — pass `--no-incremental` on every build step (default). Set `false` only if you explicitly accept MSBuild up-to-date caching.
- `platform: string? = null` — optional `-p:Platform=` (e.g. `x64`). Omit to inherit the config `platform`.

**Behavior:** Pins SDK and runs a multi-step probe (minimal → restore escalate → normal/detailed when needed). Effective exit = last `dotnet build` step. Reports `MCP_MSBUILD_SDK_MISMATCH` on SDK conflicts. Use AFTER editing to verify compile.
</details>

<details>
<summary><code>run_dotnet_test</code> — Runs dotnet test with condensed failure details.</summary>

**Parameters:**
- `workspacePath: string`
- `timeoutSeconds: int = 300` — process timeout; `0` disables (not recommended). Raise for long integration tests.
- `noBuild: bool = false` — pass `--no-build` (after a successful `run_dotnet_build`).
- `noRestore: bool = false` — pass `--no-restore`.
- `configuration: string? = null` — optional `dotnet test -c` (e.g. `Sit-Debug`). Omit to inherit the config `configuration`.
- `platform: string? = null` — optional `-p:Platform=`. Omit to inherit the config `platform`.

**Behavior:** When `noBuild=false`, compiles with a separate `dotnet build` then runs `dotnet test --no-build --no-restore`. Returns a clean passed/failed summary; kills the process tree on timeout.
</details>

<details>
<summary><code>run_specific_test</code> — Run dotnet test filtered to one class and/or method.</summary>

**Parameters:**
- `workspacePath: string`
- `className: string?` — e.g. `UserServiceTests` (simple or fully qualified)
- `methodName: string?` — e.g. `CreateUser_WhenValid_ReturnsOk`
- `timeoutSeconds: int = 300` — same as `run_dotnet_test`.
- `noBuild: bool = false` — pass `--no-build`.
- `noRestore: bool = false` — pass `--no-restore`.
- `configuration: string? = null` — optional `dotnet test -c` (same as `run_dotnet_test`).

At least one of `className` or `methodName` is required. The tool builds a VSTest-safe `--filter` internally (`FullyQualifiedName~…`) — do not use a raw bash command or hand-written filters. When the workspace is loaded, Roslyn resolves the type/method FQN for precise filtering. Use this for TDD red/green loops.
</details>

<details>
<summary><code>run_dotnet_run</code> — Runs `dotnet run --project <csproj>` with pinned SDK.</summary>

**Parameters:**
- `workspacePath: string` — a `.csproj` (executable/worker project).
- `arguments: string?` — optional arguments after `--`.
- `workingDirectory: string?` — optional working directory override.
- `timeoutSeconds: int = 120` — `0` = no timeout.
- `maxStdoutChars: int = 8000`, `maxStderrChars: int = 2000`

Returns separate stdout/stderr with size limits (stderr tail for progress). Do not use a raw `dotnet run` shell command for project runs.
</details>

<details>
<summary><code>run_format</code> — Runs dotnet format (apply or verify-only).</summary>

**Parameters:**
- `workspacePath: string`
- `verifyOnly: bool = false`

Formatted `.cs` on disk are picked up by the next symbol search (disk-sync) automatically.
</details>

<details>
<summary><code>get_test_list</code> — List test methods in loaded solution (JSON; saved <code>.cs</code> applied first).</summary>

**Parameters:** `maxResults: int = 200`

Detects Fact/Theory/TestMethod/etc. Empty `count: 0` is an agent signal that the wrong `.csproj` may be loaded — reload the test `.sln`/`.slnx` via `reload` (or set `workspace-path`).
</details>

### Projects / NuGet

<details>
<summary><code>list_projects</code> — Shows projects in the workspace (Name, TFM, OutputType, Refs).</summary>

**Parameters:**
- `workspacePath: string? = null` — when provided, loads/reloads the workspace before listing.
</details>

<details>
<summary><code>get_project_graph</code> — Builds a project-to-project dependency graph.</summary>

**Parameters:**
- `workspacePath: string? = null` — when provided, loads/reloads the workspace before building the graph.
</details>

<details>
<summary><code>list_nuget_packages</code> — Installed NuGet packages as JSON per project.</summary>

**Parameters:**
- `workspacePath: string` — `.sln`, `.slnx`, `.csproj`, or directory
- `includeTransitive: bool` — default `true`
- `includeOutdated: bool` — default `false` (adds `--outdated`)
- `includeVulnerable: bool` — default `false` (adds `--vulnerable`)

**Model guidance:** inspect the dependency tree before editing `.csproj`; do not list packages via a raw bash `dotnet list` command.
</details>

<details>
<summary><code>list_outdated_packages</code> — Outdated NuGet packages as JSON.</summary>

**Parameters:** `workspacePath: string` — shortcut for `list_nuget_packages` with `includeOutdated=true`, no transitive.
</details>

<details>
<summary><code>search_nuget_registry</code> — Search NuGet feeds for package id and latest stable version.</summary>

**Parameters:**
- `query: string` — package id or search term
- `exactMatch: bool` — default `true` (exact id lookup)
- `maxResults: int` — when `exactMatch=false`, default 10

**Model guidance:** verify a package exists before adding it; never hallucinate package names or versions.
</details>

<details>
<summary><code>run_nuget_audit</code> — Runs `dotnet list package --vulnerable` and returns a compact vulnerability table.</summary>

**Parameters:**
- `workspacePath: string`
- `maxEntries: int = 40`
</details>

<details>
<summary><code>add_package_reference</code> — Add PackageReference to .csproj.</summary>

**Parameters:** `projectPath: string`, `packageId: string`, `version: string?`

Verify id/version with `search_nuget_registry` first. Clears the in-memory workspace — use `reload` after.
</details>

<details>
<summary><code>remove_package_reference</code> — Remove PackageReference from .csproj.</summary>

**Parameters:** `projectPath: string`, `packageId: string`

Clears the in-memory workspace — use `reload` after.
</details>

<details>
<summary><code>rename_project</code> — Rename SDK-style project directory + `.csproj` and fix the MSBuild graph.</summary>

**Parameters:**
- `projectPath: string` — path to `.csproj`
- `newProjectName: string` — single path segment (e.g. `DupFinder.Core`)
- `dryRun: bool = true`
- `searchRoot: string? = null` — where to find sibling projects / `.sln` / `.slnx`

**Behavior:** Moves the identically named project folder + renames `.csproj`; updates `AssemblyName`/`RootNamespace` only when they equal the old project name; rewrites `ProjectReference` paths; updates `.sln` and `.slnx` entries. **SDK-style only.** Does **not** rename C# namespaces/types, Docker, launchSettings, CI, or docs. After apply: `reload` → `run_dotnet_build`.
</details>

### Search / Miscellaneous

<details>
<summary><code>search_code</code> — Context-friendly ripgrep alternative (plain text or regex).</summary>

**Parameters:**
- `pattern: string`
- `directoryPath: string? = null`
- `includeExtensions: string? = ".cs"` — comma/semicolon list (`.cs,.csproj,.json`), or `*` for all files.
- `useRegex: bool = false`
- `caseSensitive: bool = false` — default case-insensitive; for leftover branding checks set `true`.
- `maxResults: int = 50`
- `maxScanSeconds: int = 20`

When the number of matches exceeds the cap, the full result (same markdown format) is written to a temp file and a short response (count + path + file summary) is returned — nothing is silently truncated.

**Agent note:** if your client exposes a built-in **`grep`** tool, prefer that for ad-hoc text search (never shell-driven grep). Use this MCP tool when you need search from within the Roslyn MCP process.
</details>

<details>
<summary><code>get_mcp_server_info</code> — Binary path, tool count, logs, loaded config path(s), workspace state.</summary>

**Parameters:** *(none)*

Use after `dotnet publish` to verify the MCP host picked up the new binary (expect **42** tools).
</details>

<details>
<summary><code>stop_mcp_server</code> — Stops this MCP host process after returning (for rebuilding the server binary; restart MCP in the IDE). Not for refreshing saved source — that syncs automatically.</summary>

**Parameters:** *(none)*
</details>

### Prompts

<details>
<summary><code>RefactoringAssistantPrompt</code> — Short system-style instructions for C# refactoring workflows (uses host read/write/edit/bash for file edits).</summary>

**Parameters:**
- `focus: string? = null`
</details>

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
---

## <a name="russian-version"></a>
 🇷🇺 Описание на русском

[🇬🇧 Back to English (Main)](#roslynmcpserver)

**Почему это лучше стандартных файловых MCP?**
В отличие от базовых серверов, которые умеют только читать и писать файлы, этот сервер использует **Roslyn**. Ваш ИИ-агент (в Cursor, Cline и др.) не просто читает текст, он видит C#-код глазами компилятора. Он может получать точечную диагностику без полной пересборки, **применять встроенные Roslyn code fixes** (add using, implement interface, опечатки), искать ссылки на символы и делать безопасный семантический рефакторинг, что кардинально снижает риск галлюцинаций.

Сервер работает по `stdio` и регистрирует инструменты через MCP C# SDK, фокусируясь на:
- работе с `*.sln`/`*.slnx`/`*.csproj` через Roslyn;
- безопасных операциях чтения/правки файлов через Roslyn;
- запуске `dotnet build` / `dotnet test`;
- компактных ответах для LLM и подробных логах.

## ⚠️ Предупреждение о безопасности
Этот сервер предоставляет ИИ-агенту права на чтение, запись и выполнение консольных команд (`dotnet`) в вашем рабочем пространстве. **Всегда используйте системы контроля версий (Git)**. Не запускайте сервер от имени администратора.

## Требования
- Строго **.NET 10 SDK**. *(Сборка более старыми версиями SDK не поддерживается и завершится ошибкой).*
- IDE с поддержкой MCP (Cursor, OpenCode, VS Code + Cline).

## Подключение к Cursor / VS Code (локально)

Рекомендуется запускать **собранный exe**. Это обеспечивает мгновенный старт без оверхеда от `dotnet run`. Проект настроен с флагом `ReadyToRun`, но **без** `PublishSingleFile` (чтобы Roslyn мог динамически загружать зависимости).

### 1. Собрать publish (один раз и после изменений)

```bash
dotnet publish RoslynMcpServer.csproj -c Release -r win-x64
```

Готовый exe:
- Windows x64: `bin/Release/net10.0/win-x64/publish/RoslynMcpServer.exe`
- Linux/macOS: тот же относительный путь под ваш `-r`.

### 2. Конфиг MCP

**Через интерфейс Cursor:**
1. Перейдите в **Cursor Settings** -> **Features** -> **MCP** -> **+ Add New MCP Server**.
2. Выберите тип: `stdio`.
3. Вставьте **абсолютный путь** к `RoslynMcpServer.exe`.

**Через файл конфига:**
Укажите абсолютный путь (примеры лежат в `mcp.json` и `.cursor/mcp.json`).
Опционально добавьте `env` с `ROSLYN_MCP_WORKSPACE` — **корень репозитория** (каталог с `global.json`), чтобы MSBuild.Locator и `run_dotnet_build` использовали тот же SDK, что и решение.

По умолчанию workspace берётся из конфига **`RoslynMcp.jsonc`** (мерж exe-каталога и cwd, cwd wins) и грузится **лениво** — первый семантический вызов после старта может занять минуты (`timeout` MCP-хоста ≥ 600000 мс). Подробности — в английской секции **Config** выше.

**OpenCode (`install2opencode.ps1`):**

После `dotnet publish` в каталоге publish лежат [`install2opencode.ps1`](install2opencode.ps1) и [`AGENTS.md.sample`](AGENTS.md.sample) рядом с `RoslynMcpServer.exe`.

Из корня **целевого проекта** (вашего приложения, не этого репозитория):

```powershell
cd D:\Devel\YourApp
& "D:\Devel\RoslynMcpServer\bin\Release\net10.0\win-x64\publish\install2opencode.ps1"
```

Скрипт:

- создаёт или обновляет **`opencode.json`**, регистрируя `RoslynMcpServer` как локальный MCP-сервер (`type: local`, `enabled: true`, **`timeout`: `600000`** мс = 10 минут);
- создаёт или дополняет **`AGENTS.md`**: если правил Roslyn MCP ещё нет, подмешивает текст из `AGENTS.md.sample` (новый файл или append к существующему).

**Таймаут MCP-хоста OpenCode:** по умолчанию OpenCode обрывает MCP `tools/call` примерно через **60 секунд**. Это **не** аргумент тула `timeoutSeconds` у `run_dotnet_test` / `run_dotnet_run`. Без увеличения host timeout длинные build/test и **первый ленивый load workspace** падают с `McpError: MCP error -32001: Request timed out` (~60 с), даже если передать `timeoutSeconds: 900`. Укажите `"timeout": 600000` (или больше) в записи сервера — см. [`opencode.json.sample`](opencode.json.sample). `install2opencode.ps1` проставляет это сам. Если старая сборка OpenCode игнорирует per-server `timeout`, добавьте также `"experimental": { "mcp_timeout": 600000 }`.

Пример блока сервера:

```json
"roslyn-mcp-server": {
  "type": "local",
  "command": ["C:/path/to/RoslynMcpServer.exe"],
  "timeout": 600000,
  "enabled": true
}
```

| Параметр | Описание |
| --- | --- |
| `-BinaryPath` | Необязательный абсолютный путь к `RoslynMcpServer.exe`. Если не указан — берётся бинарь рядом со скриптом. |
| `-ProjectPath` | Необязательный корень проекта. По умолчанию — текущая рабочая директория. |

После установки перезапустите OpenCode или перезагрузите MCP-серверы.

## История agent-tools по версиям

См. английский раздел [Agent tools by version](#agent-tools-by-version) (v1.0.13–v1.3.0). Правила агента — [`AGENTS.md.sample`](AGENTS.md.sample).

## Cursor: как заставить агента реально вызывать tools

Положите **session policy** в app-репозиторий, иначе клиент часто игнорирует MCP-тулы.

**Канонический файл:** [`AGENTS.md.sample`](AGENTS.md.sample) — скопируйте в приложение как `AGENTS.md` (или влейте во фрагменты `.cursor/rules`). Текст на EN (LLM лучше следуют английским императивам). Это только политика (когда / MCP vs host / запреты); параметры тулов — в Description и в **Reference: MCP Tools** ниже (и в англ. секции).

**OpenCode:** `install2opencode.ps1` (см. блок **OpenCode** выше) сам создаёт или дополняет `AGENTS.md`.

**Maintainers:** при изменении тулов или agent-visible поведения обновляйте **вместе** `AGENTS.md.sample` и этот README («Agent tools by version» + Reference). Полный текст AGENTS в README не дублировать — только ссылка на sample.

Кратко для модели:

- workspace — из `RoslynMcp.jsonc` (`workspace-path`), ленивая загрузка; первый вызов после старта — минуты; `reload` после build / `.csproj`
- нет target `Compile` при load — повторить с `targetFramework` (inner TFM); это не SDK mismatch
- VS 2026 BuildHost / `XMakeElements` — это не SDK mismatch; нужен MCP 1.0.35+ или один SDK-style `.csproj`
- C# объявления — MCP `find_symbol_definition` / `find_usages`, не текстовый поиск и не выдуманный `search`
- текст по файлам — host Grep IDE, не shell grep
- сборка/тесты — только MCP `run_dotnet_build` / `run_dotnet_test`
- чтение/запись файлов — host `read`/`write`/`edit`; правки AST (члены, тела методов, usings) — MCP `add_member` / `update_method_body` / `organize_usings` / `implement_interface` / `extract_interface`
- полный текст политики — в `AGENTS.md.sample`

## Логи
- Основной лог: `logs/mcp-*.log` (относительно `AppContext.BaseDirectory`).
- Включено логирование входящих JSON-RPC сообщений (`MCP_LOG_INCOMING_RPC`, `MCP_LOG_INCOMING_RPC_MAX_CHARS`).
- **Ответы tools:** в лог пишется однострочная сводка и отдельные строки warning/error (без полного дублирования ответа MCP). `ROSLYN_MCP_LOG_TOOL_OUTPUT=full` — полный текст ответов tools.
- Переменные: `ROSLYN_MCP_WORKSPACE` (корень репо для MSBuild/SDK), `ROSLYN_MCP_LOG_TOOL_OUTPUT=full`.

## Reference: MCP Tools

**Имена параметров в JSON:**
- `filePath` — один `.cs` файл (семантика/навигация/правки AST).
- `path` — файл `.cs` или каталог для `get_code_skeleton` (абсолютный путь; с диска, workspace не обязателен).
- `includeExtensions` — опциональный фильтр расширений для `search_code` (по умолчанию `.cs`; `*` = все файлы).
- `caseSensitive` — опционально для `search_code` (по умолчанию `false`; для leftover branding — `true`).
- `workspacePath` — `.sln` / `.slnx` / `.csproj` (и иногда каталог): `run_dotnet_test`, `run_specific_test`, `run_format`, `list_nuget_packages`, `run_nuget_audit`, `list_outdated_packages`, опциональная перезагрузка в `list_projects` / `get_project_graph`. **`run_dotnet_build` / `run_dotnet_run` принимают только путь к файлу `.csproj`, `.sln` или `.slnx`, не каталог.** Для multi-config solution предпочитайте `.sln`/`.slnx`.
- `symbolName` — идентификатор C# для `find_symbol_definition`, `find_symbol_references`, `find_usages` и `find_implementations` (точное имя; регистр не важен для definition/usages/implementations).
- `diagnosticId` — id компилятора/анализатора из `get_diagnostics_for_file` (например `CS0246`) для `get_code_fixes` / `apply_code_fix`.
- `fixIndex` — индекс (0-based) из `get_code_fixes` для `apply_code_fix`.

Когда tool принимает `filePath`, относительные значения резолвятся относительно корня загруженного workspace; если workspace не загружен — `Environment.CurrentDirectory`. Workspace берётся из конфига `RoslynMcp.jsonc` (`workspace-path`) и грузится **лениво** — первый семантический вызов после старта может занять минуты (поднимите host MCP `timeout` до ≥ 600000 мс).

Зарегистрировано **42** инструмента (список ниже) и **1** MCP-промпт (`RefactoringAssistantPrompt`).

### Workspace

<details>
<summary><code>reload</code> — Перезагружает MSBuildWorkspace из конфига или явных аргументов.</summary>

**Параметры:**
- `workspacePath: string?` — опционально `.sln`, `.slnx` или `.csproj` (не каталог). Если не указан — берётся из конфига `workspace-path`.
- `configuration: string?` — опционально MSBuild `Configuration` (например `Sit-Debug`). Если не указан — из конфига `configuration`.
- `platform: string?` — опционально MSBuild `Platform` (`Any CPU` → `AnyCPU`). Если не указан — из конфига `platform`.
- `targetFramework: string?` — опционально MSBuild `TargetFramework` (например `net10.0`). Если не указан — из конфига `target-framework`.

**Поведение:** путь и свойства MSBuild по умолчанию из `RoslynMcp.jsonc`; аргументы переопределяют конфиг. Вызывайте после `dotnet build` (generated `obj`), правок `.csproj`/`.sln`/`Directory.Build.props` или смены проекта. Обычные сохранения `.cs` не требуют reload (disk-sync). Первая загрузка большого решения может занять минуты — поднимите host MCP timeout.
</details>

<details>
<summary><code>load_workspace</code> — Явно загружает <code>.sln</code>/<code>.slnx</code>/<code>.csproj</code> без перезапуска MCP-сервера.</summary>

**Параметры:**
- `workspacePath: string` — **обязателен**: абсолютный путь к `.sln`, `.slnx` или `.csproj` (не каталог). Конфиг `workspace-path` здесь **не** подставляется (это роль `reload`).
- `configuration: string?` / `platform: string?` / `targetFramework: string?` — опциональные глобальные свойства MSBuild (не указывать — SDK/дефолт solution).

**Поведение:** используйте, если в `RoslynMcp.jsonc` не задан `workspace-path` (иначе конфигурационный workspace грузится лениво и этот тул не нужен), чтобы загрузить другое решение, чем в конфиге, или переопределить `configuration`/`platform`/`target-framework`. Тот же путь + свойства → кэш (если проект-граф не stale); другой путь или свойства подменяют текущий workspace. Крупные решения — минуты — host timeout ≥ 600000. После успешной загрузки сохранённые `.cs` синхронизируются с диска автоматически; изменённый `.csproj`/`.sln` требует `reload` (или `reset_workspace` + `load_workspace`).
</details>

<details>
<summary><code>reset_workspace</code> — Освобождает in-process MSBuildWorkspace и сбрасывает кэш solution.</summary>

**Параметры:** нет.

**Поведение:** освобождает память / даёт чистое состояние. Используйте при разработке самого сервера или перед сменой solution/параметров через `load_workspace`. Обычные правки `.cs` и generated `obj` после build reset **не** требуют — используйте `reload`. Не перезапускает процесс MCP — если пересобран сам бинарник сервера, используйте `stop_mcp_server`.
</details>

### Семантика / Навигация

<details>
<summary><code>find_symbol_definition</code> — Семантический поиск: где объявлен тип или член (FQN + file:line:col) в загруженном solution.</summary>

**Параметры:**
- `symbolName: string` — имя класса, интерфейса, struct, enum или члена, либо точный FQN.
- `maxResults: int?`, `preview: bool?`

**Поведение:** `symbolName` с `.` трактуется как точный FQN (без fallback на простое имя); при отсутствии совпадения ошибка перечисляет кандидатов FQN. Возвращает display-строку символа, **полное имя (FQN)** и **1-based file:line:col**. FQN не различает перегрузки метода (выдаются все перегрузки с одним именем в одном типе); чтобы выбрать одну — 1-based `line`/`column` в `find_symbol_references` или `rename_symbol`. Для «где **объявлен** X?» не используй текстовый поиск и не `grep`/`findstr`/`Select-String` из терминала. Для произвольного текста по файлам — встроенный **`grep`** среды.
</details>

<details>
<summary><code>find_usages</code> — Ссылки по всему solution: файл, 1-based line:col (и опционально текст строки).</summary>

**Параметры:**
- `symbolName: string` — объявленное имя типа или члена, либо точный FQN.
- `maxResults: int?`, `preview: bool?`

**Поведение:** сначала подмешиваются сохранённые `.cs` с диска. Возвращает **1-based line:column** для каждой ссылки, сгруппировано по файлам. `symbolName` с `.` — точный FQN (без fallback). Если несколько одноимённых символов — выводятся все (сводная таблица группирует ссылки по FQN, со столбцом `First` line:col); сузьте имя или передайте FQN. FQN не различает перегрузки метода (выдаются все перегрузки с одним именем в одном типе); чтобы выбрать одну — 1-based `line`/`column` в `find_symbol_references` или `rename_symbol`. По умолчанию только позиции; `preview=true` добавляет текст строки.
</details>

<details>
<summary><code>find_symbol_references</code> — Ищет использования класса/интерфейса/метода, когда известен файл объявления.</summary>

**Параметры:**
- `filePath: string`
- `symbolName: string?` — имя объявления (class/interface/method/property/field/event/constructor); игнорируется, если заданы `line`/`column`
- `line: int?` / `column: int?` — 1-based позиция на объявлении *или* на usage (вместе) — точный выбор символа
- `maxResults: int?`, `preview: bool?`

**Поведение:** без позиции и при нескольких одноимённых декларациях в файле — ошибка со списком кандидатов (FQN + line:col), «первый выиграл» не используется. Возвращает **1-based line:column** для каждой ссылки, сгруппировано по файлам.
</details>

<details>
<summary><code>find_implementations</code> — Классы, реализующие интерфейс, или наследники базового типа.</summary>

**Параметры:**
- `symbolName: string` — имя интерфейса или базового класса, либо точный FQN (например `IRepository`, `BaseController`)
- `transitive: bool = true` — при `true` включает косвенные реализации / наследников по иерархии
- `maxResults: int?`, `preview: bool?`

**Поведение:** для **интерфейсов** — Roslyn `FindImplementationsAsync`; для **классов/struct** — `FindDerivedClassesAsync`. Каждый результат — `path:line:col`. Не используйте текстовый поиск или `find_usages` для «кто реализует X?» / «кто наследует Y?».
</details>

<details>
<summary><code>get_call_graph</code> — Кто вызывает метод и что вызывает он (call graph; saved <code>.cs</code> подмешиваются).</summary>

**Параметры:** `filePath`, `className: string?` / `methodName: string?` (вместе, если не заданы `line`/`column`), `line: int?` / `column: int?` — 1-based позиция на объявлении метода или вызове (вместе) — альтернатива `className`+`methodName`, `maxNodes: int = 25` (при превышении полный граф пишется во временный файл), `includeExternalCallees: bool = false`

**Поведение:** через `SymbolFinder.FindCallersAsync` и анализ вызовов в теле. Для расследования багов — вместо массовой загрузки тел через `get_method_body`.
</details>

<details>
<summary><code>get_diagnostics_for_file</code> — Возвращает Roslyn-диагностику для одного C# файла (только Warning и Error; сначала saved <code>.cs</code>).</summary>

**Параметры:**
- `filePath: string`

**Вывод:** для каждой диагностики — severity, **строка**, **колонка** (1-based), id (например `CS0246`, `IDE0001`) и сообщение. Эти значения нужны для `get_code_fixes`.
</details>

<details>
<summary><code>get_code_fixes</code> — Список доступных Roslyn CodeAction для диагностики в указанной позиции.</summary>

**Параметры:**
- `filePath: string`
- `diagnosticId: string` — из `get_diagnostics_for_file` (например `CS0246`)
- `line: int` — номер строки (1-based), где начинается диагностика
- `column: int = 1` — колонка (1-based, из вывода diagnostics)

**Результат:** пронумерованные фиксы (`fixIndex`, с 0). Не более 20 фиксов за вызов.

**Workflow:** `get_diagnostics_for_file` → `get_code_fixes` → `apply_code_fix`. Не придумывайте правку вручную, если Roslyn уже предлагает fix.
</details>

<details>
<summary><code>apply_code_fix</code> — Применяет CodeAction, выбранный из <code>get_code_fixes</code>.</summary>

**Параметры:**
- `filePath: string`
- `diagnosticId: string`
- `fixIndex: int` — индекс из `get_code_fixes` (0-based)
- `line: int` — те же значения, что в `get_code_fixes`
- `column: int = 1`
- `previewOnly: bool = false` — при `true` только diff-превью без записи на диск

**Поведение:** записывает изменённые файлы на диск и обновляет in-memory workspace. `fixIndex` действителен только для той же пары `filePath` / `diagnosticId` / `line` / `column` в рамках сессии.
</details>

<details>
<summary><code>rename_symbol</code> — Семантический rename C# символа через Roslyn с предпросмотром.</summary>

**Параметры:**
- `filePath: string`
- `symbolName: string`
- `newName: string`
- `line: int?` / `column: int?` — 1-based позиция на объявлении или usage (вместе) — точный выбор символа
- `scope: string = "project"` — допустимо: `project` | `solution`
- `previewOnly: bool = true`

**Поведение:** без позиции и при нескольких одноимённых декларациях в файле — ошибка со списком кандидатов (FQN + line:col). Только C# символы (типы/члены/namespace). Для папки проекта / `.csproj` / графа solution — `rename_project`. Для README/rules/URL — host Grep/edit.
</details>

### Скелеты / Чтение

<details>
<summary><code>get_class_skeleton</code> — Возвращает структуру C# файла без тел методов (индекс workspace после saved <code>.cs</code>).</summary>

**Параметры:**
- `filePath: string`

Возвращает namespace, типы, свойства и сигнатуры методов (тела опущены). Для файла с диска без workspace (или чтобы не трогать индекс) — `get_code_skeleton`; для типов из NuGet/DLL — `get_decompiled_class_skeleton`.
</details>

<details>
<summary><code>get_code_skeleton</code> — Парсит `.cs` с диска и возвращает структурный скелет с вырезанными телами (workspace не требуется).</summary>

**Параметры:**
- `path: string` — абсолютный путь к одному файлу `.cs` или к папке для рекурсивного обхода (до 20 файлов; пропуск сегментов пути `bin`/`obj`/`Test`/`Tests`).

**Важно:** этот tool работает только с путём к файлу/папке. Не передавайте `assemblyName` / `typeName`; для внешних/NuGet-сборок используйте `decompile_type` / `get_decompiled_class_skeleton`.
</details>

<details>
<summary><code>get_method_body</code> — Возвращает код одного метода в именованном классе (чтение с диска).</summary>

**Параметры:**
- `filePath: string`
- `className: string`
- `methodName: string`

**Разрешение пути:** `filePath` может быть абсолютным или относительным к workspace. Берётся первый по совпадению (без выбора перегрузки) — для перегрузок используйте `update_method_body` с `parameterTypes`. Предпочтительнее чтения всего файла для крупных исходников.
</details>

### Decompile (ILSpy)

<details>
<summary><code>explore_assembly</code> — Декомпилирует подключенную внешнюю сборку (NuGet/сторонний DLL) через ILSpy и возвращает структуру namespaces с видимыми top-level class/interface.</summary>

**Параметры:**
- `assemblyName: string?` — имя без `.dll` (через workspace MetadataReferences → deps.json → NuGet).
- `assemblyPath: string?` — абсолютный путь к `.dll` (например кэш NuGet). Нужен **один** из параметров.
</details>

<details>
<summary><code>decompile_type</code> — Декомпилирует один конкретный тип из подключенной сборки и возвращает C# исходник (circuit breaker: максимум 500 строк).</summary>

**Параметры:**
- `assemblyName: string?` / `assemblyPath: string?` — как у `explore_assembly` (один обязателен).
- `fullTypeName: string` (например `Microsoft.AspNetCore.Mvc.ControllerBase`)

**Поведение:** если результат декомпиляции больше 500 строк, tool возвращает ошибку с рекомендацией использовать `get_decompiled_class_skeleton` и `get_decompiled_method_body`.
</details>

<details>
<summary><code>get_decompiled_class_skeleton</code> — Возвращает только сигнатуры (public/protected fields/properties/methods) для одного типа из подключенной сборки.</summary>

**Параметры:**
- `assemblyName: string?` / `assemblyPath: string?` — как у `explore_assembly` (один обязателен).
- `fullTypeName: string` (например `Microsoft.AspNetCore.Mvc.ControllerBase`)
</details>

<details>
<summary><code>get_decompiled_method_body</code> — Декомпилирует только нужный метод (все совпавшие перегрузки) из одного типа в подключенной сборке.</summary>

**Параметры:**
- `assemblyName: string?` / `assemblyPath: string?` — как у `explore_assembly` (один обязателен).
- `fullTypeName: string` (например `Microsoft.AspNetCore.Mvc.ControllerBase`)
- `methodName: string`
</details>

### Правки (AST)

<details>
<summary><code>add_member</code> — Вставить метод, property или field в класс через Roslyn DocumentEditor.</summary>

**Параметры:**
- `filePath: string`
- `className: string` — top-level класс
- `memberSource: string` — полное объявление члена: метод (модификаторы, сигнатура, тело), property или field

Парсит объявление, вставляет после последнего члена класса (в тот `#region`, если последний член в нём) через Roslyn `DocumentEditor.AddMember`, форматирует. Поддерживаются только method / property / field (не event, constructor, record, вложенные типы). Workspace — из конфига (`workspace-path`), ленивая загрузка. Для новых членов — вместо ручного host-патча.
</details>

<details>
<summary><code>update_method_body</code> — Заменить тело метода через Roslyn AST.</summary>

**Параметры:**
- `filePath: string` — абсолютный путь к `.cs`
- `className: string` — класс (top-level или вложенный)
- `methodName: string`
- `newBody: string` — только statements или блок `{ ... }`
- `parameterTypes: string[]?` — например `["string", "int"]` для перегрузок; **обязателен** при нескольких overload

**Поведение:** находит метод, парсит тело в `BlockSyntax`, заменяет block/expression body, проверяет syntax errors до записи, форматирует. Пара с `get_method_body` — вместо host-правки для правок только тела.
</details>

<details>
<summary><code>organize_usings</code> — Сортировка и удаление неиспользуемых using.</summary>

**Параметры:** `filePath`, `removeUnused: bool = true`
</details>

<details>
<summary><code>implement_interface</code> — Реализовать интерфейс (stubs NotImplemented).</summary>

**Параметры:** `filePath`, `className`, `interfaceName`
</details>

<details>
<summary><code>extract_interface</code> — Выделить public-интерфейс из класса.</summary>

**Параметры:**
- `filePath: string`
- `className: string`
- `interfaceName: string?` — по умолчанию `I` + className
- `createNewFile: bool = true` — записать `{InterfaceName}.cs` в ту же папку
- `previewOnly: bool = false`

**Поведение:** собирает public instance methods/properties/events класса, генерирует сигнатуры интерфейса, добавляет `: IInterface` к классу. Block/file-scoped namespace. Partial class не поддерживаются.
</details>

### Сборка / Тесты

<details>
<summary><code>run_dotnet_build</code> — Запускает dotnet build и возвращает компактную сводку.</summary>

**Параметры:**
- `workspacePath: string` — только существующий **файл** `.csproj`, `.sln` или `.slnx` (не каталог).
- `configuration: string? = null` — опционально `dotnet build -c` (например `Sit-Debug`, `Dit-Debug`). Если не задан — из конфига `configuration`.
- `noIncremental: bool = true` — `--no-incremental` на каждом build-шаге (по умолчанию). `false` только если явно принимаете up-to-date кэш MSBuild.
- `platform: string? = null` — опционально `-p:Platform=`. Если не задан — из конфига `platform`.

**Поведение:** pinning SDK + многошаговый probe (minimal → restore escalate → normal/detailed). **Итоговый exit** = последний `dotnet build`. При конфликте SDK — `MCP_MSBUILD_SDK_MISMATCH`. Используйте ПОСЛЕ правок для проверки компиляции.
</details>

<details>
<summary><code>run_dotnet_test</code> — Запускает dotnet test с сокращенным выводом ошибок.</summary>

**Параметры:**
- `workspacePath: string`
- `timeoutSeconds: int = 300` — таймаут процесса; `0` отключает (не рекомендуется). Для долгих интеграционных поднимайте.
- `noBuild: bool = false` — `--no-build` (после успешного `run_dotnet_build`).
- `noRestore: bool = false` — `--no-restore`.
- `configuration: string? = null` — опционально `dotnet test -c` (например `Sit-Debug`). Если не задан — из конфига `configuration`.
- `platform: string? = null` — опционально `-p:Platform=`. Если не задан — из конфига `platform`.

**Поведение:** при `noBuild=false` сначала отдельный `dotnet build`, затем `dotnet test --no-build --no-restore`. Возвращает чистую сводку passed/failed; убивает дерево процессов при timeout.
</details>

<details>
<summary><code>run_specific_test</code> — dotnet test с фильтром по классу и/или методу.</summary>

**Параметры:**
- `workspacePath: string`
- `className: string?` — например `UserServiceTests`
- `methodName: string?` — например `CreateUser_WhenValid_ReturnsOk`
- `timeoutSeconds: int = 300` — как у `run_dotnet_test`.
- `noBuild: bool = false` — `--no-build`.
- `noRestore: bool = false` — `--no-restore`.
- `configuration: string? = null` — опционально `dotnet test -c` (как у `run_dotnet_test`).

Нужен хотя бы один из `className` / `methodName`. Tool строит VSTest-safe `--filter` (`FullyQualifiedName~…`) — не используйте сырой bash-команду и не пишите фильтры вручную. Если workspace загружен, Roslyn резолвит FQN типа/метода. Для TDD red/green.
</details>

<details>
<summary><code>run_dotnet_run</code> — Запускает `dotnet run --project <csproj>` с pinning SDK.</summary>

**Параметры:**
- `workspacePath: string` — `.csproj` (executable/worker проект).
- `arguments: string?` — опциональные аргументы после `--`.
- `workingDirectory: string?` — опциональный override рабочего каталога.
- `timeoutSeconds: int = 120` — `0` = без таймаута.
- `maxStdoutChars: int = 8000`, `maxStderrChars: int = 2000`

Возвращает отдельно stdout/stderr с лимитами (stderr tail для прогресса). Не используйте сырой `dotnet run` из терминала для запуска проектов.
</details>

<details>
<summary><code>run_format</code> — Запускает dotnet format (применение или verify-only).</summary>

**Параметры:**
- `workspacePath: string`
- `verifyOnly: bool = false`

Отформатированные `.cs` на диске подхватывает следующий поиск символов (disk-sync) автоматически.
</details>

<details>
<summary><code>get_test_list</code> — Список тестов в solution (JSON). Сначала saved <code>.cs</code> с диска.</summary>

**Параметры:** `maxResults: int = 200`. Детектит Fact/Theory/TestMethod и т.д. При `count: 0` — сигнал, что загружен не тот `.csproj`: перезагрузите тестовый `.sln`/`.slnx` через `reload` (или задайте `workspace-path`).
</details>

### Проекты / NuGet

<details>
<summary><code>list_projects</code> — Показывает проекты текущего workspace (Name, TFM, OutputType, Refs).</summary>

**Параметры:**
- `workspacePath: string? = null` — если задан, workspace загружается/перезагружается перед выводом.
</details>

<details>
<summary><code>get_project_graph</code> — Строит граф зависимостей project-to-project.</summary>

**Параметры:**
- `workspacePath: string? = null` — если задан, workspace загружается/перезагружается перед построением графа.
</details>

<details>
<summary><code>list_nuget_packages</code> — Установленные NuGet-пакеты (JSON по проектам).</summary>

**Параметры:**
- `workspacePath: string` — `.sln`, `.slnx`, `.csproj` или каталог
- `includeTransitive: bool` — по умолчанию `true`
- `includeOutdated: bool` — по умолчанию `false` (`--outdated`)
- `includeVulnerable: bool` — по умолчанию `false` (`--vulnerable`)

**Для модели:** смотреть дерево зависимостей до правок `.csproj`; не выводите список пакетов через сырой bash `dotnet list`.
</details>

<details>
<summary><code>list_outdated_packages</code> — Устаревшие NuGet-пакеты (JSON).</summary>

**Параметры:** `workspacePath` — shortcut для `list_nuget_packages` с `includeOutdated=true`.
</details>

<details>
<summary><code>search_nuget_registry</code> — Поиск пакета и последней стабильной версии на NuGet.</summary>

**Параметры:**
- `query: string` — id или поисковый термин
- `exactMatch: bool` — по умолчанию `true`
- `maxResults: int` — при `exactMatch=false`, по умолчанию 10

**Для модели:** проверять id/версию перед добавлением пакета; не выдумывать названия пакетов.
</details>

<details>
<summary><code>run_nuget_audit</code> — Запускает `dotnet list package --vulnerable` и возвращает компактную таблицу уязвимостей.</summary>

**Параметры:**
- `workspacePath: string`
- `maxEntries: int = 40`
</details>

<details>
<summary><code>add_package_reference</code> — Добавить PackageReference в .csproj.</summary>

**Параметры:** `projectPath`, `packageId`, `version?`. Сначала `search_nuget_registry`. Сбрасывает in-memory workspace — после используйте `reload`.
</details>

<details>
<summary><code>remove_package_reference</code> — Удалить PackageReference из .csproj.</summary>

**Параметры:** `projectPath`, `packageId`. Сбрасывает in-memory workspace — после используйте `reload`.
</details>

<details>
<summary><code>rename_project</code> — Переименование SDK-style проекта (папка + `.csproj`) и правка MSBuild-графа.</summary>

**Параметры:**
- `projectPath: string` — путь к `.csproj`
- `newProjectName: string` — один сегмент пути (например `DupFinder.Core`)
- `dryRun: bool = true`
- `searchRoot: string? = null` — корень поиска соседних проектов / `.sln` / `.slnx`

**Поведение:** переносит одноимённую папку проекта и `.csproj`; обновляет `AssemblyName`/`RootNamespace` только если они совпадали со старым именем; чинит `ProjectReference`; обновляет `.sln` и `.slnx`. Только **SDK-style**. Не трогает C# namespace/типы, Docker, launchSettings, CI, docs. После apply: `reload` → `run_dotnet_build`.
</details>

### Поиск / Прочее

<details>
<summary><code>search_code</code> — Поиск совпадений по файлам (plain text или regex).</summary>

**Параметры:**
- `pattern: string`
- `directoryPath: string? = null`
- `includeExtensions: string? = ".cs"` — список через запятую/`;` (`.cs,.csproj,.json`) или `*` для всех файлов.
- `useRegex: bool = false`
- `caseSensitive: bool = false` — по умолчанию без учёта регистра; для leftover branding — `true`.
- `maxResults: int = 50`
- `maxScanSeconds: int = 20`

При превышении cap полный результат (тот же markdown) пишется во временный файл, возвращается короткий ответ (count + путь + сводка) — ничего молча не обрезается.

**Для агента:** если в клиенте есть встроенный **`grep`**, для обычного текстового поиска предпочитай его (не grep из терминала). Этот MCP-tool — когда нужен поиск из процесса Roslyn MCP.
</details>

<details>
<summary><code>get_mcp_server_info</code> — Путь к exe, число tools, логи, загруженные конфиги, состояние workspace.</summary>

**Параметры:** *(нет)*

После `dotnet publish` — проверка, что MCP подхватил новый бинарник (ожидай **42** tools).
</details>

<details>
<summary><code>stop_mcp_server</code> — Завершает процесс MCP после ответа (чтобы пересобрать бинарник сервера; затем перезапуск MCP в IDE). Не для refresh сохранённых исходников — они синхронизируются сами.</summary>

**Параметры:** *(нет)*
</details>

### Prompts

<details>
<summary><code>RefactoringAssistantPrompt</code> — Краткие инструкции для сценариев C#-рефакторинга (правки файлов — через host read/write/edit/bash).</summary>

**Параметры:**
- `focus: string? = null`
</details>

## Лицензия
Этот проект распространяется под лицензией MIT — подробности см. в файле [LICENSE](LICENSE).
