# Agent guide — STS2 Context Coach

Context-aware card recommendation overlay for **Slay the Spire 2** (C# Harmony mod + optional Python maintainer tooling).

**Repository:** https://github.com/PPPSDavid/sts2-context-coach

## Start here

1. **`docs/REPO_MEMORY_BANK.md`** — goals, architecture map, diagnostics paths, changelog.
2. **`README.md`** — player install; Maintainer Notes for build, conda, MCP, release.
3. **`.cursor/rules/*.mdc`** — always-on validation and domain rules (loaded automatically in Cursor).

## Project goal

Ship a **stable in-game overlay** (Base + Ctx scores, signed reasons) on reward/shop picks. Heuristic scoring is the default; LLM is optional at runtime and in maintainer pipelines. The mod must never require network or LLM to play.

## Repo map (quick)

| Path | Purpose |
|------|---------|
| `ModMain.cs`, `UI/`, `Scoring/`, `State/` | Runtime mod |
| `Llm/`, `Telemetry/` | Optional LLM + run logging |
| `Data/`, `Localization/` | Embedded metadata + strings |
| `Sts2ContextCoach.Tests/` | xUnit (headless, no Godot) |
| `tools/data_refresh/` | Metadata refresh CLI |
| `tools/run_insights/` | Telemetry aggregation CLI |
| `tools/dev/` | Conda/MCP/crg helpers |

## First-time maintainer setup

| Step | Command / action |
|------|------------------|
| Game refs | `.\setup-lib.ps1 -GamePath "C:\Path\To\Slay the Spire 2"` → populates **`lib/`** |
| Optional deploy | Copy **`local.props.example`** → **`local.props`**, set **`STS2GamePath`** |
| .NET 9 | Install SDK; `dotnet build` / `dotnet test` (see validation rule) |
| Python (optional) | `conda env create -f environment.yml` then **`tools/dev/setup-conda-env.ps1`** |
| Code graph MCP | Enable **`code-review-graph`** in Cursor; run **`install_crg_st_cache_patch.ps1`** once after pip install |

## Validation (run before claiming done)

```powershell
dotnet build .\Sts2ContextCoach.csproj -c Release
dotnet test .\Sts2ContextCoach.Tests\Sts2ContextCoach.Tests.csproj -c Release
```

Python (when touching `tools/`): `ruff format .`, `ruff check .`, unittest under `tools/*/tests/`.

## Safety rails

- Runtime works with **LLM off**; no required network I/O in the mod.
- Do **not** commit **`local.props`**, **`tools/data_refresh/config.yaml`**, or API keys.
- **`data_refresh`** uses review/apply — never silently overwrite curated **`Data/*.json`**.
- Production metadata is **embedded in the DLL**; avoid loose `*.json` manifests under mod folders.

## Runtime debugging

- Godot: `%AppData%\Roaming\SlayTheSpire2\logs\godot.log`
- Mod data/logs: `%AppData%\Roaming\SlayTheSpire2\Sts2ContextCoach\`
- Correlate `[ContextCoach]` / `[ContextCoach][LLM]` with `events.jsonl` and `logs/llm/coach-*.json`

## Known gaps / not yet modeled

- **`tools/run_insights`**: shop purchases and discards not fully modeled in telemetry (see output `notes`).
- **In-agent .NET**: CI/dev machines need .NET 9 SDK + populated **`lib/`**; agent sandboxes may lack both.
- **Code graph index**: `.code-review-graph/` is local (gitignored); first MCP use may need `embed_graph` / index build per upstream CRG docs.

---

<!-- code-review-graph MCP tools -->
## MCP Tools: code-review-graph

**IMPORTANT: This project has a knowledge graph. ALWAYS use the
code-review-graph MCP tools BEFORE using Grep/Glob/Read to explore
the codebase.** The graph is faster, cheaper (fewer tokens), and gives
you structural context (callers, dependents, test coverage) that file
scanning cannot.

### When to use graph tools FIRST

- **Exploring code**: `semantic_search_nodes` or `query_graph` instead of Grep
- **Understanding impact**: `get_impact_radius` instead of manually tracing imports
- **Code review**: `detect_changes` + `get_review_context` instead of reading entire files
- **Finding relationships**: `query_graph` with callers_of/callees_of/imports_of/tests_for
- **Architecture questions**: `get_architecture_overview` + `list_communities`

Fall back to Grep/Glob/Read **only** when the graph doesn't cover what you need.

### Cursor / Agent wiring

- **Project default:** `.cursor/mcp.json` is committed so Cursor offers the **`code-review-graph`** server whenever this folder is the workspace root. **Windows:** **`crg_mcp_serve.ps1`** (finds **`Scripts\conda.exe`**). **Linux / macOS:** use **`bash`** + **`tools/dev/crg_mcp_serve.sh`** (see **README** JSON snippet—the PowerShell script is not suitable there). Leave the server **enabled** under **Settings → Features → Model Context Protocol** (servers load by default until you toggle them off).
- **Tool names:** Cursor often exposes graph tools with a **`_tool` suffix** and a **`project-*-` name prefix**. Use the MCP tools as listed in your session; they map to the logical names in the table below (for example `detect_changes` ↔ `detect_changes_tool`).

### Key Tools

| Tool | Use when |
|------|----------|
| `detect_changes` | Reviewing code changes — gives risk-scored analysis |
| `get_review_context` | Need source snippets for review — token-efficient |
| `get_impact_radius` | Understanding blast radius of a change |
| `get_affected_flows` | Finding which execution paths are impacted |
| `query_graph` | Tracing callers, callees, imports, tests, dependencies |
| `semantic_search_nodes` | Finding functions/classes by name or keyword |
| `get_architecture_overview` | Understanding high-level codebase structure |
| `refactor_tool` | Planning renames, finding dead code |

### Workflow

1. The graph auto-updates on file changes (via hooks).
2. Use `detect_changes` for code review.
3. Use `get_affected_flows` to understand impact.
4. Use `query_graph` pattern="tests_for" to check coverage.
