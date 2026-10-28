# CustomRpTweaks.Client

BepInEx client plugin companion of [CustomRpTweaks-Server](https://github.com/rp-an-87/CustomRpTweaks-Server) (SPT 4.1.x).

## Features

- **Hide other characters' skill notifications**: bots/NPCs level up skills and weapon masteries too (a lot, with high skill multipliers), and those notifications could show up on your screen. Only your own character can show them now. Configurable in BepInEx's config (`RPAN87.CustomRpTweaks.cfg`).
- **Skill throttle**: in raid, your PMC's skills can't level past your PMC level plus a configurable overshoot (experience past that is discarded). Throttled skills show in red on the skills screen with a tooltip line. Crafting and Hideout Management are excluded.
- **Special slots layout**: with more than 3 special slots (see the server mod's `pockets` option), the extra slots wrap onto new rows instead of overflowing. Slots per row is configurable.
- **Stash sort**: optionally keep the top N rows of the stash free when sorting; they are only used when everything else is full. Hovering the sort button shades the reserved rows.
- **Craft FiR items**: optionally show FiR items in hideout craft requirements as `non-FiR (FiR) / required`, and make the craft's buy button purchase replacements for the FiR items the craft would use.
- **Unchamber**: a hotkey (U, the same key as UI Fixes' unload, so one press unloads the magazine and empties the chamber) on a hovered weapon takes the round out of its chamber and puts it in a container, without taking the weapon apart. Where things go is the same rule as for Dismantle and Split (below).
- **Where things go** (Dismantle, Unchamber, Split): the container the item lies in, then any container its owner carries, then any other inventory open on screen (a bag, a dead body), then the other side (for your own gear in the menu the stash, for someone else's gear what you carry). In a raid there is no stash: when nothing has room, nothing is moved.
- The magazine top-up (T) and repair-with-a-kit (R) hotkeys moved to the **GetReady** plugin, which shows their hints in the tooltip block below.
- **Dismantle**: a hotkey (Alt+D) on a hovered item takes off the parts attached to it, all the way down (a scope on a mount on a rail), and puts them next to it in the container it lies in. The hovered item itself stays where it is unless a setting says otherwise. Works in the stash and, with the game's own raid rules, in a raid. A second hotkey (Ctrl+Alt+D) does the same but leaves the vital parts (the ones in required slots, a receiver for instance) attached, as in a raid, so only the rest of the gun comes off.
- **Equip plate**: a hotkey (I) on a hovered armor plate in the stash puts it into the first free plate slot of the armor, rig or helmet you wear.
- **Raised price indicator**: with the server mod's trader overstock feature on, prices the server has raised for you are drawn in orange (slightly larger under items in the trader's grid) on the trader screen and in the flea market, and the trader's item tooltip shows the base price struck through before the raised one. The client works out which prices are raised from the prices it has seen and asks the server once per game session, right after the first trader offers or flea results load, for offers that were already raised when the game started. If the server mod is missing or has the feature or its `clientIndicator` option off, the client stops asking and shows nothing extra. After a flea market purchase that uses up a trader offer's whole limit, that trader's offers are reloaded so the trader screen has the new price. Colours, size and the tooltip are configurable ("12. Price indicator").
- **Split stack**: a hotkey (Ctrl+S) on a hovered stack (ammo, money, grenades, anything that stacks) splits it in half without the split dialog; the new stack goes next to it.
- **Hotkey hints**: a small line below a separator at the end of an inventory item's tooltip lists the hotkeys of this mod that apply to that item right now (top up and repair from GetReady, equip plate, dismantle, unchamber), with the keys as configured. Can be turned off ("13. Hotkey hints"). With the UI Fixes mod installed, its item shortcuts that work on the hovered item (open, use, unload, top up ammo, install and uninstall mods, unpack, pin, lock and more) are listed too, with the keys set in UI Fixes ("14. UI Fixes hints": each shortcut can be switched off, and the number of hints is capped).
- **Keep walk speed while zooming**: optionally ignores the mouse-wheel walk speed change while you aim through a smooth-zoom scope and hold your scope zoom key combination (for example Alt + wheel).
- **Steady aim and holding breath**: the elite weapon draw perk (Aim Drills and weapon skills) steadies the aim for 3 seconds after you start aiming, but the game applies it even while you hold your breath, and the aim then drifts as the 3 seconds run out. With this on (default), holding breath during those seconds steadies the aim as normal, and the perk applies again if you release the breath within them.

## Build

Set `SptDir` in `CustomRpTweaks.Client.csproj.user` (the folder containing `BepInEx` and `EscapeFromTarkov_Data`), then `dotnet build`. The DLL is copied to `BepInEx/plugins/CustomRpTweaks.Client`.

## License

MIT, see [LICENSE](LICENSE).
