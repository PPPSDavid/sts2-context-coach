# STS2 Context Coach

Context-aware card scoring overlay for Slay the Spire 2.

For most players, this project is **plug-and-play**: download the release zip, extract into your game `mods` folder, and play. You do **not** need Python, wiki fetch, or LLM setup.

## What It Does

- Shows `Base` + `Ctx` score on reward/shop cards.
- Explains top context reasons with signed impact (`+` / `-`).
- Uses deck state, relic synergies, shop economy, upgrade value, and enchantment context.
- Supports English + Simplified Chinese (`zhs` auto-maps to `zh-CN`).

## Supported Runtime Features

- **Deck context:** block/draw/frontload/scaling pressure, curve pressure, redundancy.
- **Shop context:** affordability, tight-gold penalties, discount/value bonuses.
- **Upgrade context:** deterministic mechanics from wiki text + LLM tier summary.
- **Enchantment context:**
  - realized value when enchantment is present now,
  - modest expected-value bonus for future enchantment upside.
- **Localization:** embedded `en` and `zh-CN`.

## Install (Typical User)

1. Download `Sts2ContextCoach-vX.Y.Z.zip` from GitHub Releases.
2. Extract so you get:
   - `Sts2ContextCoach.dll`
   - `Sts2ContextCoach.json`
   - `result_cleaned.csv`
   - `data/cards.json`
   - `data/relics.json`
3. Copy into:
   - `<Slay the Spire 2>/mods/Sts2ContextCoach/`
4. Ensure `BaseLib` is installed/enabled.
5. Launch game.

## Optional Debug Environment Variables

- `STS2_CONTEXT_COACH_VERBOSE=1` enables verbose debug logs.
- `STS2_CONTEXT_COACH_LANG=zh-CN` forces Chinese (normally auto-detected from game settings).

## Developer Workflow (Quick)

```powershell
dotnet build .\Sts2ContextCoach.csproj -c Release
```

If `local.props` has `DeploySts2Mod=true` and `STS2GamePath`, build will auto-deploy to your game mods folder.

### Data Refresh / LLM (Optional, Maintainer-Only)

This is for metadata curation, not normal gameplay use.

- See `tools/data_refresh/README.md`.
- Keep API keys in environment variables only.
- Do not commit `tools/data_refresh/config.yaml` (already gitignored).

## Release Packaging

Use:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\release\build-release.ps1
```

Zip output is written to `release/`.

