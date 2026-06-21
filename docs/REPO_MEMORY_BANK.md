# STS2 Context Coach - Repo Memory Bank

## Project goal

`STS2 Context Coach` is a Slay the Spire 2 mod that overlays context-aware card recommendations on reward and shop choices.  
It combines static heuristic scoring with optional LLM-assisted analysis, while keeping runtime gameplay stable even when LLM features are off.

**GitHub:** https://github.com/PPPSDavid/sts2-context-coach

## Primary usage modes

- **Player/runtime mode (default):** install release zip, run in-game overlays with local data.
- **Maintainer/data mode:** refresh card/relic/keyword metadata using `tools/data_refresh` (offline CLI, optional LLM proposals).
- **Maintainer/tuning mode:** use telemetry and heuristic proposal workflows to iterate scoring quality.

## High-level architecture

- `ModMain.cs`
  - Mod entrypoint and initialization.
  - Loads config/localization/data, applies Harmony patches, and logs startup mode.
- `UI/`
  - Overlay rendering and settings UI.
  - `CardOverlayPatch.cs` is the core runtime UI patch for reward/shop overlays.
  - `LlmSettingsPanel.cs` exposes in-game config controls.
- `Scoring/`
  - Core recommendation engine and deck-analysis logic.
  - `RecommendationEngine.cs`, `DeckAnalyzer.cs`, and scoring result models.
- `State/`
  - Game-state extraction/probes/caching from runtime objects.
  - Includes reward/shop detection and economy/combat context.
- `Llm/`
  - Batch coordination, transcript logging, and model payload structures.
  - Optional path layered on top of heuristic scoring.
  - System prompt now enforces response language from runtime localization (e.g., `zh-CN` -> Simplified Chinese notes) and discourages unsupported deck-strategy claims.
  - Added periodic deck-profile summarization (signature/floor-triggered) and injects compact `deck_profile` lines into pick payloads so card-specific deck interactions remain explicit as decks grow.
- `Telemetry/`
  - Runtime config and structured run logging.
  - Useful for diagnostics, tuning, and post-run analysis.
  - `events.jsonl` may include `llm_coach_batch` / `llm_deck_summary` rows (corr id, batch key or deck signature, transcript basename, outcome) to correlate with Godot `[ContextCoach][LLM]` lines and `logs/llm/coach-*.json`.
  - Card reward / shop `decision` events record **full-row** `engine_scores` when the overlay has a coach row (or reward-card heuristics when not).
- `Data/`
  - Shipped metadata and loaders (`cards.json`, `relics.json`, `keywords.json`).
  - Includes glossary/repository classes and normalization helpers.
- `Localization/`
  - Embedded translations (`en.json`, `zh-CN.json`).
- `tools/data_refresh/`
  - Python pipeline for fetching/parsing/merging/reviewing metadata updates.
  - Not required for normal gameplay users.
- `tools/run_insights/`
  - Python CLI (`python -m tools.run_insights`, `PYTHONPATH=.`) reads `events.jsonl` or export ZIPs and writes **staging** pick-aggregate JSON (does not rewrite `Data/cards.json`).
  - Unit tests: `python -m unittest discover -s tools/run_insights/tests -p "test_*.py"`.
- `Sts2ContextCoach.Tests/`
  - xUnit regression tests for pure, runtime-safe scoring and data normalization behavior.
  - References the mod project while staying outside runtime packaging/deploy artifacts.

## Runtime data and packaging facts

- Production mod loads from shipped metadata (`Data/*.json`) embedded in the assembly to avoid STS2 scanning extra JSON manifests under mod folders.
- Runtime still ships key root files in mod directory: `Sts2ContextCoach.dll`, `Sts2ContextCoach.json`, `contextcoach.config`, `result_cleaned.csv`.
- LLM is optional at runtime (`scoring_mode`, API-key settings in `contextcoach.config`).
- Optional `llm_mirror_transcripts_into_run_folder` copies each `coach-*.json` transcript into the active run’s `llm/` folder so **Export & Share** ZIPs can include them alongside `events.jsonl`.

## Build and deploy workflow

- Project is C# on `net9.0` (`Sts2ContextCoach.csproj`).
- Depends on game/modding libs in `lib/` (`sts2.dll`, `GodotSharp.dll`, `0Harmony.dll`, `BaseLib.dll`).
- Typical commands:
  - `dotnet build .\Sts2ContextCoach.csproj -c Release`
  - `dotnet test .\Sts2ContextCoach.Tests\Sts2ContextCoach.Tests.csproj -c Release`
  - `powershell -ExecutionPolicy Bypass -File .\tools\release\build-release.ps1`
- Optional auto-deploy to local game mods directory is configured via `local.props` + project targets.

## Operational diagnostics (important)

- For in-game behavior debugging, check STS2 Godot logs early:
  - `%AppData%\Roaming\SlayTheSpire2\logs\godot.log`
- Context Coach writable/log root:
  - `%AppData%\Roaming\SlayTheSpire2\Sts2ContextCoach\`
- Correlate `[ContextCoach]` and `[ContextCoach][LLM]` log lines with telemetry/transcripts before concluding code regressions.

## Safety / conventions to preserve

- Keep runtime mod network behavior optional and resilient; base gameplay should work without LLM.
- Do not commit secrets (`config.yaml`, API keys).
- Data refresh pipeline should not silently overwrite curated production JSON.
- Preserve localization and player-facing stability when touching UI/scoring/state extraction.

## Optional maintainer tooling: code-review-graph

- **Python env:** [README](../README.md) Maintainer Notes + [root `environment.yml`](../environment.yml); helper scripts [`tools/dev/setup-conda-env.ps1`](../tools/dev/setup-conda-env.ps1), [`tools/dev/recreate-conda-env.ps1`](../tools/dev/recreate-conda-env.ps1). Example Cursor MCP config: [`.cursor/mcp.json`](../.cursor/mcp.json) (paths are machine-specific—adjust `command` to your env’s `python.exe`).
- **Graph + MCP:** install per upstream `code-review-graph` docs; index under `.code-review-graph/` (gitignored). Excludes: [`.code-review-graphignore`](../.code-review-graphignore).
- **Optional local embeddings:** `pip install "code-review-graph[embeddings]"` plus compatible `transformers` / `sentence-transformers`; run `embed_graph` / MCP `embed_graph_tool`. Keep `CRG_EMBEDDING_MODEL` aligned with the model used to build vectors (see `.cursor/mcp.json` `env` if you use the sample config).
- **Semantic search perf / GPU meter:** upstream reloads **`SentenceTransformer` per search**; this repo adds an optional **process-level model cache** ([`tools/dev/crg_st_model_cache.py`](../tools/dev/crg_st_model_cache.py) + [`install_crg_st_cache_patch.ps1`](../tools/dev/install_crg_st_cache_patch.ps1), `.pth` in site-packages) gated by **`CRG_APPLY_ST_CACHE_PATCH=1`** in MCP `env`. Hybrid retrieval still spends most time on **CPU** (SQLite + Python cosine over all vectors), so **low sustained GPU %** is normal—the GPU only spikes during short query **`encode()`** calls.

## Where to start when resuming work

1. Read this file and `.cursor/rules/*.mdc`.
2. Read `README.md` for user-facing install/features.
3. Check active branch changes before editing (repo is often intentionally dirty).
4. For bug reports involving runtime behavior, inspect Godot logs + Context Coach logs first.

## Memory bank maintenance protocol

- Update this file whenever repo-level architecture, goals, usage, workflows, or diagnostics paths materially change.
- Keep edits concise and operational (what changed, where, and why it matters).
- Do not churn wording when there is no meaningful change.
- Add a changelog entry for every substantive memory update.

## Changelog

- 2026-04-11: LLM observability + telemetry: `events.jsonl` gains `llm_coach_batch` / `llm_deck_summary`; reward/shop decisions log full-row `engine_scores`; optional `llm_mirror_transcripts_into_run_folder` + export ZIP `llm/` entries; `tools/run_insights` CLI for staging insights from exports; Cursor **three-expert review ritual** documented in `.cursor/rules/repo-memory-bank.mdc`.
- 2026-04-11: Added `Diagnostics/CoachGameLog` so metadata JSON ingestion can log under `dotnet test` without initializing Godot-backed `MegaCrit.Sts2.Core.Logging.Log` (fixes xUnit host AV on Windows); README gained a contributor “quick map” + explicit headless-test note; `.cursor/mcp.json` gitignored as machine-local.
- 2026-04-11: `tools/release/build-release.ps1` now packages `dll` + manifest + `contextcoach.config` + `result_cleaned.csv` only (metadata is embedded in the DLL); `Sts2ContextCoach.csproj` assembly/file version aligned with `Sts2ContextCoach.json` for v0.1.3.
- 2026-04-11: Fixed root `.gitignore` `release/` rule to `/release/` so `tools/release/*.ps1` is trackable while zip output stays ignored.
- 2026-04-11: Added `docs/RELEASE_NOTES_v0.1.3.md` as the canonical English + 简体中文 text for the GitHub Release body.
- 2026-04-08: Created initial memory bank and always-on rule so future sessions load repo context by default.
- 2026-04-08: Added explicit auto-update protocol so future sessions refresh this memory when structure/goals/usage evolve.
- 2026-04-08: Added `Sts2ContextCoach.Tests` xUnit workflow and documented `dotnet test` command to guard behavior during refactors.
- 2026-04-09: Tightened LLM prompt behavior to follow runtime locale for output language and avoid asserting deck synergies without explicit payload evidence.
- 2026-04-09: Added periodic LLM `deck_profile` cache (deck/relic signature + floor delta refresh) and prompt injection to improve deck-mechanic awareness without sending full deck metadata each pick.
- 2026-04-10: Documented optional `code-review-graph` trial (MCP config, `.code-review-graphignore`, gitignored `.code-review-graph/`, Windows UTF-8 tip for `detect-changes`) so maintainers can use blast-radius/review context without bloating the repo index.
- 2026-04-10: Documented Qwen3 local embeddings setup (`[embeddings]` pip extras, `CRG_EMBEDDING_MODEL` in MCP `env`, re-embed on model change) after enabling semantic search for the graph.
- 2026-04-11: Documented `semantic_search_nodes` slowness drivers (CPU-only PyTorch, per-call `SentenceTransformer` construction in `code-review-graph`, full-vector scan) and practical mitigations for maintainers.
- 2026-04-11: Added maintainer conda env `sts2-context-coach` (`environment.yml`, `requirements-dev.txt`, `tools/dev/setup-conda-env.ps1`, README instructions, CUDA 12.8 PyTorch pip order) so data_refresh and code-review-graph share a GPU-capable Python stack.
- 2026-04-11: Pointed [`.cursor/mcp.json`](../.cursor/mcp.json) `code-review-graph` server at `sts2-context-coach`’s interpreter (`python.exe -m code_review_graph serve`); other machines should retarget conda prefix.
- 2026-04-11: Removed personal conda tooling preferences (mamba/uv) from README, memory bank, and Cursor/AGENTS rules; maintainer docs stay project-scoped (`conda` + `pip` only in repo text/scripts).
- 2026-04-11: README Maintainer Notes and memory bank `code-review-graph` section reduced to repo-generic setup pointers (no long MCP/embeddings latency prose).
- 2026-04-11: README Maintainer Notes expanded with **C# onboarding** (`setup-lib.ps1`, `local.props.example`, .NET 9), optional Python callout, build vs release zip, link to `docs/GITHUB_RELEASE_GUIDE.md`.
- 2026-04-11: Optional **CRG `SentenceTransformer` cache** (site-packages `.pth` + `CRG_APPLY_ST_CACHE_PATCH` in `.cursor/mcp.json`) and README note on **CPU-bound hybrid retrieval** vs brief GPU `encode`; `run_bench_st_cache.bat` for A/B timing.
