# SupGalaxy-Server
Run and administer an always-on SupGalaxy daemon on your own server.

A dedicated WebRTC host for [SupGalaxy](https://github.com/embiimob/SupGalaxy). Players click **Connect to Server** in the game. The game POSTs its offer to this server, and the answer comes back in the HTTP response, so a connection takes about a second instead of waiting on Sup!?/IPFS polling. The server **runs the game rules for every active world** (players are not trusted), relays the remaining game messages between players (star topology, scoped per world), and saves the worlds to disk.

> The SupGalaxy client must be updated to use this server. The exact changes, written for an AI agent, are in
> [`src/SupGalaxyServer.Core/ClientIntegrationNotes.cs`](src/SupGalaxyServer.Core/ClientIntegrationNotes.cs).

## Features
- **Main (signaling) port**: default `55555`. It always listens on `127.0.0.1` as well as the configured bind address.
- **Player port range**: default `55556-56556` (UDP, one even port per player, so **501 players** by default).
- **GUI (Windows)**:
  - Alphabetical, searchable player list with details: world, IP, port, connected time, traffic (messages in/out), position.
  - **Kick** a player.
  - **Block** a player by name, and a list of blocked users with **Unblock**.
  - Only one session per username (case-insensitive). A second login with the same name gets `409 name_in_use`.
- **Saves**: an incremental save runs every 10 minutes (configurable), plus a save on shutdown. On restart the server continues from the last save. **Reset saved session…** discards it after a warning.
- **Low lag**: high-frequency updates are dropped for congested receivers, there are per-player rate limits, and voice/video goes directly between nearby players (the server relays only their `p2p_signal` messages).

## Server-authoritative game rules (protocol 2)
No player is "host". Clients only send requests (`block_hit`, `request_block_break`, `request_block_place`, `request_block_toggle`, `fish_spawn_request`, `player_hit`, ...). The server decides and sends the results (`block_change`, `add_to_inventory`, `block_action_denied`, ...). Rule-result messages sent by clients are dropped.
- **Terrain**: `Rules/TerrainGenerator.cs` is a bit-identical C# port of the client's `js/worker.js` (sha256 parity tests in `TerrainTests.cs`), so the server knows every natural block.
- **Implemented on the server** (`Rules/WorldRules.cs`): block strength, tool and laser damage, drops (and the leaf tree-seed bonus), blue-laser area mining, placing and reach checks, doors, chunk ownership (home spawn chunks and one-year edit claims with a 30-day takeover window, persisted), tree-seed growth, fish spawn commands, PvP melee range/damage/knockback, lava damage, volcano events (Vulcan worlds), magician/calligraphy stone validation, mob ownership (only the spawner may move, kill or despawn its mobs) and limits on mob damage and loot.
- **Still client-side**: mob AI (run by the spawning client), inventory contents, health, crafting, chests, and chunk claims that exist only on IPFS/Sup!? (the server only knows claims made through it).

## Build / run / test (.NET 8 SDK)
```bash
dotnet build SupGalaxyServer.sln
dotnet test                                                   # unit + real WebRTC end-to-end tests over 127.0.0.1
dotnet run --project src/SupGalaxyServer.Gui                  # Windows GUI
dotnet run --project src/SupGalaxyServer.Headless -- --port 55555 --players 55556-56556   # any OS
```
Headless options: `--data <dir> --port <n> --players <a-b> --bind <ip> --name <name> --public <ip>`.
Console commands: `list [filter]`, `info <name>`, `kick <name>`, `block <name>`, `unblock <name>`, `blocked`, `worlds`, `save`, `reset`, `quit`.

Data directory (default `%LOCALAPPDATA%/SupGalaxyServer`, override with `--data`): `settings.json`, `blocked.json`, `session/`.

## Quick local test
1. Start the server, then open `http://127.0.0.1:55555/info`.
2. In two browser profiles running the updated SupGalaxy client, click **Connect to Server** and enter `127.0.0.1`.

## HTTP API
| Method | Path | Result |
|---|---|---|
| GET | `/info` | name, protocol, players, maxPlayers, port range |
| GET | `/health` | `{status:"ok", players}` |
| POST | `/connect` | body `{world,user,offer:{type,sdp},iceCandidates}` → `{ok,answer:{type,sdp},iceCandidates,port,...}`. Errors: 400 `bad_request`, 403 `blocked`, 409 `name_in_use`, 503 `server_full`, 500 `negotiation_failed` |

## Internet hosting
- Forward **TCP 55555** and **UDP 55556-56556** to the server. Set **Public IP** in the GUI (or `--public`) when the server is behind NAT.
- Browsers on `https://` pages may only call plain `http://` on `127.0.0.1`/`localhost`. For remote players, set `CertificatePath`/`CertificatePassword` (a `.pfx` file) in `settings.json` so the server is reachable over `https://host:55555`.
- Step-by-step guide (Cloudflare DNS, Let's Encrypt via win-acme or a Cloudflare Origin certificate, Windows Firewall, running as a service): [`docs/SSL-Cloudflare-WindowsServer.md`](docs/SSL-Cloudflare-WindowsServer.md).
