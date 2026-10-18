# CustomRpTweaks.Client

BepInEx client plugin companion of [CustomRpTweaks-Server](https://github.com/rp-an-87/CustomRpTweaks-Server) (SPT 4.1.x).

## Features

- **Hide other characters' skill notifications**: bots/NPCs level up skills and weapon masteries too (a lot, with high skill multipliers), and those notifications could show up on your screen. Only your own character can show them now. Configurable in BepInEx's config (`RPAN87.CustomRpTweaks.cfg`).
- **Skill throttle**: in raid, your PMC's skills can't level past your PMC level plus a configurable overshoot (experience past that is discarded). Throttled skills show in red on the skills screen with a tooltip line. Crafting and Hideout Management are excluded.
- **Special slots layout**: with more than 3 special slots (see the server mod's `pockets` option), the extra slots wrap onto new rows instead of overflowing. Slots per row is configurable.
- **Stash sort**: optionally keep the top N rows of the stash free when sorting; they are only used when everything else is full. Hovering the sort button shades the reserved rows.
- **Craft FiR items**: optionally show FiR items in hideout craft requirements as `non-FiR (FiR) / required`, and make the craft's buy button purchase replacements for the FiR items the craft would use.

## Build

Set `SptDir` in `CustomRpTweaks.Client.csproj.user` (the folder containing `BepInEx` and `EscapeFromTarkov_Data`), then `dotnet build`. The DLL is copied to `BepInEx/plugins/CustomRpTweaks.Client`.

## License

MIT, see [LICENSE](LICENSE).
