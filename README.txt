ZERO HOUR PHOTON CRAWLER
========================

WHAT IT DOES
Logs in to the game's servers the way the game does (with the Steam ticket of a dedicated account that owns
Zero Hour), then loops: look at a region's room list, join a competitive match that is already in its later
rounds, stay quietly until the match ends, record every player's kills, deaths, damage, score, team and
win/loss, leave, and move on. Each finished match is saved in the "matches" folder and, if an upload address
and key are set, sent to the bot. It sends no chat, moves nothing and takes no game actions.

ONE-TIME SET UP
1. Install the .NET 8 SDK (free, from Microsoft).
2. From the Photon "Realtime .NET" SDK you downloaded, copy into this folder:
      libs\\Release\\netstandard2.0\\Photon-NetStandard.dll   ->  into a folder named "lib" here
      the whole folder "PhotonLoadbalancingApi"            ->  so that PhotonLoadbalancingApi\\LoadBalancingClient.cs exists
3. Copy steam_api64.dll (from the game's folder, under Zero Hour_Data\\Plugins) into this folder.
   steam_appid.txt (it contains 1359090) is already here.
4. Steam must be running and signed in to the dedicated account, and Windows must not go to sleep.

SETTINGS (config.json)
 - "Crawl" section: the main settings. MinRound (matches from this round on), MinFreeSlots (1 = may take the last
   slot), MaxJoinsPerHour, StopAfterIdleHours (stops if no room is available for that long), MaxHours (0 = no limit),
   Regions, VerboseLog (leave false for long runs).
 - Crawl "PreferDirect" (false by default): how the winners are decided. See "HOW TEAMS AND WINNERS ARE READ" below.
 - Crawl "RpcNumbers": the game's message numbers for team, round-score and match-end messages. Leave them as they are
   unless the game has been updated (see "AFTER A GAME UPDATE").
 - Crawl "UploadUrl" and "ApiKey": leave BLANK while testing (results are only saved in the matches folder).
   When you are happy with the results, set
      "UploadUrl": "http://fi13.bot-hosting.cloud:25305/api/ingest"
   and set "ApiKey" to your upload key (the same value as ZH_INGEST_KEY on the bot host).
 - To use the single-room "peek" instead of the crawler, set Crawl "Enabled" to false and use the JoinRoom section.

HOW TEAMS AND WINNERS ARE READ
 The crawler can tell who won in two ways:
  1. "Bonus method" (the original): after each round the game adds +50 to every player on the winning team and +10 to
     the losing team. Reading those bonuses shows who was on which side and who won.
  2. "Direct method" (new): the game itself announces it. Every player reports their side (blue or red) each round, the
     host sends the round score as (blue wins, red wins), and the sides swap once at halftime. The crawler follows
     those messages. A player who left before the swap is handled (their old side is flipped).
 Right now both are worked out for every match. With PreferDirect = false the record uses the bonus method, and the log
 says whether the two agree:
      CHECK OK: both methods agree on all 8 players (round score blue 5 - red 4).
      CHECK DISAGREE: the methods differ on 2 of 8 players: ...
 Run it for a while and read those lines (search the log for "CHECK"). If they agree on several matches, set
 PreferDirect to true. The direct method is then used whenever it worked, and the bonus method is the fallback.
 Each match file also says which method decided it ("decided_by") and the round score (rounds_blue, rounds_red).
 Checked so far on one recorded match (5-4, the account that lost was read as a loss). Please send me any CHECK DISAGREE line.

GAME MODES (confirmed by M7)
 0 = TDM, 1 = TDM/Hostage, 2 = TDM/Bomb, 3 = DM/FFA (free-for-all).
 The crawler joins modes 0, 1 and 2 (AllowedGameModes in config.json). Mode 3 is left out on purpose: free-for-all has no
 teams, so neither the bonus method nor the direct method can say who won, and the crawler would only occupy a slot.
 Co-op is a separate "Match Type" (BlockedMatchTypes keeps it out). Only mode 2 (TDM/Bomb) has been checked so far;
 in the log, the CHECK lines show whether TDM and TDM/Hostage matches are read correctly too.

CAPTURING A CHAT MESSAGE (for the chat-sending investigation)
 Set "CaptureUnknownEvents": true in config.json. With this on, the crawler fully logs every event code it
 doesn't already understand to chat_capture.log, every time one happens - no truncation, no "first 2 only" limit.
 Your own events are logged in full; anything from another real player is logged as code/size/sender only, never
 their actual message, so a capture session never ends up holding someone else's chat text.
 To use it:
   1. Set CaptureUnknownEvents to true. Also set CaptureTrustedPlayer to your own in-game display name, exactly as
      it appears in the room (case doesn't matter) - this is the one account allowed to have its own content shown
      in full, since the crawler's own actor never sends chat itself. Leave it blank for anyone else: everyone
      else's content stays redacted no matter what.
   2. Start the crawler and join a room - ideally a private, near-empty one, though it still works, with everyone
      but you protected, in a populated room if that is what is available.
   3. From YOUR OWN account (the one named in CaptureTrustedPlayer), send ONE deliberate test chat message,
      something recognisable like "testmsg12345".
   4. Stop the crawler, set CaptureUnknownEvents and CaptureTrustedPlayer back to false/blank, and look at
      chat_capture.log for a line tagged "[trusted test account]" around when you sent it. Send Claude that file
      (or the relevant lines from it).
 Turn this back off afterwards - it's a diagnostic tool for this one investigation, not something to leave on.

SENDING A TEST CHAT MESSAGE (experimental - the crawler's first attempt at actively sending traffic, not just
reading it)
 Set "SendTestChatMessage" in config.json to the exact text you want sent, e.g. "stbtest12345". Leave it blank
 otherwise - this is off by default and should only be turned on for a deliberate test.
 What it does: about 4 seconds after joining a room, it sends that text as a chat message ONCE, using our best
 reconstruction of what a real chat message looks like on the wire (one INSTANTIATE for a "ChatFeed_Prefab" view,
 then an RPC index 197 "ShowChatFeed" call on it, with args [3, <our actor's IDLogger-style view id>, "<text>"]).
 The "3" is copied unchanged from what a real client sent in testing; its exact meaning (possibly a chat-channel
 flag) is not confirmed. The view-id scheme is also inferred from observation, not from the SDK's own allocation
 rules, so it may not be entirely correct.
 IMPORTANT: test this in an empty or private room first, never in a live match with real players, until you have
 confirmed it behaves correctly. Watch the console for a "TEST CHAT SENT" line, then check - from a SEPARATE real
 game client sitting in the same room - whether the message actually appeared, and whether it looked right (right
 text, no visual glitch, nothing else unexpected). If anything looks wrong, Ctrl+C immediately and send Claude the
 console output and what you saw in-game.
 Turn this back off (blank) after testing. It only ever sends once per run, but there is no reason to leave it set.

AFTER A GAME UPDATE
 The game sends each message as a short number: its place in an alphabetical list of the game's multiplayer functions.
 An update that adds or removes such a function shifts the numbers. If the log starts saying "Direct method: no round
 score was received", or the match-end signal stops being noticed, that is the likely cause. To fix it:
   1. Dump the new game version with Il2CppDumper, as before, to get a new dump.cs.
   2. Run    python rpc_numbers.py path\to\dump.cs
   3. Paste the "RpcNumbers" block it prints into the Crawl section of config.json.
 The game's version text ("AppVersion" in config.json) also usually has to be updated after a patch.

RUNNING
   dotnet run
 To stop: press Ctrl+C, or create an empty file named stop.txt in this folder. It leaves the current room first.
 It also stops by itself if no suitable room has been available for 6 hours.

WHAT IT WRITES
 - matches\*.json   one file per recorded match (always, even when uploading)
 - zh_peek.log      a log of what it did (kept small; rolls over to zh_peek.old.log at 20 MB)
 Both contain other players' names and IDs. Keep them private.

IMPORTANT
 - It is untested against many matches. First run it for an hour with uploads OFF and check some of the
   matches files against what you know actually happened.
 - Each visit takes a player slot until the match ends. Ask M7 about slot limits before running it at scale.
 - Do not put your Steam password anywhere. This program never needs it.
