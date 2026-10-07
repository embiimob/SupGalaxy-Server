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
- **Shared discoveries**: Chunk Keyword / IPFS imports sent by a client (`ipfs_chunk_from_client_*`) are re-assembled, validated, saved per world and sent to the other players in that world; players who join later get them in the initial world sync. Invalid entries inside an import are skipped (the rest is merged) and the sender gets a `server_import_result` ack. Limits: `MaxImportSize` (default 100M characters), `ImportTimeoutSeconds`, `MaxPendingImportsPerPlayer` (256), `MaxPendingImportCharsPerPlayer` (200M) in `settings.json` (older `settings.json` files with the previous default of 4 are upgraded automatically).
- **Fast world sync**: clients that send `features: ["http_world_sync"]` in `POST /connect` download a world's saved state as a single gzip HTTP response (`GET /world-sync/{token}`, one-time per-player token) instead of hundreds of data-channel messages; live updates are held until the client confirms, and anything that goes wrong falls back to the data-channel sync. Settings: `EnableHttpWorldSync`, `HttpWorldSyncFetchTimeoutSeconds` (30), `HttpWorldSyncTimeoutSeconds` (300).
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

Browsers on `https://` pages block connections to plain `http://` addresses (except `127.0.0.1`). For players on the internet to reach your server, the signaling port (default **TCP 55555**) must serve HTTPS with a trusted certificate.

This guide covers a **Windows** host using a free **Cloudflare** non-proxied DNS record and a free **Let's Encrypt** SSL certificate via `win-acme`. If you are running headless on Linux/macOS, see the [Other OS section](#other-os-headless).

### 1. Cloudflare DNS Setup
1. In your Cloudflare dashboard, go to your domain's **DNS** settings.
2. Add an **A record** for your host (e.g., `play.supgalaxy.org`).
3. Set the **IPv4 address** to your server's public IP.
4. **Important:** Turn off the proxy status so it is **DNS only** (gray cloud icon). The game requires direct WebRTC connections.

### 2. Windows Firewall & Router
Open an **elevated PowerShell** and run these exact commands (copy and paste) to open the needed ports:
```powershell
New-NetFirewallRule -DisplayName "SupGalaxy signaling (TCP)" -Direction Inbound -Protocol TCP -LocalPort 55555 -Action Allow
New-NetFirewallRule -DisplayName "SupGalaxy players (UDP)"   -Direction Inbound -Protocol UDP -LocalPort 55556-56556 -Action Allow
New-NetFirewallRule -DisplayName "ACME HTTP validation"      -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
```
*Note: You must also forward these same ports (TCP 55555, UDP 55556-56556, and TCP 80) on your home/office router or cloud VM network security group.*

### 3. Let's Encrypt SSL via win-acme
1. Download **win-acme x64 trimmed** from [win-acme.com](https://www.win-acme.com/) and extract it to `C:\win-acme`.
2. Create the folders and restart script by copying and pasting this into your **elevated PowerShell**:
   ```powershell
   New-Item -ItemType Directory -Force C:\SupGalaxyServer\cert
   Set-Content C:\SupGalaxyServer\restart.ps1 'Restart-Service SupGalaxyServer -ErrorAction SilentlyContinue'
   ```
3. Request the certificate (replace the placeholder domain and email, then copy/paste):
   ```powershell
   C:\win-acme\wacs.exe --source manual --host play.supgalaxy.org --validation selfhosting --store pfxfile --pfxfilepath C:\SupGalaxyServer\cert --pfxpassword "ChangeMe-PfxPassword" --installation script --script C:\SupGalaxyServer\restart.ps1 --accepttos --emailaddress you@example.com
   ```
4. Your certificate is now saved in `C:\SupGalaxyServer\cert`. win-acme will automatically renew it every ~60 days.

### 4. Configure Server Settings
Edit your `settings.json` (usually in `%LOCALAPPDATA%/SupGalaxyServer` or your `--data` folder) to point to the certificate. Use **double backslashes** in the path. Set `PublicAddress` to your server's public IP.
```json
{
  "PublicAddress": "203.0.113.10",
  "CertificatePath": "C:\\SupGalaxyServer\\cert\\play.supgalaxy.org.pfx",
  "CertificatePassword": "ChangeMe-PfxPassword"
}
```

### 5. Running as a Windows Service (NSSM)
To keep the server running in the background and start on boot:
1. Download NSSM from [nssm.cc](https://nssm.cc/download) and copy `win64\nssm.exe` to `C:\SupGalaxyServer\`.
2. Install the service via **elevated PowerShell** (adjust paths as needed):
   ```powershell
   .\nssm.exe install SupGalaxyServer C:\SupGalaxyServer\app\SupGalaxyServer.Headless.exe "--data C:\SupGalaxyServer\data"
   .\nssm.exe set SupGalaxyServer AppDirectory C:\SupGalaxyServer\app
   .\nssm.exe set SupGalaxyServer Start SERVICE_AUTO_START
   .\nssm.exe set SupGalaxyServer AppStopMethodConsole 15000
   .\nssm.exe start SupGalaxyServer
   ```

### Other OS (Headless)
If you are running the headless server on Linux or macOS, the same DNS and port requirements apply (TCP 55555, UDP 55556-56556, TCP 80).
1. Use `certbot` to obtain the certificate (port 80 must be free):
   ```bash
   sudo certbot certonly --standalone -d play.supgalaxy.org
   ```
2. Combine the certificate and private key into a `.pfx` file that .NET can read:
   ```bash
   sudo openssl pkcs12 -export -out /etc/letsencrypt/live/play.supgalaxy.org/cert.pfx -inkey /etc/letsencrypt/live/play.supgalaxy.org/privkey.pem -in /etc/letsencrypt/live/play.supgalaxy.org/fullchain.pem -password pass:ChangeMe-PfxPassword
   ```
3. Update `settings.json` with the path to the `cert.pfx` file and the password, just like the Windows instructions.
