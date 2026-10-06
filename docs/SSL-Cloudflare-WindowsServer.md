# SSL for SupGalaxy-Server with Cloudflare and Windows Server

SupGalaxy runs on an `https://` page. Browsers block calls from an `https://` page to a plain `http://` address, except for `127.0.0.1` and `localhost`. So players on the internet can only reach your server if its signaling port (default **TCP 55555**) serves HTTPS with a certificate that is valid for the host name they type.

The examples below use these values. Replace them with your own:

| Placeholder | Example |
|---|---|
| Your domain (a zone in Cloudflare) | `example.com` |
| Server host name | `play.example.com` |
| Server public IPv4 | `203.0.113.10` |
| Install folder | `C:\SupGalaxyServer` |

## Which setup to pick

| | **A. DNS only + Let's Encrypt (recommended)** | **B. Cloudflare proxy + Origin certificate** |
|---|---|---|
| Cloudflare cloud icon | grey (DNS only) | orange (proxied) |
| Signaling port | 55555 (any port) | must be a port Cloudflare proxies: **443, 2053, 2083, 2087, 2096, 8443** |
| Certificate | free Let's Encrypt, created and renewed automatically by win-acme using a Cloudflare API token | Cloudflare Origin CA certificate (valid up to 15 years, trusted **only** by Cloudflare) |
| What players type | `play.example.com` | `play.example.com:8443` (or `https://play.example.com` for 443) |

Two things are the same in both setups:

- **WebRTC game traffic (UDP 55556-56556) never goes through Cloudflare.** Cloudflare's proxy only carries HTTP(S), so players always connect straight to the server's public IP for the game data. The proxy in option B only hides the short `/connect` request. It does **not** hide your IP, because the IP is part of the WebRTC answer.
- Windows Firewall and your router/cloud firewall must allow TCP for the signaling port and UDP 55556-56556.

Choose **A** unless you have a reason to use the proxy.

---

## Part 1: Prepare Windows Server (both setups)

1. Sign in to the server as an Administrator.
2. Install the **.NET 8 SDK** from <https://dotnet.microsoft.com/download/dotnet/8.0>. You only need this on the machine where you build. If you build on another PC and copy the output, skip this step.
3. Build a self-contained copy of the headless server. It runs as a service and needs no installed runtime. Open **PowerShell** in the repository folder:
   ```powershell
   dotnet publish src\SupGalaxyServer.Headless -c Release -r win-x64 --self-contained true -o C:\SupGalaxyServer\app
   ```
   If you prefer the GUI, publish it the same way:
   ```powershell
   dotnet publish src\SupGalaxyServer.Gui -c Release -r win-x64 --self-contained true -o C:\SupGalaxyServer\gui
   ```
   The GUI needs a logged-in desktop. For an always-on server, use the headless host as a service (Part 5). Both read the same `settings.json` when you point them at the same `--data` folder.
4. Create the data and certificate folders:
   ```powershell
   New-Item -ItemType Directory -Force C:\SupGalaxyServer\data, C:\SupGalaxyServer\cert
   ```
5. Run the server once to create `settings.json`. Type `quit` to stop it.
   ```powershell
   C:\SupGalaxyServer\app\SupGalaxyServer.Headless.exe --data C:\SupGalaxyServer\data
   ```
6. Open the Windows Firewall ports in an **elevated PowerShell**. For setup B, use your chosen port (for example 8443) instead of 55555.
   ```powershell
   New-NetFirewallRule -DisplayName "SupGalaxy signaling (TCP)" -Direction Inbound -Protocol TCP -LocalPort 55555 -Action Allow
   New-NetFirewallRule -DisplayName "SupGalaxy players (UDP)"   -Direction Inbound -Protocol UDP -LocalPort 55556-56556 -Action Allow
   ```
7. Open or forward the same ports **outside** Windows:
   - **Home or office router:** forward TCP 55555 and UDP 55556-56556 to the server's LAN IP.
   - **Cloud VM (Azure, AWS, and so on):** add inbound rules for the same ports to the network security group or security group.
8. Find your public IP with `(Invoke-RestMethod https://api.ipify.org)`. You need it in Parts 2 and 4.

## Part 2: Create the DNS record in Cloudflare (both setups)

1. Log in at <https://dash.cloudflare.com> and select your domain (`example.com`).
2. Go to **DNS → Records → Add record**.
3. Set:
   - **Type:** `A`
   - **Name:** `play`
   - **IPv4 address:** `203.0.113.10`
   - **Proxy status:** **DNS only (grey cloud)** for setup A, or **Proxied (orange cloud)** for setup B
   - **TTL:** Auto
4. Click **Save**.
5. Check the record from the server with `Resolve-DnsName play.example.com`.
   - Setup A should return `203.0.113.10`.
   - Setup B returns Cloudflare IPs, which is expected.

Now go to **Part 3A** or **Part 3B**.

---

## Part 3A: Let's Encrypt certificate with win-acme and Cloudflare DNS (DNS only setup)

win-acme proves you own the domain by creating a temporary DNS TXT record through the Cloudflare API (DNS-01 validation). It does not need port 80 or 443 to be open.

### 3A.1 Create a Cloudflare API token

1. In Cloudflare, click your profile icon → **My Profile → API Tokens → Create Token**.
2. Next to **Edit zone DNS**, click **Use template**.
3. Under **Zone Resources**, select **Include → Specific zone → example.com**.
4. Click **Continue to summary → Create Token**.
5. **Copy the token now.** Cloudflare shows it only once. Keep it secret.

### 3A.2 Install win-acme and its Cloudflare plugin

1. Download the latest **win-acme** `x64 pluggable` zip from <https://www.win-acme.com/> (GitHub releases of `win-acme/win-acme`).
2. Extract it to `C:\win-acme`.
3. From the same release, download the **Cloudflare validation plugin** (`plugin.validation.dns.cloudflare.v2.x.x.zip`).
4. Extract the plugin's files **into the same folder** (`C:\win-acme`).

### 3A.3 Request the certificate as a password-protected .pfx

1. Choose a strong password for the .pfx. The example uses `ChangeMe-PfxPassword`.
2. Create the script that restarts the server after every renewal. The server loads the certificate once, at start.
   ```powershell
   Set-Content C:\SupGalaxyServer\restart.ps1 'Restart-Service SupGalaxyServer -ErrorAction SilentlyContinue'
   ```
3. Request the certificate (one line, elevated PowerShell):
   ```powershell
   C:\win-acme\wacs.exe --source manual --host play.example.com --validation cloudflare --cloudflareapitoken "<YOUR_TOKEN>" --store pfxfile --pfxfilepath C:\SupGalaxyServer\cert --pfxpassword "ChangeMe-PfxPassword" --installation script --script C:\SupGalaxyServer\restart.ps1 --accepttos --emailaddress you@example.com
   ```
   Option names can change between win-acme versions. If this command is rejected, run `C:\win-acme\wacs.exe` with no arguments and answer the menu instead:
   1. **M** (create certificate, full options) → **Manual input** → `play.example.com`.
   2. Validation: **Create verification records with Cloudflare** → paste the token.
   3. Key type: **RSA**.
   4. Store: **PFX archive** → folder `C:\SupGalaxyServer\cert` → enter the password.
   5. Installation: **Start external script or program** → `C:\SupGalaxyServer\restart.ps1`.
4. Check the result:
   - The command should end with `Certificate ... created`.
   - `C:\SupGalaxyServer\cert` now contains a file like `play.example.com.pfx`. Note its exact name.
   - win-acme has created a **Windows Task Scheduler** task that renews the certificate automatically about every 60 days. The task calls `restart.ps1` after each renewal.

Continue with **Part 4**.

---

## Part 3B: Cloudflare Origin certificate (proxied setup)

1. In Cloudflare, open your domain → **SSL/TLS → Overview**. Set the encryption mode to **Full (strict)**.
2. Go to **SSL/TLS → Origin Server → Create Certificate**.
3. Set:
   - Key type: **RSA (2048)**
   - Host names: `play.example.com`
   - Validity: as long as you like (up to 15 years)
4. Click **Create**.
5. Copy the two text boxes into files on the server:
   - **Origin Certificate** → `C:\SupGalaxyServer\cert\origin.pem`
   - **Private Key** → `C:\SupGalaxyServer\cert\origin.key`. Cloudflare shows the key only once.
6. Convert the files to a .pfx. Use **one** of these options:
   - **OpenSSL.** It is included with Git for Windows: `C:\Program Files\Git\usr\bin\openssl.exe`.
     ```powershell
     & "C:\Program Files\Git\usr\bin\openssl.exe" pkcs12 -export -out C:\SupGalaxyServer\cert\origin.pfx -inkey C:\SupGalaxyServer\cert\origin.key -in C:\SupGalaxyServer\cert\origin.pem -passout pass:ChangeMe-PfxPassword
     ```
   - **certutil (built into Windows).** The key file must have the same base name as the certificate file and the extension `.key`:
     ```powershell
     Copy-Item C:\SupGalaxyServer\cert\origin.pem C:\SupGalaxyServer\cert\origin.cer
     certutil -p "ChangeMe-PfxPassword" -MergePFX C:\SupGalaxyServer\cert\origin.cer C:\SupGalaxyServer\cert\origin.pfx
     ```
7. Delete `origin.key` once the .pfx exists, or restrict access to it (see Part 6).
8. Pick a signaling port that Cloudflare proxies: **8443** (or 443, 2053, 2083, 2087, 2096).
   - Use the same port in the firewall rule (Part 1, step 6) and in `SignalingPort` (Part 4).
   - The player UDP range stays 55556-56556, opened directly to the server.
9. Under **Security → Bots**, make sure **Bot Fight Mode** is off for this host, or add a WAF skip rule for `play.example.com`. A browser challenge would block the game's `POST /connect` request.
10. Players connect with `play.example.com:8443`. The certificate is trusted only through Cloudflare, so `https://203.0.113.10:8443` shows a certificate warning. That is expected.

---

## Part 4: Point the server at the certificate (both setups)

1. Stop the server (or the service, see Part 5).
2. Edit `C:\SupGalaxyServer\data\settings.json`:
   - Use **double backslashes** in paths.
   - Keep the other values as they are.
   - For setup B, change `"SignalingPort"` to `8443` and use `origin.pfx`.
   ```json
   {
     "ServerName": "My SupGalaxy Server",
     "BindAddress": "0.0.0.0",
     "SignalingPort": 55555,
     "PlayerPortStart": 55556,
     "PlayerPortEnd": 56556,
     "PublicAddress": "203.0.113.10",
     "IceServers": [ "stun:supturn.com:3478" ],
     "SaveIntervalMinutes": 10,
     "CertificatePath": "C:\\SupGalaxyServer\\cert\\play.example.com.pfx",
     "CertificatePassword": "ChangeMe-PfxPassword",
     "MaxMessageSize": 524288,
     "MaxMessagesPerSecond": 600
   }
   ```
   - `PublicAddress` must be the public **IP** that players reach for UDP 55556-56556. Set it whenever the server is behind NAT (home router, Azure, AWS, and so on).
3. Start the server in a console to check it:
   ```powershell
   C:\SupGalaxyServer\app\SupGalaxyServer.Headless.exe --data C:\SupGalaxyServer\data
   ```
   The first log line should say `listening on https://127.0.0.1:55555, https://0.0.0.0:55555 ...`. If the certificate path or password is wrong, the server exits at start with an exception that names the problem (file not found, or the password is not correct).
4. Test from another computer (not the server itself):
   ```powershell
   Test-NetConnection play.example.com -Port 55555          # TcpTestSucceeded : True
   curl.exe https://play.example.com:55555/info             # JSON with name, protocol, players ...
   ```
   Also open `https://play.example.com:55555/info` in a browser. It should load without a certificate warning (setup B: `:8443`).
5. Type `quit` to stop the console server, then install it as a service.

Local testing still works. The server always listens on `127.0.0.1` too, but once a certificate is configured that listener also uses HTTPS. The certificate is for `play.example.com`, so `https://127.0.0.1:55555` shows a name warning. To test locally without a certificate, use a separate data folder without `CertificatePath` (`--data C:\SupGalaxyServer\localtest`).

## Part 5: Run as a Windows service (always on)

The headless host keeps running when stdin is closed, and it saves cleanly on Ctrl+C or a stop request. That makes it a good fit for **NSSM** (Non-Sucking Service Manager).

1. Download NSSM from <https://nssm.cc/download> and copy `win64\nssm.exe` to `C:\SupGalaxyServer\`.
2. In an elevated PowerShell, install and configure the service:
   ```powershell
   cd C:\SupGalaxyServer
   .\nssm.exe install SupGalaxyServer C:\SupGalaxyServer\app\SupGalaxyServer.Headless.exe "--data C:\SupGalaxyServer\data"
   .\nssm.exe set SupGalaxyServer AppDirectory C:\SupGalaxyServer\app
   .\nssm.exe set SupGalaxyServer Start SERVICE_AUTO_START
   .\nssm.exe set SupGalaxyServer AppStdout C:\SupGalaxyServer\server.log
   .\nssm.exe set SupGalaxyServer AppStderr C:\SupGalaxyServer\server.log
   .\nssm.exe set SupGalaxyServer AppStopMethodConsole 15000
   .\nssm.exe start SupGalaxyServer
   ```
   - **Always pass `--data`.** The service runs as LocalSystem, whose `%LOCALAPPDATA%` is not your user's folder.
   - `AppStopMethodConsole 15000` sends Ctrl+C and gives the server 15 seconds to save the world before it is stopped.
3. Check it: `Get-Service SupGalaxyServer` should show `Running`, and `C:\SupGalaxyServer\server.log` should show the listeners. Repeat the tests in Part 4, step 4.
4. **Alternative without NSSM:** use Task Scheduler.
   1. Create a task with **Run whether user is logged on or not**.
   2. Set the trigger to **At startup**.
   3. Set the action to `C:\SupGalaxyServer\app\SupGalaxyServer.Headless.exe` with arguments `--data C:\SupGalaxyServer\data`.
   4. Uncheck **Stop the task if it runs longer than**.
   5. Change `restart.ps1` to: `Stop-ScheduledTask SupGalaxyServer; Start-ScheduledTask SupGalaxyServer`.

   Note: stopping a scheduled task ends the process without the save on exit. You only lose changes made since the last 10-minute incremental save.

## Part 6: Lock down and maintain

1. Restrict the certificate folder to Administrators and SYSTEM:
   ```powershell
   icacls C:\SupGalaxyServer\cert /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F"
   ```
2. Restrict `settings.json` the same way, because it contains the .pfx password:
   ```powershell
   icacls C:\SupGalaxyServer\data\settings.json /inheritance:r /grant:r "Administrators:F" "SYSTEM:F"
   ```
3. **Renewal.**
   - **Setup A:** win-acme renews automatically and runs `restart.ps1`. Players reconnect within seconds, and the world is saved on the restart. Test renewal once with `C:\win-acme\wacs.exe --renew --force`.
   - **Setup B:** nothing to renew until the Origin certificate expires.
4. **Changing the public IP:** update the Cloudflare `A` record **and** `PublicAddress` in `settings.json`, then restart the service.
5. **Troubleshooting.**

| Symptom | Fix |
|---|---|
| `/info` works but players never connect (ICE fails) | UDP 55556-56556 is blocked by Windows Firewall, the router or the cloud firewall, or `PublicAddress` is missing or wrong |
| Browser: `ERR_CERT_COMMON_NAME_INVALID` | The player typed an IP or another name. They must use `play.example.com` |
| Browser: mixed content blocked | The client used `http://` for a remote host. Use the host name so the client chooses `https://` |
| Cloudflare error 521/522 (setup B) | The service is not running, or the signaling port is not 443/2053/2083/2087/2096/8443, or TCP is blocked |
| Cloudflare error 526 (setup B) | SSL mode is Full (strict) but the server is not using the Origin certificate, or the .pfx is for another host name |
| Server exits at start with a certificate error | Wrong `CertificatePath` (check the double backslashes) or wrong `CertificatePassword` |
