# CustomRpTweaks.Client

BepInEx client plugin companion of [CustomRpTweaks-Server](https://github.com/rp-an-87/CustomRpTweaks-Server) (SPT 4.1.x).

## Features

- **Hide other characters' skill notifications**: bots/NPCs level up skills and weapon masteries too (a lot, with high skill multipliers), and those notifications could show up on your screen. Only your own character can show them now. Configurable in BepInEx's config (`RPAN87.CustomRpTweaks.cfg`).
- **Skill throttle**: in raid, your PMC's skills can't level past your PMC level plus a configurable overshoot (experience past that is discarded). Throttled skills show in red on the skills screen with a tooltip line. Crafting and Hideout Management are excluded.
- **Special slots layout**: with more than 3 special slots (see the server mod's `pockets` option), the extra slots wrap onto new rows instead of overflowing. Slots per row is configurable.
- **Stash sort**: optionally keep the top N rows of the stash free when sorting; they are only used when everything else is full. Hovering the sort button shades the reserved rows.
- **Craft FiR items**: optionally show FiR items in hideout craft requirements as `non-FiR (FiR) / required`, and make the craft's buy button purchase replacements for the FiR items the craft would use.
- **Kit button**: optionally adds a button above the pin button of your stash (hideout/menu only). It brings a preset list of consumables from the stash into pockets, then vest: health resource (AFAK > Salewa > IFAK > Car > AI-2), bandage points, heavy bleed points (CALOK-B > CAT > Esmarch), Propital and grenades (an amount per grenade type: M67, RGD-5, F-1, VOG-17/25 and impact RGN/RGO, the last two picked at random; each amount is an "at least", and an optional grenade quota tops the total up with random non-impact grenades from the stash). Each amount is configurable (0 disables), what you carry counts towards it, non-FiR and fuller items are preferred, and worse carried items can be swapped for better ones from the stash. The same button also tops up raid cash (a target for roubles, euros and dollars, section "6c. Kit cash"): money already in a docs case, SICC case, wallet or WZ wallet you carry (or have in the secured container) counts, and the missing amount is moved from the stash into one of those, splitting a stack if needed. It can't tell which map you will play, so the amount is one per currency.
- **Magazine top-up**: a hotkey on a hovered magazine fills it with the ammo it already holds, or else the best ammo it accepts.
- **Disassemble**: a hotkey (Alt+D) on a hovered item takes off the parts attached to it, recursively or only the top level, and puts them next to it in the container it lies in. Works in the stash and, with the game's own raid rules, in a raid.
- **Equip plate**: a hotkey (I) on a hovered armor plate in the stash puts it into the first free plate slot of the armor, rig or helmet you wear.
- **Raised price indicator**: with the server mod's trader overstock feature on, prices the server has raised for you are drawn in orange (slightly larger under items in the trader's grid) on the trader screen and in the flea market, and the trader's item tooltip shows the base price struck through before the raised one. The client works out which prices are raised from the prices it has seen and asks the server once per game session, right after the first trader offers or flea results load, for offers that were already raised when the game started. If the server mod is missing or has the feature or its `clientIndicator` option off, the client stops asking and shows nothing extra. After a flea market purchase that uses up a trader offer's whole limit, that trader's offers are reloaded so the trader screen has the new price. Colours, size and the tooltip are configurable ("12. Price indicator").
- **Keep walk speed while zooming**: optionally ignores the mouse-wheel walk speed change while you aim through a smooth-zoom scope and hold your scope zoom key combination (for example Alt + wheel).

## Build

Set `SptDir` in `CustomRpTweaks.Client.csproj.user` (the folder containing `BepInEx` and `EscapeFromTarkov_Data`), then `dotnet build`. The DLL is copied to `BepInEx/plugins/CustomRpTweaks.Client`.

## License

MIT, see [LICENSE](LICENSE).
