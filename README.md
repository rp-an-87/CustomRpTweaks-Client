# CustomRpTweaks.Client

BepInEx client plugin companion of [CustomRpTweaks-Server](https://github.com/rp-an-87/CustomRpTweaks-Server) (SPT 4.1.x).

## Features

- **Hide other characters' skill notifications**: bots/NPCs level up skills and weapon masteries too (a lot, with high skill multipliers), and those notifications could show up on your screen. Only your own character can show them now. Configurable in BepInEx's config (`RPAN87.CustomRpTweaks.cfg`).

## Build

Set `SptDir` in `CustomRpTweaks.Client.csproj.user` (the folder containing `BepInEx` and `EscapeFromTarkov_Data`), then `dotnet build`. The DLL is copied to `BepInEx/plugins/CustomRpTweaks.Client`.

## License

MIT, see [LICENSE](LICENSE).
