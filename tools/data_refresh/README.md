# STS2 data refresh (offline tool)

Standalone CLI for refreshing and enriching **local** metadata for the Slay the Spire 2 mod project. This tool is **not** part of the runtime mod: the game mod should keep reading static JSON only and must not perform network I/O.

## Keyword glossary (`keywords.json`)

The mod’s LLM reads `Data/keywords.json` (ship with the mod). It is filled **mechanically** from wiki.gg (no LLM): detail pages plus Debuffs/Buffs table **Description** fallbacks.

It runs automatically as part of:

- `python main.py parse` — updates `Data/keywords.json`, `output/keywords.generated.json`, and adds `keywords_glossary` to `parsed_raw.json` / `metadata_summary.json`
- `python main.py refresh` — same, plus `output/keywords.diff.json` vs pre-run `Data/keywords.json`, and a **Keywords glossary** section in `refresh_report.md`

Standalone:

```bash
python main.py keywords-refresh
```

Sources: `wiki_keyword_index_pages` + `wiki_keyword_pages` in `config.yaml` (defaults: Debuffs, Buffs, Orbs).

## What it does

1. **Fetch** wiki.gg pages and Steam patch feeds (with disk cache and TTL).
2. **Parse** HTML/RSS into intermediate records (cards, relics, acts, events, encounters, monsters, patch notes).
3. **Merge** conservatively with existing production JSON (`Data/cards.json`, `Data/relics.json` by default).
4. **Optionally** call an LLM (OpenAI-compatible API) to propose tags — disabled when no API key is set.
5. **Write** generated artifacts under `output/` plus `refresh_report.md`, diffs, and `review_queue.json`.
6. **Review / apply / backup / rollback** without silently overwriting curated data.

LLM tag proposals are constrained to the coach-supported vocabulary in `SUPPORTED_TAGS.md`.

## Install

**Option A — minimal venv (data_refresh only):**

```bash
cd tools/data_refresh
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
```

**Option B — repo conda env:** from the **repo root**, follow **`README.md` → Maintainer Notes → Python environment**, or run `powershell -ExecutionPolicy Bypass -File .\tools\dev\setup-conda-env.ps1`, then `conda activate sts2-context-coach` and run `python tools/data_refresh/main.py ...` from the repo root (or `cd tools/data_refresh` first).

## Configure

1. Copy `config.example.yaml` to `config.yaml` beside `main.py`.
2. Default wiki URLs point at **slaythespire.wiki.gg** (e.g. `Slay_the_Spire_2:Cards_List`). Override in `config.yaml` if paths change.
3. Set **Steam** `steam_news_rss` / app id URLs to the real game AppID when known.
4. **LLM**: set `llm.enabled: true` and export the API key referenced by `api_key_env`. If unset, enrichment is skipped safely.

### OpenRouter

OpenRouter uses the same **Chat Completions** API shape as OpenAI.

1. Copy `config.example.yaml` to `config.yaml` (or merge the `llm` block).
2. Set `provider: openrouter`, `base_url: "https://openrouter.ai/api/v1"`, `api_key_env: OPENROUTER_API_KEY`.
3. Set `model` to an OpenRouter id (e.g. `openai/gpt-4o-mini` — see [OpenRouter models](https://openrouter.ai/models)).
4. Optional: keep `extra_headers` with `HTTP-Referer` and `X-Title` (recommended by OpenRouter for attribution).
5. In PowerShell: `$env:OPENROUTER_API_KEY = "sk-or-v1-..."` then run `python main.py refresh` (or `enrich` after `parse`).

**Never commit API keys.** Prefer environment variables only. If a key was pasted into chat or checked into git, **revoke it in the OpenRouter dashboard and create a new one**.

Environment overrides:

| Variable | Effect |
|----------|--------|
| `STS2_DATA_DIR` | Override directory containing `cards.json` / `relics.json` |
| `STS2_REFRESH_MERGE_MODE` | `safe` (default), `suggest`, or `overwrite` |
| `STS2_REFRESH_LLM_KEY` | Shortcut to inject API key into `api_key_env` |

## Commands

| Command | Purpose |
|---------|---------|
| `python main.py keywords-refresh` | Only mechanical keyword glossary → `Data/keywords.json` (also runs inside `parse` / `refresh`) |
| `python main.py fetch` | Download sources into `cache/` |
| `python main.py parse` | Parse wiki pages to `output/parsed_raw.json` (cards/relics/world entities) |
| `python main.py enrich` | LLM proposals → `output/llm_proposals.json` (needs `parsed_raw.json`) |
| `python main.py heuristics-analyze --logs-dir logs` | Build LLM heuristic-tuning proposals from telemetry + scoring rules |
| `python main.py heuristics-list` | List heuristic proposals + review status |
| `python main.py heuristics-set --id <proposal_id> --status approved` | Approve/reject one heuristic proposal |
| `python main.py diff` | Deep diffs: production vs `*.generated.json` |
| `python main.py validate` | Validate generated JSON |
| `python main.py refresh --safe` | Full pipeline → `output/` + `refresh_report.md` + `review_queue.json` |
| `python main.py review list` | List pending review items |
| `python main.py review approve --type card --id Bash` | Mark queue item approved |
| `python main.py review reject --type relic --id Vajra` | Mark rejected |
| `python main.py review note --type card --id Bash --message "..."` | Append note |
| `python main.py apply-approved` | Apply **approved** items to production JSON (backup first) |
| `python main.py backup` | Timestamped backup under `backups/` |
| `python main.py rollback --to <folder>` | Restore a backup folder name |
| `python main.py backups-list` | List backups |

### Promotion helper

Use `promote.ps1` to run a safe promotion flow:

1. validate generated artifacts
2. apply approved review items (creates backup)
3. sync `Data/*.json` into `bin/Release/net9.0/Data/*.json`

Examples:

- Dry run (no apply):  
  `powershell -ExecutionPolicy Bypass -File tools/data_refresh/promote.ps1 -ConfigPath tools/data_refresh/config.yaml -WhatIf`
- Full promote:  
  `powershell -ExecutionPolicy Bypass -File tools/data_refresh/promote.ps1 -ConfigPath tools/data_refresh/config.yaml`
- Skip release copy:  
  `powershell -ExecutionPolicy Bypass -File tools/data_refresh/promote.ps1 -ConfigPath tools/data_refresh/config.yaml -SkipReleaseCopy`

## Generated vs production

| Location | Role |
|----------|------|
| `Data/cards.json`, `Data/relics.json` | **Production** — what the mod loads |
| `tools/data_refresh/output/cards.generated.json` | Proposed full snapshot (includes `_meta`) |
| `tools/data_refresh/output/relics.generated.json` | Proposed relics |
| `tools/data_refresh/output/patch_notes.generated.json` | Parsed patch entries |
| `tools/data_refresh/output/world.generated.json` | Parsed acts/events/encounters/monsters (offline-only world metadata) |
| `tools/data_refresh/output/metadata_summary.json` | Top-level coverage summary (counts + by-act coverage + skill coverage) |
| `tools/data_refresh/output/*.diff.json` | DeepDiff vs production |
| `tools/data_refresh/output/refresh_report.md` | Human-readable run summary |
| `tools/data_refresh/output/review_queue.json` | Items needing or receiving review |
| `tools/data_refresh/cache/` | Raw HTML/RSS cache |
| `tools/data_refresh/backups/` | Copies taken before `apply-approved` |

## Merge modes

- **safe** (default): trusted wiki objective fields apply when there is no conflict; conflicts are queued. LLM fields are **never** auto-applied — they appear in the review queue only.
- **suggest**: same conservative behavior for wiki; LLM still queued (alias for safe in this MVP for LLM).
- **overwrite**: wiki conflicts are written into the **generated** JSON while still recording review rows for visibility. Production files are **never** written until `apply-approved`.

## Supported tags

Exact supported card/relic tag vocabulary is documented in `SUPPORTED_TAGS.md` and enforced by `tag_vocabulary.py` during LLM enrichment normalization.

## Safety

- `refresh` does **not** modify production JSON.
- `apply-approved` writes production only after explicit approvals; it creates a backup first.
- Manual fields can be listed per-entity under `_meta.manual_override_fields` (future use); merge already respects `manual_override_fields` when present.
- `heuristics-analyze` only writes review artifacts (`output/heuristic_proposals.json`, `output/heuristic_proposals.md`) and a helper script (`tools/data_refresh/review_heuristic_proposals.ps1`); it does not change runtime scoring code. It calls the LLM whenever the key referenced by `llm.api_key_env` is set, **even if** `llm.enabled` is false (so you can keep card/tag `enrich` off while still reviewing heuristics).

## Rollback

After a bad apply, use `python main.py rollback --to <timestamp_folder>` with a name from `backups-list`.
