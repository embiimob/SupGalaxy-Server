# SSL for SupGalaxy-Server with GoDaddy DNS and Windows Server

SupGalaxy runs on an `https://` page. Browsers block calls from an `https://` page to plain `http://` addresses, except `127.0.0.1` and `localhost`. So players on the internet can only reach your server if the signaling port (default **TCP 55555**) serves HTTPS with a trusted certificate for the host name they type.

This guide assumes your domain's DNS is managed at **GoDaddy**. Replace the example values below with your own.

| Placeholder | Example |
|---|---|
| Domain (DNS at GoDaddy) | `example.com` |
| Server host name | `play.example.com` |
| Server public IPv4 | `203.0.113.10` |
| Install folder | `C:\SupGalaxyServer` |
| .pfx password | `ChangeMe-PfxPassword` |

## Which certificate to use

| | **A. Let's Encrypt via win-acme (recommended)** | **B. Certificate bought from GoDaddy** |
|---|---|---|
| Cost | free | paid, yearly |
| Validation | win-acme answers an HTTP check on **TCP 80** of the server | GoDaddy checks the domain (DNS TXT record or email) |
| Renewal | automatic, about every 60 days, with a server restart | manual, yearly: repeat Part 3B |

GoDaddy's DNS API is not available to most accounts, so this guide does not use DNS-API automation. Option A needs **TCP port 80** to reach the server, but only for the few seconds of each issue or renewal.

Two points apply in every setup:
- The certificate only protects the short `POST /connect` request on TCP 55555. The game data goes over WebRTC (UDP 55556-56556), which is always encrypted (DTLS) and needs no certificate.
- Players must type the **host name** (`play.example.com`), not the IP. The certificate is only valid for the name.

---

## Part 1: Prepare Windows Server

1. Sign in to the server as an Administrator.
2. Install the **.NET 8 SDK** from <https://dotnet.microsoft.com/download/dotnet/8.0>. You only need it on the machine where you build.
3. Open PowerShell in the repository folder and publish a self-contained copy of the headless server. It runs as a service and needs no installed runtime.
   ```powershell
   dotnet publish src\SupGalaxyServer.Headless -c Release -r win-x64 --self-contained true -o C:\SupGalaxyServer\app
   ```
   The GUI can be published the same way (`src\SupGalaxyServer.Gui`, output `C:\SupGalaxyServer\gui`). It needs a logged-in desktop. For an always-on server, use the headless host as a service (Part 5). Both programs use the same `settings.json` when started with the same `--data` folder.
4. Create the folders:
   ```powershell
   New-Item -ItemType Directory -Force C:\SupGalaxyServer\data, C:\SupGalaxyServer\cert
   ```
5. Run the server once to create `settings.json`, then type `quit`:
   ```powershell
   C:\SupGalaxyServer\app\SupGalaxyServer.Headless.exe --data C:\SupGalaxyServer\data
   ```
6. Open the Windows Firewall in an **elevated PowerShell**. Rule 3 is only needed for option A.
   ```powershell
   New-NetFirewallRule -DisplayName "SupGalaxy signaling (TCP)" -Direction Inbound -Protocol TCP -LocalPort 55555 -Action Allow
   New-NetFirewallRule -DisplayName "SupGalaxy players (UDP)"   -Direction Inbound -Protocol UDP -LocalPort 55556-56556 -Action Allow
   New-NetFirewallRule -DisplayName "ACME HTTP validation"      -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
   ```
7. Open or forward the same ports **outside** Windows:
   - **Home or office router:** forward TCP 55555, UDP 55556-56556 and (for option A) TCP 80 to the server's LAN IP. Give the server a fixed LAN IP or a DHCP reservation.
   - **Cloud VM (Azure, AWS, and so on):** add inbound rules for the same ports to the network security group or security group.
8. Find your public IP: `Invoke-RestMethod https://api.ipify.org`. You need it in Parts 2 and 4.

## Part 2: Create the DNS record at GoDaddy

1. Sign in at <https://www.godaddy.com>, then open **My Products** (or **Domain Portfolio**).
2. Next to `example.com`, click **DNS** (or **⋯ → Edit DNS**). This opens the **DNS Records** tab.
3. If a record named `play` already exists (type A, AAAA or CNAME), edit or delete it, so only the new record answers for that name.
4. Click **Add New Record** and set:
   - **Type:** `A`
   - **Name:** `play` (do not include the domain)
   - **Value:** `203.0.113.10`
   - **TTL:** `600 seconds`, or **Custom → 600** (the lowest GoDaddy allows)
5. Click **Save**. If GoDaddy asks you to confirm the change, confirm it.
6. Wait a few minutes, then check from the server:
   ```powershell
   Resolve-DnsName play.example.com -Server 8.8.8.8      # must return 203.0.113.10
   ```
   Do not continue until this returns your IP. Certificate validation fails otherwise.
7. Leave the record as A-only. Do not add an `AAAA` (IPv6) record for `play` unless the server is really reachable over IPv6 on all the ports above.
8. Optional: if the server's public IP changes (home connections), use a static IP from your ISP. Otherwise you must update this record whenever the IP changes (Part 6).

If your domain's nameservers are not GoDaddy's (the DNS page says *"We can't display your DNS information because your nameservers aren't managed by us"*), add the same A record at whichever provider the nameservers point to.

Now do **Part 3A** or **Part 3B**.

---

## Part 3A: Free Let's Encrypt certificate with win-acme (recommended)

win-acme ("WACS") gets the certificate and saves it as a password-protected `.pfx` file. It creates a scheduled task that renews the certificate automatically. With the **self-hosting** validation, win-acme briefly answers Let's Encrypt on port 80 itself. IIS is not needed.

1. Make sure nothing else uses TCP 80. Check with `Get-NetTCPConnection -LocalPort 80 -State Listen`.
   - If IIS is installed and uses port 80, stop it during issue with `iisreset /stop`.
   - Better: choose the **filesystem** validation for its `wwwroot` (`--validation filesystem --webroot C:\inetpub\wwwroot`).
2. Download the latest **win-acme x64 trimmed** zip from <https://www.win-acme.com/> (or GitHub `win-acme/win-acme` releases) and extract it to `C:\win-acme`.
3. Create the script that restarts the server after each renewal. The server loads the certificate only when it starts.
   ```powershell
   Set-Content C:\SupGalaxyServer\restart.ps1 'Restart-Service SupGalaxyServer -ErrorAction SilentlyContinue'
   ```
4. Request the certificate (one line, elevated PowerShell):
   ```powershell
   C:\win-acme\wacs.exe --source manual --host play.example.com --validation selfhosting --store pfxfile --pfxfilepath C:\SupGalaxyServer\cert --pfxpassword "ChangeMe-PfxPassword" --installation script --script C:\SupGalaxyServer\restart.ps1 --accepttos --emailaddress you@example.com
   ```
   If your win-acme version rejects an option, run `C:\win-acme\wacs.exe` without options and use the menu:
   1. **M** (full options) → **Manual input** → `play.example.com`.
   2. Validation: **Serve verification files from memory** (self-hosting).
   3. Key type: **RSA**.
   4. Store: **PFX archive** → path `C:\SupGalaxyServer\cert` → password.
   5. Installation: **Start external script or program** → `C:\SupGalaxyServer\restart.ps1`.
5. Check the result:
   - win-acme should report that the certificate was created.
   - `C:\SupGalaxyServer\cert` now contains a file like `play.example.com.pfx`. Note its exact name for Part 4.
   - A Task Scheduler task called *win-acme renew* now exists.
6. If validation fails, the usual causes are:
   - The `play` A record does not point to this server yet (Part 2, step 6).
   - TCP 80 is blocked by the router or cloud firewall (Part 1, step 7).
   - Another program is using port 80 (step 1).

Continue with **Part 4**.

---

## Part 3B: Certificate bought from GoDaddy

1. Buy an SSL certificate in your GoDaddy account (**SSL Certificates**). A single-domain "DV" certificate is enough.
2. Create a key and certificate request (CSR) **on the server**, so the private key never leaves it.
   1. Save this as `C:\SupGalaxyServer\cert\request.inf`:
      ```ini
      [NewRequest]
      Subject = "CN=play.example.com"
      KeyLength = 2048
      KeyAlgorithm = RSA
      Exportable = TRUE
      MachineKeySet = TRUE
      RequestType = PKCS10
      [Extensions]
      2.5.29.17 = "{text}"
      _continue_ = "dns=play.example.com"
      ```
   2. Create the request:
      ```powershell
      cd C:\SupGalaxyServer\cert
      certreq -new request.inf request.csr
      ```
3. In GoDaddy, open the certificate → **Set up** / **Manage** → paste the full contents of `request.csr` (including the `BEGIN/END` lines).
   - For domain: `play.example.com`
   - Submit.
4. Prove domain control.
   - Choose the **DNS** option: GoDaddy adds the TXT record automatically when the domain is in the same account. Otherwise add the TXT record they show on the DNS page from Part 2.
   - Wait for the **Issued** status.
5. Download the certificate. For server type, choose **IIS** (or **Other**). Unzip it into `C:\SupGalaxyServer\cert`. It contains your certificate (`xxxxxxxx.crt`) and the GoDaddy chain (`gd_bundle-g2*.p7b` or `.crt`).
6. Join the certificate with the private key that `certreq` kept in the machine store, then export a `.pfx`:
   ```powershell
   cd C:\SupGalaxyServer\cert
   certreq -accept xxxxxxxx.crt
   $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq "CN=play.example.com" | Sort-Object NotAfter -Descending | Select-Object -First 1
   $pfxPass = Read-Host "PFX password" -AsSecureString
   Export-PfxCertificate -Cert $cert -FilePath C:\SupGalaxyServer\cert\play.example.com.pfx -Password $pfxPass -ChainOption BuildChain
   ```
   If `certreq -accept` reports a missing chain, first import the GoDaddy bundle: `Import-Certificate -FilePath .\gd_bundle-g2.crt -CertStoreLocation Cert:\LocalMachine\CA`.
7. Write the expiry date in your calendar. Repeat this part (with a new CSR) before the certificate expires, then restart the service.

---

## Part 4: Point the server at the certificate

1. Stop the server or service.
2. Edit `C:\SupGalaxyServer\data\settings.json`. Keep the other values, and use **double backslashes** in paths.
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
   `PublicAddress` is the public **IP** that players reach on UDP 55556-56556. Set it whenever the server is behind NAT (home router, Azure, AWS, and so on).
3. Start the server in a console to check it:
   ```powershell
   C:\SupGalaxyServer\app\SupGalaxyServer.Headless.exe --data C:\SupGalaxyServer\data
   ```
   - The first log line should read `listening on https://127.0.0.1:55555, https://0.0.0.0:55555 ...`.
   - A wrong path or password stops the server at start with an exception that names the problem.
4. Test from **another** computer:
   ```powershell
   Test-NetConnection play.example.com -Port 55555      # TcpTestSucceeded : True
   curl.exe https://play.example.com:55555/info         # JSON with name, protocol, players ...
   ```
   Also open `https://play.example.com:55555/info` in a browser. It must load without a certificate warning.
5. Type `quit`, then install the service (Part 5).

For local testing, the server always listens on `127.0.0.1` too, but with a certificate configured that listener also uses HTTPS. That gives a name warning, because the certificate is for `play.example.com`. To test locally without a certificate, start a second copy with a separate data folder that has no `CertificatePath`, for example `--data C:\SupGalaxyServer\localtest --port 55600 --players 55602-55700`.

## Part 5: Run as a Windows service (always on)

The headless host keeps running when it has no console input. It saves the world when it gets Ctrl+C. **NSSM** turns it into a service.

1. Download NSSM from <https://nssm.cc/download> and copy `win64\nssm.exe` to `C:\SupGalaxyServer\`.
2. In an elevated PowerShell:
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
   - **Always pass `--data`.** The service runs as LocalSystem, whose `%LOCALAPPDATA%` is not your user folder.
   - `AppStopMethodConsole 15000` sends Ctrl+C and allows 15 seconds for the final save.
3. Check that `Get-Service SupGalaxyServer` shows `Running` and that `C:\SupGalaxyServer\server.log` shows the `https://` listeners. Repeat Part 4, step 4.

If you can't use NSSM, you can use Task Scheduler instead:
1. Create a task: **Run whether user is logged on or not**, trigger **At startup**.
2. Action: `C:\SupGalaxyServer\app\SupGalaxyServer.Headless.exe`, arguments `--data C:\SupGalaxyServer\data`.
3. Uncheck **Stop the task if it runs longer than**.
4. Change `restart.ps1` to `Stop-ScheduledTask SupGalaxyServer; Start-ScheduledTask SupGalaxyServer`.

Stopping a task ends the process without the final save, so changes since the last 10-minute save are lost.

## Part 6: Lock down and maintain

1. Allow only Administrators and SYSTEM to read the certificate files and `settings.json`, which contains the .pfx password:
   ```powershell
   icacls C:\SupGalaxyServer\cert /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F"
   icacls C:\SupGalaxyServer\data\settings.json /inheritance:r /grant:r "Administrators:F" "SYSTEM:F"
   ```
2. **Renewal**
   - **Option A:** win-acme renews automatically and runs `restart.ps1`. Players reconnect within seconds, and the world is saved during the restart. Test renewal once with `C:\win-acme\wacs.exe --renew --force`. Keep TCP 80 forwarded so renewals keep working.
   - **Option B:** repeat Part 3B every year.
3. **Changing the public IP:** edit the `play` A record at GoDaddy (Part 2) **and** `PublicAddress` in `settings.json`, then `Restart-Service SupGalaxyServer`.
4. **Troubleshooting**

| Symptom | Fix |
|---|---|
| `/info` works but players never connect (ICE fails) | UDP 55556-56556 is blocked by Windows Firewall, the router or the cloud firewall, or `PublicAddress` is missing or wrong |
| Browser shows `ERR_CERT_COMMON_NAME_INVALID` | The player typed an IP or another name; use `play.example.com` |
| Browser shows `ERR_CERT_AUTHORITY_INVALID` (option B) | The .pfx lacks the GoDaddy chain; import `gd_bundle` and export again with `-ChainOption BuildChain` |
| Browser blocks mixed content | The client used `http://` for a remote host; the address must be the host name so the client uses `https://` |
| `Resolve-DnsName` returns an old IP | Wait for the TTL (up to 10 min with TTL 600; longer if the old TTL was 1 hour) |
| win-acme validation fails | DNS not updated yet, TCP 80 not forwarded, or port 80 in use |
| Server exits at start with a certificate error | Wrong `CertificatePath` (check the double backslashes) or wrong `CertificatePassword` |
