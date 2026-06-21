# Agent context

Read **`AGENTS.md`** and **`docs/REPO_MEMORY_BANK.md`** first. This file mirrors the code-review-graph MCP section from **`AGENTS.md`** for Gemini-specific sessions.

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
