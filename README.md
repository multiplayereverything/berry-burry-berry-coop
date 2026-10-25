# Berry Bury Berry Co-op

Online and LAN co-op for Berry Bury Berry, as a BepInEx 5 plugin. Up to 4 players farm the host's save together:
one shared wallet, one yard, everyone's berries, coins, upgrades and buildings in the same world.

Built with AI (Claude). Toolkits used:
- [universal-modder](https://github.com/rehan-remade/universal-modder): the mod-any-game loop (recon, route, lab,
  verify in the real game) and its knowledge base, which confirmed BepInEx 5 on Unity 6 Mono.
- [REA](https://github.com/morluto/rea): its evidence-first workflow for managed assemblies. The game code was read
  with ilspycmd (the REA MCP server was not connected); every hook is based on the decompiled code, not on guesses.
- [graphify](https://github.com/Graphify-Labs/graphify): a code graph of the decompiled game to find the hub classes.
- [anti-slop](https://github.com/miqdadbadjuber/anti-slop): rules for comments and this README.
- The Multiplayer Everything global mod relay, in tunnel mode.

## Install

1. Install BepInEx 5 (x64, 5.4.23.x) into the game folder: unzip it next to
   `BerryBerryBerryBerryBerryBerryBerryBerryBerry.exe`, start the game once, quit.
2. Copy `BerryCoop.dll` to `BepInEx/plugins/BerryCoop/`.
3. Every player needs the same mod version.

## Play

Press **F8** for the co-op window.

- **Host online room**: opens a room on the relay and shows a 5-letter code. No port forwarding.
  Then start or load a game; friends load into it automatically.
- **Join**: from the main menu, type the code (or pick a public room), or join a LAN / direct IP (port 27777).
- The host's save is the shared farm. Guests' own saves are never written while they play on someone else's farm.

### What is shared

| Thing | How |
|---|---|
| Money, star orbs, seeds, piggy bank | One wallet on the host; every player sees and spends it |
| Coin pocket | Shared too: any coin anyone collects shows for everyone right away, banked into the wallet at bedtime |
| Upgrades, shop purchases, plant tiers | Bought by anyone, paid from the wallet, applied for everyone |
| Berries, coins, seeds, star orbs, tools, props, berry buddies, piñatas | Simulated on the host and mirrored (including whether each can be grabbed); the player holding something simulates it locally |
| Joining | A "Syncing" screen covers the farm until the host's state arrives; the host resends everything every 5 s |
| Picking up, throwing, kicking, vacuuming | Work on every object; ownership passes to whoever touches it |
| Dirt patches, conveyors, trampolines, juicers | Placed and sold by anyone, same tiles for everyone |
| Day timer, night, abilities, hole growth and movement | Host-driven, mirrored |
| Walls, dig spots, radio, berry picker, slot machine | Actions are sent to the host and replayed for everyone |
| End of day / next day | Anyone going to bed ends the day for everyone; the host starts the next day, guests load the same save |

Cutscenes and endings play for whoever triggers them and are not shared. Guests return to the menu when the host does.

## Settings

`BepInEx/config/berrybury.coop.cfg`: name, max players (2-4), public room listing, LAN port, menu key, and a relay
address. Leave the relay empty: the mod reads the current server from
[mods-info](https://github.com/multiplayereverything/mods-info) and falls back to the built-in one.

## Build

```bash
dotnet build src/BerryCoop/BerryCoop.csproj -c Release -p:GameDir="F:\SteamLibrary\steamapps\common\BerryBerryBerry" -p:CopyToGame=true
```

References the game's own DLLs from `GameDir`; nothing from the game is in this repository.

## Testing

`tools/lab.ps1` starts a host and a guest copy on one PC with scripted actions (`-berrycoop-lab host|guest`) and
writes `LAB` lines and screenshots. Lab runs keep their saves in `lab/`, never in your save folder.
`-Online -Relay 127.0.0.1:7799` runs the same test through a local copy of the relay (`relay-test/`).

## Credits

- Relay and client helpers (`src/BerryCoop/Relay`): Multiplayer Everything global mod relay.
- BepInEx and HarmonyX.
- Berry Bury Berry by Get(Color) Games. This mod ships none of the game's files or code.
