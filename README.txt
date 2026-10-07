ZERO HOUR PHOTON CRAWLER
========================

WHAT IT DOES
Logs in to the game's servers the way the game does (with the Steam ticket of a dedicated account that owns
Zero Hour), then loops: look at a region's room list, join a competitive match that is already in its later
rounds, stay quietly until the match ends, record every player's kills, deaths, damage, score, team and
win/loss, leave, and move on. Each finished match is saved in the "matches" folder and, if an upload address
and key are set, sent to the bot. It moves nothing and takes no game actions, other than optionally announcing
itself in room chat on join/leave (see JOIN/LEAVE CHAT ANNOUNCEMENT below).

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

JOIN/LEAVE CHAT ANNOUNCEMENT (real feature)
 Set "JoinChatMessage" and/or "LeaveChatMessage" in config.json to have the crawler say something in room chat
 shortly after joining and/or right before leaving. Leave either blank to skip that announcement. The message
 fades after "ChatMessageDisplaySeconds" (default 8). "LeaveMessageDelaySeconds" (default 3) controls how long
 the crawler stays connected after sending the leave message before it actually leaves the room - needed because
 leaving instantly destroys everything the crawler owns, including the chat bubble.

DIAGNOSTIC TOOLS (for investigating new protocol details, e.g. after a game update changes something)
 These are all off/blank by default and should stay that way for normal runs.
 - "CaptureUnknownEvents": true logs every event code the crawler doesn't already understand, in full, to
   chat_capture.log. Other real players' content is never recorded (code/size/sender only); only the crawler's
   own events, or the one display name in "CaptureTrustedPlayer" (case-insensitive), are logged unredacted.
   Use this briefly in a private test room with one deliberate test message, then turn it back off.
 - "SendTestChatMessage": "<text>" sends that text as a one-off chat message a few seconds after joining, using
   the same mechanism as the real JoinChatMessage/LeaveChatMessage feature. Useful for testing a chat-sending
   change in isolation without running a full join/leave cycle. Test in an empty or private room first.

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
