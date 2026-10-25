# MODLOG: Berry Bury Berry co-op

## Facts
- Install: `F:\SteamLibrary\steamapps\common\BerryBerryBerry`, exe `BerryBerryBerryBerryBerryBerryBerryBerryBerry.exe`.
- Unity 6000.2.6f2, Mono backend, DX12. Company/product from app.info: `Get(Color) Games` / `BerryBerryBerryBerryBerryBerryBerryBerryBerry`.
- Saves: `%USERPROFILE%\AppData\LocalLow\Get(Color) Games\BerryBerryBerryBerryBerryBerryBerryBerryBerry\CloudSave\slot_N.json` (JsonUtility of `SaveLoadManager.SavedPlayerData`).
- Ships Netcode for GameObjects + Facepunch transport, but only leftover prototype classes use them (`LobbyManager`, `ObjectOwnershipHelper`). The real game is single-player MonoBehaviours. Not reused.
- Decompiled (ilspycmd 9.1) to `C:\Users\NickBin\BerryBerryBerry-decomp` (outside the repo, never shipped).
- Local tools in `.tools/`: .NET SDK 8.0.426, ilspycmd 9.1.0.7988.

## How the game runs
- Scene `Game`. Round = one day. `GameManager.GameState`: PreRoundSetup -> Intro (VCR) -> Playing -> (night when `roundTimer_Curr` hits 0) -> RoundOverShop.
- Round end: `ForceEndRound(fromBed)` banks `money_Held` into `money` (half if fell in the hole), opens shop.
- Next round: `InShop_CloseShopAndStartNextRound` -> `SaveLoadManager.SaveDataAndStartNextRound` -> save JSON, reload scene, `LoadSavedGame` after 5 frames.
- Everything persistent between rounds is the save JSON. Buildables are not saved; they are placed per round.
- Currencies on `PlayerStats`: `money` (banked), `money_Held` (pocket), `seeds`, `starOrbs`.
- Coins/seeds: `SuckUppables` + `Coin`; picked by `OnPlayerPickedUsed` (manual or AutoCoinPickUp sphere), credited after 0.35 s.
- Berries eaten by the turtle (`TurtleEatAoe.EatBerry`) -> coin queue spawns coins.
- Player actions are all in `PlayerGrabber` (pickup, throw, kick `KickInFrontOfUs`, vacuum, popgun, build, sell, move hole, abilities).

## Route
BepInEx 5.4.23.x + HarmonyX plugin (Mono, Unity 6 confirmed working by the universal-modder Fart Together note).
Online through the Multiplayer Everything relay in tunnel mode (own TCP protocol, host listens).

## Sync design (host-authoritative)
- Host runs the real simulation. Guests mirror it and send commands.
- World backbone: host sends its `CreateNewPlayerData()` JSON on join and on every next round; guest loads it through a patched `LoadFromSlot` (no file written). Guests never write saves.
- State mirror: reflection over value fields of singleton managers (PlayerStats, GameManager whitelist, puzzles, radio, etc.), host -> guest diffs.
- Shared wallet: guest calls to PlayerStats currency methods are forwarded to the host, which runs them. `money_Held` stays per player.
- Entities: every root with PickUppable / SuckUppables / BerryCultist_AI gets a net id. Scene objects match by hierarchy path; spawned ones are cloned on guests from prefabs found by name. Guest-local spawns of those types are destroyed (the host's copy arrives instead).
- Ownership: host owns everything; a guest that picks up / kicks / vacuums an entity claims it, simulates it and streams it; on rest it hands it back with velocity.
- Coins on guests: pickup request to host, host removes coin and grants value to that guest's pocket.
- Gameplay calls (shop buys, upgrades, build, sell, abilities, hole move, radio, walls, dig) are forwarded guest -> host, replayed host -> guest for visuals.
- Round flow: host ends the round -> guests end theirs (bank pocket). Guests wait in the shop for the host's next-round JSON.
