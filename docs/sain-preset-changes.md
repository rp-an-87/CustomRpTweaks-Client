# SAIN preset "No Bullshit" – change ledger

**File:** `C:\SPT Stuff\SPT 4160\SPT_Runtime\user\mods\Solarint-SAIN-ServerMod\Presets\No Bullshit.json`
(base: `harderpmcs`; created via the server web UI)

Path inside the file: `BotSettings.<WildSpawnType>.Settings.<easy|normal|hard|impossible>.Difficulty.<key>`

## Scav nerf – flat -30% (current, 2026-10-19)

Applied to `assault` (Scav), `assaultGroup` (Scav Group), `cursedAssault` (Tagged and Cursed Scav) and `crazyAssaultEvent` (Crazy Scav Event). Same values on every difficulty for the first three keys, because the base already scales by difficulty via `Core.VisibleDistance` (100/125/150/200 m), `VisibleAngle`, `AimForHeadChance`, etc.
**Not changed:** `marksman` (Scav Sniper).

| Key (`Difficulty.*`) | Base easy / normal / hard / imp | Now | Change | Effect |
|---|---|---|---|---|
| VisibleDistCoef | 1 / 1 / 1 / 1 | 0.7 each | -30% | multiplies `Core.VisibleDistance`: effective sight 70 / 87.5 / 105 / 140 m |
| GainSightCoef | 1 / 1 / 1 / 1 | 0.7 each | -30% | spot speed, lower = slower to notice |
| ScatteringCoef | 1 / 1 / 1 / 1 | 1.3 each | +30% spread | higher = less accurate |
| HearingDistanceCoef | 0.5 / 0.65 / 0.75 / 1 | 0.5 / 0.65 / 0.75 / 1 | none (base) | hearing range |

Note: Crazy Scav Event's true base was never captured; it was set to the same values as the other scavs (hearing 0.5 / 0.65 / 0.75 / 1).
Tuning: to nerf/buff, change the same key in all four types.

## Guard and goon hearing reduction (2026-10-19)

Goal: guards abandon their boss to investigate far-off noises. Reduced `Difficulty.HearingDistanceCoef`, same value on all four difficulties. First attempt used the 3.x `No Bullshit 413` values (0.3 / 0.5); softened to 0.7 across the board to see how it feels.

| Bot (preset name) | Key | Base (all difficulties) | 3.x NB413 value | Now |
|---|---|---|---|---|
| Rashala Guard | `followerBully` | 1 | 0.3 | 0.7 |
| Gluhar Guard Assault | `followerGluharAssault` | 1 | 0.5 (imp 1.0) | 0.7 |
| Gluhar Guard Scout | `followerGluharScout` | 1 | 0.5 | 0.7 |
| Gluhar Guard Security | `followerGluharSecurity` | 1 | 0.5 | 0.7 |
| Gluhar Guard Snipe | `followerGluharSnipe` | 1 | 0.5 | 0.7 |
| Kaban Guard | `followerBoar` | 1 | 0.5 | 0.7 |
| Basmach | `followerBoarClose1` | 1 | 0.5 | 0.7 |
| Gus | `followerBoarClose2` | 1 | 0.5 | 0.7 |
| Knight (goon boss) | `bossKnight` | 1 | 1 | 0.7 |
| BigPipe | `followerBigPipe` | 1 | 1 | 0.7 |
| BirdEye | `followerBirdEye` | 1 | 1 | 0.7 |

Unchanged (hearing 1): Sanitar, Shturman, Tagilla guards, Kolontay guards, Zryachiy guard, all other bosses.
Version history: 3.9 (SPT 395) none; 3113 NB411 Gluhar guards + Rashala Guard 0.5; NB413 added Kaban/Kolontay guards 0.5, Rashala Guard 0.3.
Tuning: lower = hear less and chase fewer noises; the 3.x values (0.5 / 0.3) are the next step if they still rush. If other followers start seeking you out, apply the same key to them.

## Superseded schemes

- Tiered leniency (user-made on Crazy Scav Event, then copied to the other three): VisibleDistCoef/GainSightCoef 0.5/0.6/0.7/0.75, ScatteringCoef 2/1.75/1.6/1.5, HearingDistanceCoef 0.3/0.4/0.45/0.5. File state kept as `No Bullshit.json.bak2`.
- Flat -30% including hearing (0.35/0.455/0.525/0.7). File state kept as `No Bullshit.json.bak3`.

## Watch list / deferred (nothing applied)

3.x settings not carried over to 4.x. Revisit only if the problem comes back. Paths are under `GlobalSettings` in the preset file.

- **PMCs/Raiders all rushing the player after one shot** (3.x problem; 4.x not yet confirmed): 3.x NB413 had `Difficulty.AggressionCoef` 0.5 (4.x: 1) and 411 had `Difficulty.HearingDistanceCoef` 0.4 (4.x: 1). Nerf options: lower these globally (try 0.7–0.8 first) or lower per-bot `HearingDistanceCoef` on `pmcUSEC`, `pmcBEAR`, `pmcBot` (Raider). Note: 3.x NB413 SEEK ME OUT had both at 10 (hunt-the-player setup), so don't copy that.
- **Labs** (not played yet): 3.x NB413 had `Location.LocationSettings.Labs` `HearingDistanceCoef` 0.5 and `AggressionCoef` 2.0 (4.x: 1 / 1).
- **`General.Extract.SAIN_EXTRACT_TOGGLE`**: True in 3.x, False in 4.x. If bots linger in raid, turn it on.
- **Chatter**: `Talk` / `SquadTalk` chances were 100 in 3.x, 10–85 now; PMC/goon talkative off. Cosmetic.

## Candidates if scavs are still too strong (not yet changed)

Same path, other blocks, per difficulty:
- `Core.VisibleDistance` / `Core.VisibleAngle` – hard cap on sight range/cone
- `Core.ScatteringPerMeter`, `Core.ScatteringClosePerMeter` – accuracy over distance
- `Aiming.MAX_AIMING_UPGRADE_BY_TIME` – how much aim improves while holding fire
- `Shoot.*` burst/full-auto settings (look for `AUTOMATIC_FIRE_SCATTERING_COEF`, burst lengths) – the "full burst" behaviour
- `GlobalSettings.Difficulty` – affects every bot (currently ScatteringCoef 0.75, ACCURACY_SPEED_COEF 0.8)

## Backups (temporary, may be deleted)

Pre-change copies of `No Bullshit.json` were kept in the temp scratchpad of the session that made the changes, so
they are not permanent. The live preset file above is the source of truth. What each copy held:
- `No Bullshit.json.bak` = before any scav change (holds the base values)
- `No Bullshit.json.bak2` = tiered scheme state
- `No Bullshit.json.bak3` = flat -30% incl. hearing, Crazy Scav Event still tiered
- `No Bullshit.json.bak4` = after scav changes, before guard hearing reduction
- `No Bullshit.json.bak5` = guards at 0.3/0.5, before softening to 0.7 and adding goons

If you want a permanent rollback point, copy the preset file somewhere safe before editing it.

## Related mod forks (not SAIN)

Changes made alongside the SAIN work, kept as local git forks next to this repo (`dev-own/`):
- `ABPS-fork` (branch `scav-groups`): Acid's Bot Placement System with scav groups and configurable scav spacing.
  Client plugin only; server config untouched. License is CC BY-NC-ND 4.0, so this fork is private and not for redistribution.
- `DynamicMaps-fork` (branch `scav-heatmap`): Dynamic Maps with a scav heatmap overlay (per-map settings, intel level,
  pack merging, stable offset, rogue/raider toggle). MIT.

Both deploy only their plugin DLL to the game, keep the original as `.orig` and the previous build as `.bak`
next to it, and can be built while the game runs (the new DLL loads on the next start).
