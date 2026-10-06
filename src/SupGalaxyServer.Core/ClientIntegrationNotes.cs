// =====================================================================================================================
//  NOTES FOR AI AGENTS: HOW TO CHANGE THE SUPGALAXY CLIENT (https://github.com/embiimob/SupGalaxy) TO USE THIS SERVER
// =====================================================================================================================
//
//  This file holds no runtime logic. It is the specification an AI (or a human) should follow when changing the browser
//  game so it can connect to this dedicated server. Read all of it before editing the client. The client
//  files referenced below are js/web-rtc.js, js/main.js and js/declare.js in the SupGalaxy repository.
//
//  ---------------------------------------------------------------------------------------------------------------
//  1. WHAT THE SERVER IS
//  ---------------------------------------------------------------------------------------------------------------
//  * An always-on WebRTC "host" peer. In the existing client, a player hosts and the others send offer files
//    through Sup!?/IPFS and wait for an answer to be polled back. This server replaces that slow path. The client
//    POSTs the same offer JSON over HTTP and receives the answer in the response, usually in under a second.
//  * Star topology: every player has ONE RTCPeerConnection, to the server. It carries ONE data channel
//    (the existing "game" channel). The server relays game messages between players, scoped to each player's
//    world, the same way the player host relays them today.
//  * Signaling listener: http://<host>:55555 by default (the "main port"). It always listens on 127.0.0.1 too,
//    so local testing works with http://127.0.0.1:55555.
//  * Each player is given one UDP port from the player range (default 55556-56556) for its WebRTC traffic.
//    SIPSorcery needs even RTP ports, so the default range holds 501 players.
//  * The server keeps a copy of world edits (chunkDeltas, foreignBlockOrigins, magician/calligraphy stones) and
//    saves them to disk every 10 minutes. It sends that state to every player who enters a world.
//  * The server RUNS THE GAME RULES for every active world (Rules/WorldRules.cs). Players cannot be trusted (a
//    modified client could run other rules), so no client is "host" and no client decides anything that changes the
//    world. The server ports the client's isHost code paths: block breaking (tool / laser damage, block strength,
//    drops, the leaf tree seed bonus), block placing, doors, chunk ownership (home spawn chunks and edit claims),
//    tree seed growth, fish spawn commands, PvP melee damage and range, lava damage and volcano eruptions. It knows
//    the terrain because it runs a bit-identical C# port of js/worker.js generateChunkData.
//  * Still client-side (the server cannot verify these): mob AI (it runs on the client that spawned the mob, the
//    server only enforces that nobody else moves/kills it), each player's own inventory contents, health, crafting
//    and score. Chunk claims that exist only on IPFS/Sup!? are not known to the server; claims made on the server
//    are kept in its save files.
//
//  ---------------------------------------------------------------------------------------------------------------
//  2. HTTP API (main port, CORS enabled for any origin)
//  ---------------------------------------------------------------------------------------------------------------
//  GET  /info    -> { name, software, protocol, players, maxPlayers, playerPortStart, playerPortEnd }
//                   Use it to show "server online, N/M players" next to the Connect button.
//  GET  /health  -> { status: "ok", players }
//  POST /connect  body (exactly the object connectToServer() already builds for the offer file):
//                   { "world": worldName, "user": userName, "offer": { "type": "offer", "sdp": "..." },
//                     "iceCandidates": [ RTCIceCandidateInit, ... ] }
//                 200 -> { ok: true, serverName, protocol, user, world, port,
//                          answer: { type: "answer", sdp: "..." }, iceCandidates: [ RTCIceCandidateInit, ... ] }
//                 Errors return { ok: false, code, error } (error = readable message), status/code is one of:
//                   400 bad_request         invalid username / world / SDP
//                   403 blocked             the server admin has blocked this username
//                   409 name_in_use         this username is already connected (one session per name,
//                                           case-insensitive). Tell the user, do not retry automatically.
//                   503 server_full         no free player port
//                   500 negotiation_failed
//  Mixed content: a page served over https:// may call http://127.0.0.1 or http://localhost, but browsers block
//  plain http:// to any other host. Remote servers must be set up with a certificate (CertificatePath in
//  settings.json) and reached over https://host:55555.
//
//  ---------------------------------------------------------------------------------------------------------------
//  3. CLIENT CHANGE: "Connect to Server" BUTTON  (js/web-rtc.js + index.html)
//  ---------------------------------------------------------------------------------------------------------------
//  Add a button near the existing host/join controls. It asks for "host[:port]" (default port 55555; remember the
//  last value in localStorage) and calls connectToDedicatedServer(address). Suggested implementation:
//
//      var dedicatedServer = null;          // { name, base } while connected to a server
//      const SERVER_PEER = "@server";       // key in `peers`. Not a valid SupGalaxy username, so it cannot collide.
//
//      async function connectToDedicatedServer(address) {
//          const base = /^https?:\/\//.test(address) ? address.replace(/\/+$/, "")
//                     : (/^(127\.|localhost)/.test(address) ? "http://" : "https://") + address
//                       + (/:\d+$/.test(address) ? "" : ":55555");
//          const pc = new RTCPeerConnection({ iceServers: await getTurnCredentials() });
//          // DO NOT add microphone/camera tracks to this connection (see section 6).
//          const dc = pc.createDataChannel("game");
//          dedicatedServer = { name: address, base };
//          peers.set(SERVER_PEER, { pc, dc, address: null });
//          setupDataChannel(dc, SERVER_PEER);      // reuse the existing handler (with the changes in section 4)
//          const candidates = [];
//          pc.onicecandidate = e => { if (e.candidate) candidates.push(e.candidate.toJSON()); };
//          await pc.setLocalDescription(await pc.createOffer());
//          // Waiting ~1s or for "complete" is enough; the server also accepts candidates embedded in the SDP.
//          await Promise.race([
//              new Promise(r => { pc.onicegatheringstatechange = () => pc.iceGatheringState === "complete" && r(); }),
//              new Promise(r => setTimeout(r, 1000)) ]);
//          const res = await fetch(base + "/connect", { method: "POST", headers: { "Content-Type": "application/json" },
//              body: JSON.stringify({ world: worldName, user: userName, offer: pc.localDescription, iceCandidates: candidates }) });
//          const body = await res.json();
//          if (!res.ok || !body.ok) { pc.close(); peers.delete(SERVER_PEER); dedicatedServer = null;
//              addMessage("Server refused connection: " + (body.error || res.status), 5000); return; }
//          await pc.setRemoteDescription(body.answer);
//          for (const c of body.iceCandidates || []) { try { await pc.addIceCandidate(c); } catch (e) {} }
//          pc.onconnectionstatechange = () => { if (["failed", "closed"].includes(pc.connectionState)) onServerDisconnected(); };
//      }
//
//  When connected to a server, do NOT run offer/answer polling (stopAllPolling()), do NOT become a Sup!? host
//  (activateHost()), and refuse the old connectToServer() flow, since the server already links you to everyone.
//
//  ---------------------------------------------------------------------------------------------------------------
//  4. CLIENT CHANGE: MESSAGE HANDLING  (setupDataChannel in js/web-rtc.js)
//  ---------------------------------------------------------------------------------------------------------------
//  a) In e.onmessage, handle the server_* messages FIRST, before the early `if (n === userName) return;`.
//     server_welcome carries YOUR own username, and that check would drop it.
//       server_welcome   { serverName, username, protocol, players: [names...] }
//                        Connected. The server then sends `new_player` for every player already online. The
//                        existing new_player handler creates avatars and adds `peers` entries with pc:null.
//                        Keep that.
//       server_kick      { reason }   Show the reason, close the connection, do NOT reconnect automatically.
//                        (Kicks and blocks come from the server admin.)
//  b) `world_sync_start` / `world_sync_chunk` are sent by the server in the format sendWorldStateAsync() already
//     uses. The current handlers only run `if (!isHost)`. When connected to a server, apply them ALWAYS (also on
//     They arrive after connecting and whenever you enter a world you have not synced
//     yet on this connection. You can also ask for them again:
//       send { type: "request_world_sync", world: worldName }
//  c) In onopen, when connected to a server, run the existing `if (!isHost) {...}` path (clear world states, then
//     switchWorld). The server acts as the host. Skip the `if (isHost) {...}` block: the server sends new_player and
//     world sync itself.
//  d) Messages that only the server may send are dropped if a client sends them: new_player, remove_peer,
//     world_sync*, renegotiation_offer/answer, server_*, and every game rule result: block_change,
//     batch_block_change, block_place, block_break, block_damaged, block_action_denied, remove_from_inventory, alert,
//     fish_spawn_command, volcano_event, magician_stone_removed, calligraphy_stone_removed, magician_stones_sync.
//     Clients may keep sending block_change after applying a server block_break (setBlockGlobal re-broadcasts); the
//     server ignores those, so that is harmless, but it is cleaner to pass broadcast=false in server mode.
//  e) The server rewrites `username` to the sender's real name. Always set `username: userName` on outgoing
//     messages.
//  f) remove_peer { username } is sent when a player leaves (disconnects, is kicked, or times out after 45s without
//     traffic). Keep sending { type: "i_am_alive" } every 10s, as the client already does.
//
//  ---------------------------------------------------------------------------------------------------------------
//  5. CLIENT CHANGE: SENDING REQUESTS, THE SERVER RUNS THE RULES  (js/web-rtc.js, js/main.js, js/mobs.js)
//  ---------------------------------------------------------------------------------------------------------------
//  * All outgoing game messages go to peers.get(SERVER_PEER).dc. Entries created from new_player have dc:null and
//    must be skipped. Loops like `for (const [, p] of peers) p.dc && p.dc.readyState === "open" && p.dc.send(m)`
//    already do this.
//  * Keep `isHost = false` while connected to a server. Every code path that is `if (isHost || peers.size === 0)`
//    then takes the existing client branch and just sends the request:
//        block_hit / request_block_break { x, y, z, world, toolId?, laserColor?: "red"|"green", isBlue? }
//        request_block_place  { x, y, z, blockId, inventoryBlockId, originSeed, world }
//        request_block_toggle { x, y, z, blockId, world }
//        fish_spawn_request   { x, y, z, fishType: "fish_rare"|"fish_school", originSeed, world }   (integers)
//        fish_spawn_remove    { world, key }
//        player_hit           { target, toolId }
//    Coordinates are integer block coordinates, `world` must be the world you are in (the world of your last
//    player_move); requests for another world are ignored. Hand actions must be within 16 blocks of your last
//    reported position, laser hits within 192 and fish requests within 8, so keep sending player_move.
//  * The server answers exactly like the browser host did, so the existing non-host handlers keep working:
//        to the requester only : add_to_inventory, remove_from_inventory, block_action_denied { reason, chunkKey },
//                                alert { message }, player_damage { damage, attacker, kx, kz }
//        to the whole world    : block_change / batch_block_change (no username, so every client applies it,
//                                including the requester), block_damaged { x, y, z, hits }, block_break, block_place
//                                (these carry the actor's username for sounds/particles; the actor's own client
//                                drops them because of `if (n === userName) return;`, which is fine because the
//                                block_change already applied the block), fish_spawn_command, fish_spawn_remove,
//                                magician_stone_removed, calligraphy_stone_removed, volcano_event { volcano,
//                                eventType, seed, world }, player_damage { attacker: "lava" } (to the burning player).
//  * Remove client-side "I am the host" simulations when connected to a server: do not run manageTreeSeeds,
//    manageVolcanoes or the host lava check (the server does them for every world), and do not send
//    magician_stones_sync / mob_state_batch as a host.
//  * Mobs: keep the existing spawner model (isMobAuthority: mob.spawner === userName, petOwner for wolves). The
//    server remembers the first player that reports a mob id (mob_spawn / mob_update / mob_update_batch) as its
//    owner and drops updates, kills and despawns for that id from anybody else. When the owner disconnects the
//    server sends mob_despawn for its mobs. A successful wolf_tame_result from the owner moves the wolf to `owner`.
//    Do NOT take over orphaned mobs as "host" (the old host did), just let them despawn.
//  * Messages a mob owner sends to ONE player must include `to: "<username>"` (the server delivers a message that
//    has `to` only to that player; peers.get(name).dc is null in server mode):
//        player_damage { to, damage, attacker, kx?, kz? }  damage 0 < d <= 15, attacker must not be "lava",
//                                                         at most one per target every 100 ms
//        add_to_inventory { to, blockId, count, originSeed }  mob loot, count 1..12, a real block id
//    Both are only accepted when the target is in the sender's world.
//  * Magician / calligraphy stones: send magician_stone_placed / calligraphy_stone_placed AFTER the
//    request_block_place for the 127/128 block was confirmed (block_change received). The server only stores and
//    relays stone data if the block at stoneData.x/y/z is that stone and the sender may edit the chunk. Breaking
//    the stone block removes the stone on the server, which then broadcasts *_stone_removed.
//  * Chunk ownership: the server registers every player's home spawn chunk (calculateSpawnPoint(user@world)) when
//    they enter a world, and remembers it in the save. Edits in other chunks create / renew claims exactly like the
//    host handler of request_block_place (1 year, can be taken over during the first 30 days or once expired).
//    The client may keep its own OWNED_CHUNKS for messages, but the server's decision is final.
//
//  ---------------------------------------------------------------------------------------------------------------
//  6. PROXIMITY VOICE / VIDEO CHAT
//  ---------------------------------------------------------------------------------------------------------------
//  The server only relays data. It does not mix or forward audio/video, and it rejects renegotiation_offer/answer.
//  Media goes directly between players who are near each other, using the server only as a signaling relay:
//  * When another player comes within the proximity radius (updateProximityVideo uses 32 blocks) and you have a mic
//    or camera on, create an RTCPeerConnection for that player only (store it in peers.get(name).pc), add your
//    tracks, and exchange SDP/candidates with
//        { type: "p2p_signal", to: "<their name>", username: userName, kind: "offer"|"answer"|"candidate", data }
//    The server delivers p2p_signal (like any message with `to`) only to the named player.
//    To avoid glare, the player whose name sorts lower creates the offer.
//  * Reuse the existing ontrack code (userAudioStreams / userVideoStreams) on those direct connections. Close them
//    when the player leaves proximity or a remove_peer arrives (cleanupPeer).
//  * Keep the TURN servers from getTurnCredentials() for these direct links. Players behind strict NATs need them.
//
//  ---------------------------------------------------------------------------------------------------------------
//  7. TESTING LOCALLY
//  ---------------------------------------------------------------------------------------------------------------
//  Start the server (GUI or `dotnet run --project src/SupGalaxyServer.Headless`), open two browser profiles with
//  different usernames, click "Connect to Server" and enter 127.0.0.1. GET http://127.0.0.1:55555/info should show
//  2 players. In the server GUI, Kick and Block must close the client's session, and a blocked name must get 403
//  on /connect.
// =====================================================================================================================

namespace SupGalaxyServer;

/// <summary>
/// Wire-level constants shared with the SupGalaxy client. See the notes at the top of this file for how the
/// client must be changed.
/// </summary>
public static class ClientProtocol
{
    public const int Version = GameRelay.ProtocolVersion;
    public const string ConnectPath = "/connect";
    public const string InfoPath = "/info";
    public const string HealthPath = "/health";
    public const string DataChannelLabel = "game";

    public const string ServerWelcome = "server_welcome";
    public const string ServerKick = "server_kick";
    public const string RequestWorldSync = "request_world_sync";
    public const string PeerSignal = "p2p_signal";
}
