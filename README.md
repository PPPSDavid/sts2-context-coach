# STS2 Context Coach

Context-aware card recommendation overlay for Slay the Spire 2 reward/shop choices.

For most players, this mod is **plug-and-play**: download a release zip, extract into your game `mods` folder, and play. You do **not** need Python, wiki fetch, or LLM setup to use it.

## Preview

![Reward choice example](docs/images/reward-example.png)
![Shop example](docs/images/shop-example.png)

## Requirements

- **BaseLib is required.** This mod depends on BaseLib to load and patch into STS2.
- Slay the Spire 2 with mod loading enabled (beta branch/mod-capable branch).

## Installation (Typical User)

1. Download `Sts2ContextCoach-vX.Y.Z.zip` from GitHub Releases.
2. Extract the zip contents into:
   - `<Slay the Spire 2>/mods/Sts2ContextCoach/`
3. Confirm these files exist:
   - `Sts2ContextCoach.dll`
   - `Sts2ContextCoach.json`
   - `contextcoach.config`
   - `result_cleaned.csv`
   
   Card, relic, and keyword metadata are **embedded in the DLL** so Slay the Spire 2 does not treat loose `*.json` under `mods/` as extra mod manifests. You should **not** need a `data/` folder in the install directory for normal play.
4. Ensure [BaseLib](https://github.com/Alchyr/BaseLib-StS2) is installed/enabled.
5. Launch game.

## What It Shows

- `Base` + `Ctx` score on reward/shop cards.
- Top context reasons with explicit signed impact (`+` / `-`).
- Mixed positives/negatives so tradeoffs are visible.

## Supported Features

- **Deck context:** draw/block/frontload/scaling pressure, curve pressure, redundancy.
- **Shop context:** affordability, tight-gold penalties, sale/value bonuses.
- **Upgrade context:** deterministic mechanics from wiki text + upgrade value tiers.
- **Enchantment context:** realized value if present + modest expected-value upside.
- **Localization:** embedded English + Simplified Chinese (`zhs` auto-maps to `zh-CN`).

## Example Usage

- **Reward pick:** compare `Ctx` and reason lines to see whether a card solves your current deck gap (draw, block, scaling, etc.).
- **Shop pick:** check affordability penalties before buying high-score cards when gold is tight.
- **Enchanted cards:** if a card already has an enchantment, realized-value context can materially change priority.
- **Upgrades:** cards with strong upgrade trajectories can get extra context value even when base form is average.

## FAQ

### Why don’t I see any overlay?

- Verify BaseLib is installed and loaded.
- Check folder nesting; avoid `mods/Sts2ContextCoach/Sts2ContextCoach/...`.
- Confirm `Sts2ContextCoach.dll` and `Sts2ContextCoach.json` are in the same mod folder.

### Why is Chinese not showing?

- The mod auto-detects STS2 language and maps `zhs` to `zh-CN`.
- You can force language via `STS2_CONTEXT_COACH_LANG=zh-CN`.

### Is LLM required to play with this mod?

- No. LLM tooling is only for maintainers refreshing metadata.
- End users only need the release zip files.

### Can I tune logging?

- `STS2_CONTEXT_COACH_VERBOSE=1` enables verbose diagnostics.
- Default usage should keep logs quieter.

## Credits / Inspiration

- This project builds on its own context-scoring pipeline and metadata flow.
- Initial static-value direction was inspired by the STS2 draw-rate mod: [blackpatton17/sts2-draw-rate](https://github.com/blackpatton17/sts2-draw-rate).

## Maintainer Notes (Optional)

Not required to **play** with the mod; only if you are building or changing the **C# mod**, refreshing **metadata**, or running **tests** from source.

### How the mod is structured (quick map)

| Folder / file | Responsibility |
|----------------|----------------|
| `ModMain.cs` | Loads config and data, applies Harmony patches, wires startup logging. |
| `UI/` | Reward/shop overlay (`CardOverlayPatch.cs`) and in-game settings (`LlmSettingsPanel.cs`). |
| `Scoring/` | Heuristic scoring (`RecommendationEngine`, `DeckAnalyzer`) and score models. |
| `State/` | Reflection-based game state extraction, shop/economy probes, caching. |
| `Llm/` | Optional batch LLM calls, parsing, deck-profile summaries, transcript logging. |
| `Data/` | Shipped metadata (`cards.json`, `relics.json`, `keywords.json`) and loaders (`MetadataRepository`, `KeywordGlossary`). |
| `Telemetry/` | `contextcoach.config`, run folders, export helpers. |
| `Localization/` | Embedded `en.json` / `zh-CN.json` strings. |
| `tools/data_refresh/` | Maintainer Python pipeline for metadata refresh (not required to play). |
| `Sts2ContextCoach.Tests/` | xUnit regression tests for scoring and data helpers. |

Use this table as a compass when reading the repo; deeper architecture notes live in `docs/REPO_MEMORY_BANK.md`.

### C# development (.NET 9)

1. Install the **[.NET 9 SDK](https://dotnet.microsoft.com/download)**.
2. Put game/modding references under **`lib/`** (`sts2.dll`, `GodotSharp.dll`, `0Harmony.dll`, `BaseLib.dll`). Easiest: install **[BaseLib](https://github.com/Alchyr/BaseLib-StS2)** into the game’s `mods\BaseLib\`, then from the **repo root** run:

   ```powershell
   .\setup-lib.ps1 -GamePath "C:\Path\To\Slay the Spire 2"
   ```

   (Adjust **`-GamePath`** to your Slay the Spire 2 install. The script copies from the game’s `data_sts2_windows_x86_64` folder and from `mods\BaseLib\`.)

3. **Optional — deploy on build:** copy **`local.props.example`** to **`local.props`** (gitignored), set **`STS2GamePath`** to your game folder, and keep **`DeploySts2Mod`** `true` if you want `dotnet build` to copy the mod into `<game>\mods\Sts2ContextCoach\`.

### Data refresh / LLM

- See **`tools/data_refresh/README.md`** for the Python pipeline.
- Keep API keys in environment variables; do not commit **`tools/data_refresh/config.yaml`** (gitignored).

### Python environment (conda)

**Optional** — only needed for **`tools/data_refresh`**, optional **code-review-graph** / embeddings work, or other Python tooling. C#-only changes can skip this section.

Use a conda env named **`sts2-context-coach`** (see root **`environment.yml`**) so Python tooling shares one stack. Install **CUDA PyTorch** from the official **cu12.8** wheel index **before** `requirements-dev.txt` so optional embedding work sees a GPU build of `torch`.

From the **repo root**:

```powershell
conda env create -f .\environment.yml
conda activate sts2-context-coach
python -m pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128
python -m pip install -r .\requirements-dev.txt
python -c "import torch; print(torch.__version__, torch.cuda.is_available())"
```

Run **`conda env create`** only the first time; if the env already exists, start at **`conda activate`**. To start over: `conda env remove -n sts2-context-coach -y`, then repeat the block. **`tools\dev\setup-conda-env.ps1`** and **`tools\dev\recreate-conda-env.ps1`** automate the same steps.

**Cursor + code-review-graph:** the repo includes an example **`.cursor/mcp.json`**; point `command` at your env’s **`python.exe`** and use **`args`: `["-m","code_review_graph","serve"]`** so the MCP server pins that interpreter. After **`pip install`**, run **`tools\dev\install_crg_st_cache_patch.ps1`** once so a **`.pth`** hook loads **`crg_st_model_cache.py`**; keep **`CRG_APPLY_ST_CACHE_PATCH=1`** in MCP `env` (as in the example) so **`SentenceTransformer` is cached across tool calls** instead of reloading every semantic search.

**Why Task Manager may show low GPU use:** most hybrid-search time is **CPU** (SQLite + Python cosine over all stored vectors). The GPU only runs a **short** embedding forward pass per query. The cache patch removes the biggest avoidable cost (reloading the model each call). **`tools\dev\run_bench_st_cache.bat`** prints first vs second query timing on your machine.

### Build, test, and release zip

From the **repo root** (after **`lib/`** is populated):

```powershell
dotnet build .\Sts2ContextCoach.csproj -c Release
dotnet test .\Sts2ContextCoach.Tests\Sts2ContextCoach.Tests.csproj -c Release
```

Tests are plain **.NET / xUnit** (no Godot editor required). They exercise scoring helpers and JSON metadata ingestion; keep new data-layer code from calling STS2 logging APIs directly so `dotnet test` stays stable on CI machines.

Release zip (for GitHub / sharing):

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\release\build-release.ps1
```

Output: **`release/Sts2ContextCoach-v<version>.zip`**. See **`docs/GITHUB_RELEASE_GUIDE.md`** for tagging and publishing a GitHub Release.

