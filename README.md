# SupGalaxy-Server
Run and administer an always-on SupGalaxy daemon on your own server.

A dedicated WebRTC host for [SupGalaxy](https://github.com/embiimob/SupGalaxy). Players click **Connect to Server** in the game. The game POSTs its offer to this server, and the answer comes back in the HTTP response, so a connection takes about a second instead of waiting on Sup!?/IPFS polling. The server relays game messages between all players (star topology, scoped per world), keeps world edits, and saves them to disk.

> The SupGalaxy client must be updated to use this server. The exact changes, written for an AI agent, are in
> [`src/SupGalaxyServer.Core/ClientIntegrationNotes.cs`](src/SupGalaxyServer.Core/ClientIntegrationNotes.cs).

## Features
- **Main (signaling) port**: default `55555`. It always listens on `127.0.0.1` as well as the configured bind address.
- **Player port range**: default `55556-56556` (UDP, one even port per player, so **501 players** by default).
- **GUI (Windows)**:
  - Alphabetical, searchable player list with details: world, IP, port, connected time, traffic, position, world authority.
  - **Kick** a player.
  - **Block** a player by name, and a list of blocked users with **Unblock**.
  - Only one session per username (case-insensitive). A second login with the same name gets `409 name_in_use`.
- **Shared discoveries**: Chunk Keyword / IPFS imports sent by a client (`ipfs_chunk_from_client_*`) are re-assembled, validated, saved per world and sent to the other players in that world; players who join later get them in the initial world sync. Limits: `MaxImportSize`, `ImportTimeoutSeconds`, `MaxPendingImportsPerPlayer` in `settings.json`.
- **Saves**: an incremental save runs every 10 minutes (configurable), plus a save on shutdown. On restart the server continues from the last save. **Reset saved session…** discards it after a warning.
- **Low lag**: high-frequency updates are dropped for congested receivers, there are per-player rate limits, and voice/video goes directly between nearby players (the server relays only their `p2p_signal` messages).

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
- Step-by-step guide (GoDaddy DNS, free Let's Encrypt certificate via win-acme, Windows Firewall, running as a service): [`docs/SSL-GoDaddy-WindowsServer.md`](docs/SSL-GoDaddy-WindowsServer.md).
