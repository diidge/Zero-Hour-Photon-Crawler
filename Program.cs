// Zero Hour Photon client - lobby scanner, "room peek", and match-recording crawler.
//
//  * Scan mode (ScanAllRegions = true): visits every region and lists the rooms in each lobby.
//  * Join mode (JoinRoom.Name/Match set in config.json): joins ONE room as an ordinary player, sits there
//    writing down what the server sends (players, properties, events), then leaves after StaySeconds.
//    Press Ctrl+C at any time to make it leave right away.
//  * Crawl mode (Crawl.Enabled = true, overrides the above): autonomously visits competitive matches
//    already near their end, records the result, and moves on. See RunCrawler().
//
// It never moves and takes no game actions beyond joining/leaving rooms. It can optionally announce itself
// in room chat on join/leave (JoinChatMessage/LeaveChatMessage below) - that is the one exception to "silent".

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using ExitGames.Client.Photon;
using Photon.Realtime;
using PhotonHashtable = System.Collections.Hashtable;

class Program : IConnectionCallbacks, ILobbyCallbacks, IMatchmakingCallbacks, IInRoomCallbacks, IOnEventCallback
{
    private readonly LoadBalancingClient client = new LoadBalancingClient();
    private readonly Dictionary<string, RoomInfo> rooms = new Dictionary<string, RoomInfo>();
    private bool printedRooms;
    private bool finished;
    private DateTime end;
    private int runSeconds;
    public bool ReachedMaster;

    // join-mode state
    private string joinName = "";
    private string joinMatch = "";      // just the number part, e.g. 596-972-686; the program finds the full room name itself
    private bool searching;
    private int staySeconds = 90;
    public bool JoinedRoom;
    private readonly Dictionary<int, int> eventCounts = new Dictionary<int, int>();
    private readonly Dictionary<int, int> eventDumped = new Dictionary<int, int>();
    private readonly Dictionary<string, int> rpcNames = new Dictionary<string, int>();
    private DateTime lastSummary = DateTime.UtcNow;
    private DateTime lastBoard = DateTime.UtcNow;
    private static int boardSeconds = 30;
    private static bool verbose = true;      // false for long unattended runs: keeps the log small
    private static int fileWrites;
    // crawler / recorder state
    public bool crawlVisit;                 // true when this instance is visiting a room to record its result
    public int crawlMinRound = 6;
    public bool Skipped;                    // joined, but the room no longer qualified, so we left at once
    public Dictionary<string, object> Record;   // the finished-match result, once captured
    public string RecordKey = "";
    private readonly Dictionary<int, string[]> roster = new Dictionary<int, string[]>();   // actor -> {name, id}
    private bool roomClosed, inWindow, endSeen;
    private DateTime closedAt = DateTime.UtcNow, finalizeAt = DateTime.UtcNow;
    private DateTime? endSignalAt;
    private readonly Dictionary<int, long> windowDelta = new Dictionary<int, long>();      // score change per actor in a round-end window
    private readonly List<Dictionary<int, char>> roundClasses = new List<Dictionary<int, char>>();   // per round: actor -> 'W' winner / 'L' loser
    // The game's own team and round-score messages (the "direct" method; the bonus reading above is the "inferred" one)
    private readonly Dictionary<int, char> sideOf = new Dictionary<int, char>();       // actor -> 'B' blue / 'R' red, from their latest report
    private readonly Dictionary<int, int> sideEpoch = new Dictionary<int, int>();      // how many side swaps had happened when they reported
    private int swapCount;                                                             // side swaps seen so far (halftime)
    private int directBlue = -1, directRed = -1;                                       // latest round score: (blue wins, red wins)
    private static bool preferDirect;                                                  // config Crawl.PreferDirect
    // Message numbers in the game's multiplayer list. They are valid for game version 1.07sp2 and change when an update adds
    // or removes such a function. rpc_numbers.py works out the new ones from a fresh dump.cs; they go in config Crawl.RpcNumbers.
    private static int rpcBlue = 23, rpcRed = 24, rpcWins = 27, rpcSwap = 213, rpcEnd = 199, rpcNextMap = 243;
    private static int crawlMinFree = 2;
    private static string crawlUrl = "", crawlKey = "";
    private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private bool leaving;                                              // set when we start leaving, so we never re-join
    private readonly Dictionary<int, string> viewNames = new Dictionary<int, string>();   // view id -> prefab name
    private readonly HashSet<int> watch = new HashSet<int>();          // views of the game's "Log" objects (IDLogger etc.)
    private readonly Dictionary<int, string[]> lastState = new Dictionary<int, string[]>();
    private readonly HashSet<string> rpcSampled = new HashSet<string>();
    private int consoleChanges;
    private static StreamWriter fileLog;

    private static bool traceOn;
    private static string steamHex = "";
    private static string steamId = "";
    private static bool steamReady;
    private static Steamworks.HAuthTicket steamHandle = Steamworks.HAuthTicket.Invalid;
    private static bool scanning;
    private static volatile bool stopRequested;
    private static bool onlyCompetitive;

    // Chat announcement feature (real, shipped): say something in room chat on join and/or just before leaving.
    private static string joinChatMessage = "";
    private static string leaveChatMessage = "";
    private static int leaveMessageDelaySeconds = 3;  // how long we wait, still connected, after sending the leave message

    // Diagnostic-only fields, all off/blank by default. Safe to leave in config.json as "" / false for every
    // normal run; only turn these on deliberately, for a short session, when investigating a new protocol detail.
    private static bool captureUnknownEvents;    // see CaptureUnknownEvents note on HandleInstantiate/CaptureEvent
    private static string captureTrustedPlayer = "";  // see CaptureEvent
    private static string sendTestChatMessage = "";   // see MaybeSendTestChatMessage

    private static readonly HashSet<int> allowedModes = new HashSet<int>();
    private static readonly HashSet<int> blockedTypes = new HashSet<int>();
    public bool Refused;   // found the room, but it is not the kind of match we are allowed to join

    // ---------------------------------------------------------------------------------------------------------
    // A real IDLogger INSTANTIATE carries a field (type code 86, 12 bytes = 3 big-endian floats) that is the
    // player's spawn-position Vector3. Our logger object has no real position, so we register the same custom
    // type code and always send (0,0,0) - what matters is that the field is PRESENT and correctly typed, not
    // its value.
    struct Vec3 { public float X, Y, Z; }
    const byte Vec3TypeCode = 86;
    static bool vec3Registered;

    static void WriteFloatBE(byte[] buf, int offset, float v)
    {
        var b = BitConverter.GetBytes(v);   // little-endian on x86 - reverse it for Photon's big-endian wire format
        buf[offset] = b[3]; buf[offset + 1] = b[2]; buf[offset + 2] = b[1]; buf[offset + 3] = b[0];
    }
    static float ReadFloatBE(byte[] buf, int offset) =>
        BitConverter.ToSingle(new[] { buf[offset + 3], buf[offset + 2], buf[offset + 1], buf[offset] }, 0);

    static byte[] SerializeVec3(object o)
    {
        var v = (Vec3)o;
        var buf = new byte[12];
        WriteFloatBE(buf, 0, v.X); WriteFloatBE(buf, 4, v.Y); WriteFloatBE(buf, 8, v.Z);
        return buf;
    }
    static object DeserializeVec3(byte[] bytes) =>
        new Vec3 { X = ReadFloatBE(bytes, 0), Y = ReadFloatBE(bytes, 4), Z = ReadFloatBE(bytes, 8) };

    // Registers our Vec3 stand-in under the real game's Vector3 custom type code (86) so Photon can serialize
    // it correctly on the wire. If this doesn't compile against your installed Photon package version, the
    // error will show the real PhotonPeer.RegisterType signature it expects.
    static void RegisterVec3CustomType()
    {
        if (vec3Registered) return;
        vec3Registered = true;
        try { PhotonPeer.RegisterType(typeof(Vec3), Vec3TypeCode, SerializeVec3, DeserializeVec3); }
        catch (Exception ex) { Console.WriteLine($"[warn] could not register the Vec3 custom type: {ex.Message}"); }
    }

    static void Main()
    {
        var cfg = JsonDocument.Parse(File.ReadAllText("config.json")).RootElement;
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; stopRequested = true; Log("Ctrl+C: leaving the room and disconnecting..."); };

        bool useSteam = cfg.TryGetProperty("UseSteam", out var us) && us.GetBoolean();
        if (useSteam && !GetSteamTicket()) { Log("Could not get a Steam ticket, so nothing was tried. See the message above."); return; }

        int connectSeconds = cfg.TryGetProperty("ConnectSeconds", out var cs) ? cs.GetInt32() : 25;
        int runSeconds = cfg.TryGetProperty("RunSeconds", out var rs) ? rs.GetInt32() : 60;
        scanning = cfg.TryGetProperty("ScanAllRegions", out var sc) && sc.GetBoolean();
        if (scanning) runSeconds = cfg.TryGetProperty("ScanSeconds", out var ss) ? ss.GetInt32() : 8;

        // Join mode settings
        string joinName = "", joinRegion = "", joinMatch = "";
        int stay = 90;
        if (cfg.TryGetProperty("JoinRoom", out var jr))
        {
            if (jr.TryGetProperty("Name", out var jn)) joinName = jn.GetString() ?? "";
            if (jr.TryGetProperty("Match", out var jm)) joinMatch = jm.GetString() ?? "";
            if (jr.TryGetProperty("Region", out var jg)) joinRegion = jg.GetString() ?? "";
            if (jr.TryGetProperty("StaySeconds", out var st)) stay = st.GetInt32();
        }
        if (cfg.TryGetProperty("ScoreboardSeconds", out var bs)) boardSeconds = bs.GetInt32();
        if (cfg.TryGetProperty("OnlyCompetitive", out var oc)) onlyCompetitive = oc.GetBoolean();
        // DIAGNOSTIC ONLY, off by default: logs every event code the crawler doesn't already understand, in
        // full, to chat_capture.log. Other real players' content is never recorded (code/size/sender only) -
        // only our own events, or the one trusted account below, are logged in full. See HandleInstantiate/
        // CaptureEvent. Use briefly in a private test room, then turn back off.
        if (cfg.TryGetProperty("CaptureUnknownEvents", out var cue)) captureUnknownEvents = cue.GetBoolean();
        // The one display name (case-insensitive) allowed to have its own captured content shown unredacted
        // during a CaptureUnknownEvents test, since the crawler itself never sends chat on its own. Leave blank
        // outside of a deliberate, consented test.
        if (cfg.TryGetProperty("CaptureTrustedPlayer", out var ctp)) captureTrustedPlayer = (ctp.GetString() ?? "").Trim();
        // DIAGNOSTIC ONLY, off by default: if set, sends this exact text as a one-off test chat message a few
        // seconds after joining. Superseded by the real JoinChatMessage/LeaveChatMessage feature below; kept
        // only as a quick way to test chat-sending changes in isolation. Leave blank otherwise.
        if (cfg.TryGetProperty("SendTestChatMessage", out var stc)) sendTestChatMessage = (stc.GetString() ?? "").Trim();
        // Real feature: announce in room chat on join and again right before leaving. Leave either blank to skip.
        if (cfg.TryGetProperty("JoinChatMessage", out var jcm)) joinChatMessage = (jcm.GetString() ?? "").Trim();
        if (cfg.TryGetProperty("LeaveChatMessage", out var lcm)) leaveChatMessage = (lcm.GetString() ?? "").Trim();
        if (cfg.TryGetProperty("ChatMessageDisplaySeconds", out var cds)) chatDisplaySeconds = cds.GetInt32();
        if (cfg.TryGetProperty("LeaveMessageDelaySeconds", out var lmd)) leaveMessageDelaySeconds = lmd.GetInt32();
        if (cfg.TryGetProperty("AllowedGameModes", out var agm)) foreach (var m in agm.EnumerateArray()) allowedModes.Add(m.GetInt32());
        if (cfg.TryGetProperty("BlockedMatchTypes", out var bmt)) foreach (var m in bmt.EnumerateArray()) blockedTypes.Add(m.GetInt32());
        if (onlyCompetitive && joinName.Length > 0 && joinMatch.Length == 0) { joinMatch = joinName; joinName = ""; }   // look the room up first so it can be checked
        bool joinMode = joinName.Length > 0 || joinMatch.Length > 0;
        if (joinMode) scanning = false;

        if (cfg.TryGetProperty("Crawl", out var crawlCfg) && crawlCfg.TryGetProperty("Enabled", out var cen) && cen.GetBoolean())
        {
            RunCrawler(cfg, crawlCfg, useSteam, connectSeconds);
            return;
        }

        var tally = new List<string>();
        int totalRooms = 0, totalPlayers = 0;

        foreach (var ep in cfg.GetProperty("Endpoints").EnumerateArray())
        {
            int port = ep.GetProperty("Port").GetInt32();
            string proto = ep.GetProperty("Protocol").GetString() ?? "Udp";
            bool nameServer = ep.TryGetProperty("NameServer", out var ns) && ns.GetBoolean();
            var regions = new List<string>();
            if (ep.TryGetProperty("Regions", out var rg))
                foreach (var r in rg.EnumerateArray()) regions.Add(r.GetString() ?? "");
            else regions.Add("");
            if (joinMode && joinRegion.Length > 0) { regions.Clear(); regions.Add(joinRegion); }

            foreach (var region in regions)
            {
                if (stopRequested) return;
                Log($"---------- port {port} over {proto} ({(nameServer ? "NAME server" : "master server")}), region '{region}' ----------");
                if ((scanning || joinMode) && useSteam && !GetSteamTicket()) { Log("Could not get a fresh Steam ticket."); return; }
                var p = new Program();
                p.joinName = joinMode ? joinName : "";
                p.joinMatch = joinMode ? joinMatch : "";
                p.staySeconds = stay;
                p.Attempt(cfg, ep.TryGetProperty("Server", out var sv) ? (sv.GetString() ?? "") : "", port, proto, nameServer, region, connectSeconds, runSeconds);
                if (joinMode)
                {
                    if (p.JoinedRoom) { Log("done."); return; }
                    if (p.Refused) { Log("Stopped: the room was found but not joined (see above)."); return; }
                    continue;
                }
                if (scanning)
                {
                    int players = 0;
                    foreach (var r in p.rooms.Values) players += r.PlayerCount;
                    totalRooms += p.rooms.Count; totalPlayers += players;
                    tally.Add($"{region,-5} {(p.ReachedMaster ? p.rooms.Count + " rooms, " + players + " players in rooms" : "could not connect")}");
                    continue;
                }
                if (p.ReachedMaster) { Log("done."); return; }
            }
        }
        if (joinMode) { Log($"Room '{(joinName.Length > 0 ? joinName : joinMatch)}' could not be found or joined in the region(s) tried."); return; }
        if (scanning)
        {
            Log("================ REGION SUMMARY ================");
            foreach (var t in tally) Log(t);
            Log($"TOTAL: {totalRooms} rooms, {totalPlayers} players in rooms");
            return;
        }
        Log("None of the listed ports answered.");
    }

    void Attempt(JsonElement cfg, string server, int port, string proto, bool nameServer, string region, int connectSeconds, int runSeconds)
    {
        string S(string k) => cfg.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";
        this.runSeconds = runSeconds;
        var protocol = proto.Equals("Tcp", StringComparison.OrdinalIgnoreCase) ? ConnectionProtocol.Tcp : ConnectionProtocol.Udp;

        if (!traceOn)
        {   // show Photon's own debug messages (they usually include the server's reason for refusing us)
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
            traceOn = true;
        }
        RegisterVec3CustomType();
        client.LoadBalancingPeer.DebugOut = DebugLevel.ALL;
        client.AuthMode = AuthModeOption.AuthOnce;   // log in once at the name server, then use the token (what the game does)
        client.AddCallbackTarget(this);
        client.StateChanged += (from, to) => Log($"state: {from} -> {to}");
        client.NickName = S("NickName");

        string ticket = steamHex.Length > 0 ? steamHex : S("SteamTicketHex");
        string uid = steamId.Length > 0 ? steamId : S("SteamId");
        if (ticket.Length > 0)
        {
            client.AuthValues = new AuthenticationValues { UserId = uid, AuthType = CustomAuthenticationType.Steam };
            client.AuthValues.AddAuthParameter("ticket", ticket);
            Log("sending a Steam ticket for login");
        }

        var settings = new AppSettings
        {
            AppIdRealtime = S("AppId"),
            AppVersion = S("AppVersion"),
            UseNameServer = nameServer,
            FixedRegion = region,
            Server = server.Length > 0 ? server : S("Server"),
            Port = port,
            Protocol = protocol,
            EnableLobbyStatistics = true,
        };

        Log($"connecting to {settings.Server}:{settings.Port} over {protocol}, version {settings.AppVersion}, region '{settings.FixedRegion}'");
        if (!client.ConnectUsingSettings(settings))
        {
            Log("ConnectUsingSettings returned false (the client refused to start connecting).");
            return;
        }

        end = DateTime.UtcNow.AddSeconds(connectSeconds);
        while (!finished && !stopRequested && DateTime.UtcNow < end)
        {
            client.Service();
            Thread.Sleep(30);
            if (crawlVisit && JoinedRoom) CrawlTick();
            if (searching && pendingRoom != null && (DateTime.UtcNow - pendingAt).TotalSeconds >= 6 && rooms.TryGetValue(pendingRoom, out var pr)) HandleFound(pr, true);
            if (verbose && JoinedRoom && (DateTime.UtcNow - lastSummary).TotalSeconds >= 60) { lastSummary = DateTime.UtcNow; PrintSummary(); }
            if (JoinedRoom && (DateTime.UtcNow - lastBoard).TotalSeconds >= boardSeconds) { lastBoard = DateTime.UtcNow; PrintScoreboard("every " + boardSeconds + "s"); }
        }

        if (JoinedRoom)
        {
            if (verbose) PrintSummary();
            PrintScoreboard("final");
            PrintPlayers("at the end");
            if (leaveChatMessage.Length > 0)
            {
                SendChatMessage(leaveChatMessage, "leave");
                // Stay connected a bit longer before actually leaving: Photon auto-destroys everything we own
                // (including the chat view) the instant we leave the room, so the message needs this wait to
                // be visible at all. LeaveMessageDelaySeconds in config.json controls how long.
                for (int i = 0; i < leaveMessageDelaySeconds * 1000 / 30; i++) { client.Service(); Thread.Sleep(30); }
            }
            Log("leaving the room now.");
            leaving = true;
            client.OpLeaveRoom(false);
            for (int i = 0; i < 60; i++) { client.Service(); Thread.Sleep(30); }
        }
        else if (ReachedMaster && joinName.Length == 0 && joinMatch.Length == 0) PrintRooms(scanning);

        client.Disconnect();
        for (int i = 0; i < 20; i++) { client.Service(); Thread.Sleep(30); }
        client.RemoveCallbackTarget(this);
    }

    // Asks the running Steam client (signed in as the bot account) for a login ticket.
    static bool GetSteamTicket()
    {
        try
        {
            if (!steamReady)
            {
                if (!Steamworks.SteamAPI.Init())
                {
                    Log("Steam could not start. Check that: Steam is running and signed in to the bot account, the account owns Zero Hour, and steam_appid.txt (containing 1359090) and steam_api64.dll are next to the program.");
                    return false;
                }
                steamReady = true;
            }
            if (steamHandle != Steamworks.HAuthTicket.Invalid) Steamworks.SteamUser.CancelAuthTicket(steamHandle);
            steamId = Steamworks.SteamUser.GetSteamID().m_SteamID.ToString();
            var buf = new byte[1024];
            uint size;
            steamHandle = Steamworks.SteamUser.GetAuthSessionTicket(buf, buf.Length, out size);
            for (int i = 0; i < 20; i++) { Steamworks.SteamAPI.RunCallbacks(); Thread.Sleep(50); }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < size; i++) sb.AppendFormat("{0:x2}", buf[i]);
            steamHex = sb.ToString();
            Log($"Steam ticket obtained for account {steamId} ({size} bytes).");
            return size > 0;
        }
        catch (Exception ex)
        {
            Log("Steam error: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    static void Log(string msg) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"); FileLog(msg); }

    // Everything also goes into zh_peek.log (next to the program) so it can be sent for analysis.
    static void FileLog(string msg)
    {
        try
        {
            if (fileLog != null && ++fileWrites % 500 == 0 && new FileInfo("zh_peek.log").Length > 20 * 1024 * 1024)
            {   // keep the log from growing forever: the old one becomes zh_peek.old.log (replacing the previous old one)
                fileLog.Dispose(); fileLog = null;
                File.Copy("zh_peek.log", "zh_peek.old.log", true); File.Delete("zh_peek.log");
            }
            if (fileLog == null) { fileLog = new StreamWriter("zh_peek.log", true) { AutoFlush = true }; fileLog.WriteLine("===== run " + DateTime.Now + " ====="); }
            fileLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
        }
        catch { }
    }

    // DIAGNOSTIC ONLY (see CaptureUnknownEvents). A separate, small log just for this investigation, kept apart
    // from zh_peek.log so a capture session is easy to find and share on its own.
    static StreamWriter captureLog;
    static void CaptureLog(string msg)
    {
        try
        {
            if (captureLog == null) { captureLog = new StreamWriter("chat_capture.log", true) { AutoFlush = true }; captureLog.WriteLine("===== capture session " + DateTime.Now + " ====="); }
            captureLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
        }
        catch { }
    }

    static bool IsDecimal(string t) =>
        t.IndexOf('.') >= 0 && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    // Turns any value from the game's messages into short readable text.
    static string Str(object o, int depth = 0)
    {
        if (o == null) return "null";
        var s = o as string; if (s != null) return "\"" + s + "\"";
        var b = o as byte[]; if (b != null) return "bytes(" + b.Length + ")" + (b.Length <= 24 ? BitConverter.ToString(b) : "");
        var h = o as Hashtable;
        if (h != null)
        {
            var kv = new List<string>();
            foreach (DictionaryEntry e in h) kv.Add(Str(e.Key, depth + 1) + "=" + Str(e.Value, depth + 1));
            return "{" + string.Join(",", kv) + "}";
        }
        var arr = o as Array;
        if (arr != null)
        {
            if (depth > 3) return "[...]";
            var items = new List<string>();
            foreach (var x in arr) { items.Add(Str(x, depth + 1)); if (items.Count >= 60) { items.Add("..."); break; } }
            return "[" + string.Join(",", items) + "]";
        }
        if (o.GetType().Name == "UnknownType")
        {
            // A Photon custom type our raw client has no decoder registered for (likely a Unity-specific
            // extension, e.g. a Vector3 position). Rather than guess its shape, pull out whatever fields/
            // properties the object actually has via reflection - this should reveal a type code and the
            // raw bytes, which is enough to reproduce the value exactly without understanding its format.
            var parts = new List<string>();
            foreach (var f in o.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                object v; try { v = f.GetValue(o); } catch { continue; }
                parts.Add(f.Name + "=" + Str(v, depth + 1));
            }
            foreach (var p in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                object v; try { v = p.GetValue(o); } catch { continue; }
                parts.Add(p.Name + "=" + Str(v, depth + 1));
            }
            return "UnknownType{" + string.Join(",", parts) + "}";
        }
        return Convert.ToString(o);
    }

    // ---------------------------------------------------------------- printing helpers
    // The lobby sometimes sends a room's player count first and its details (game mode etc.) a moment later,
    // so when the details are missing we wait for them instead of deciding straight away.
    string pendingRoom;
    DateTime pendingAt;
    void HandleFound(RoomInfo r, bool timedOut)
    {
        bool unknown = onlyCompetitive && RoomInt(r, "Game Mode") < 0;
        if (unknown && !timedOut)
        {
            if (pendingRoom != r.Name)
            {
                pendingRoom = r.Name; pendingAt = DateTime.UtcNow;
                end = DateTime.UtcNow.AddSeconds(14);
                Log($"FOUND room '{r.Name}' ({r.PlayerCount}/{r.MaxPlayers}). Its details have not arrived yet, waiting a few seconds before deciding...");
            }
            return;
        }
        bool ok; string why = "";
        if (unknown)
        {   // details never came: only the competitive quick-match rooms are named "Secured..."; co-op rooms are named differently
            ok = r.Name.StartsWith("Secured", StringComparison.Ordinal) && r.PlayerCount < r.MaxPlayers;
            if (ok) Log("Its details never arrived, but its name starts with 'Secured' (a competitive quick match), so it is allowed.");
            else why = r.PlayerCount >= r.MaxPlayers ? "it is full" : "I could not read its game mode and its name is not a 'Secured' match";
        }
        else ok = RoomAllowed(r, out why);
        searching = false;
        if (!ok)
        {
            Log($"FOUND room '{r.Name}' ({r.PlayerCount}/{r.MaxPlayers}) but NOT joining: {why}.");
            Refused = true; finished = true;
            return;
        }
        joinName = r.Name;
        end = DateTime.UtcNow.AddSeconds(20);
        Log($"FOUND room '{r.Name}' ({r.PlayerCount}/{r.MaxPlayers}). Joining as '{client.NickName}'...");
        if (!client.OpJoinRoom(new EnterRoomParams { RoomName = r.Name })) { Log("The client refused to send the join request."); finished = true; }
    }

    static int RoomInt(RoomInfo r, string key)
    {
        try { return r.CustomProperties != null && r.CustomProperties.ContainsKey(key) ? Convert.ToInt32(r.CustomProperties[key]) : -1; }
        catch { return -1; }
    }

    // Competitive = a game mode on the allowed list (TDM / Bomb / Hostage...) and not a blocked match type (co-op).
    bool RoomAllowed(RoomInfo r, out string why)
    {
        why = "";
        if (r.PlayerCount >= r.MaxPlayers) { why = $"it is full ({r.PlayerCount}/{r.MaxPlayers})"; return false; }
        if (!onlyCompetitive) return true;
        int mode = RoomInt(r, "Game Mode"), type = RoomInt(r, "Match Type");
        if (mode < 0) { why = "I can't tell its game mode"; return false; }
        if (blockedTypes.Contains(type)) { why = $"its Match Type is {type} (blocked, e.g. co-op)"; return false; }
        if (allowedModes.Count > 0 && !allowedModes.Contains(mode)) { why = $"its Game Mode is {mode}, which is not on the allowed list"; return false; }
        return true;
    }

    void PrintRooms(bool compact = false)
    {
        Log($"=== {rooms.Count} rooms known ===");
        foreach (var r in rooms.Values)
        {
            if (compact)
            {
                var parts = new List<string>();
                if (r.CustomProperties != null)
                    foreach (DictionaryEntry kv in r.CustomProperties) parts.Add($"{kv.Key}={kv.Value}");
                Log($"    {r.Name}  {r.PlayerCount}/{r.MaxPlayers}  {string.Join("; ", parts)}");
                continue;
            }
            Log($"room '{r.Name}'  players {r.PlayerCount}/{r.MaxPlayers}  open={r.IsOpen} visible={r.IsVisible}");
            if (r.CustomProperties != null)
                foreach (DictionaryEntry kv in r.CustomProperties)
                    Log($"    {kv.Key} = {kv.Value}");
        }
    }

    void PrintPlayers(string title)
    {
        var room = client.CurrentRoom;
        if (room == null) return;
        Log($"--- {title}: room '{room.Name}' {room.PlayerCount}/{room.MaxPlayers} ---");
        if (room.CustomProperties != null)
            foreach (DictionaryEntry kv in room.CustomProperties) Log($"    room property {kv.Key} = {kv.Value}");
        foreach (var kv in room.Players)
        {
            var pl = kv.Value;
            Log($"    actor {pl.ActorNumber}{(pl.IsLocal ? " (us)" : "")}{(pl.IsMasterClient ? " (master)" : "")}  name '{pl.NickName}'  userId '{pl.UserId}'");
            if (pl.CustomProperties != null)
                foreach (DictionaryEntry p in pl.CustomProperties) Log($"        {p.Key} = {p.Value}");
        }
    }

    // Each player's "IDLogger" object has view id (actor number x 1000 + 1). Its values, as decoded from a real match:
    //   #4 alive | #5 health | #6 damage dealt | #7 deaths | #8 kills | #9 score | #10 ping
    void PrintScoreboard(string why)
    {
        var room = client.CurrentRoom;
        if (room == null) return;
        string rs = room.CustomProperties != null && room.CustomProperties.ContainsKey("RoundStatus") ? Convert.ToString(room.CustomProperties["RoundStatus"]) : "?";
        string ms = room.CustomProperties != null && room.CustomProperties.ContainsKey("MatchStatus") ? Convert.ToString(room.CustomProperties["MatchStatus"]) : "?";
        Log($"======== SCOREBOARD ({why}) room '{room.Name}' MatchStatus={ms} RoundStatus={rs} players={room.PlayerCount} ========");
        foreach (var kv in room.Players)
        {
            var pl = kv.Value;
            if (pl.IsLocal) continue;   // that's us
            string id = pl.CustomProperties != null && pl.CustomProperties.ContainsKey("DiscriminatedId") ? Convert.ToString(pl.CustomProperties["DiscriminatedId"]) : pl.UserId;
            string[] st;
            if (!lastState.TryGetValue(pl.ActorNumber * 1000 + 1, out st) || st.Length < 11)
            {
                Log($"  actor {pl.ActorNumber,-2} {pl.NickName}  [{id}]  (no stats received yet)");
                continue;
            }
            Log($"  actor {pl.ActorNumber,-2} {pl.NickName}  [{id}]  kills {st[8]}  deaths {st[7]}  damage {st[6]}  score {st[9]}  health {st[5]}  {(st[4] == "True" ? "alive" : "dead")}  ping {st[10]}");
        }
    }


    // ======================================================================== crawler / recorder
    static string PlayerId(Player pl)
    {
        if (pl.CustomProperties != null && pl.CustomProperties.ContainsKey("DiscriminatedId"))
        {
            var d = Convert.ToString(pl.CustomProperties["DiscriminatedId"]);
            if (!string.IsNullOrEmpty(d)) return d;
        }
        return pl.UserId ?? "";
    }

    void FillRoster()
    {
        var room = client.CurrentRoom;
        if (room == null) return;
        foreach (var kv in room.Players)
            if (!kv.Value.IsLocal) roster[kv.Key] = new[] { kv.Value.NickName ?? "", PlayerId(kv.Value) };
    }

    // Called many times a second while we sit in a room we are recording.
    void CrawlTick()
    {
        var now = DateTime.UtcNow;
        if (inWindow && (now - closedAt).TotalSeconds >= 7) FinishWindow();
        if (!endSeen)
        {
            if (endSignalAt != null) { endSeen = true; finalizeAt = endSignalAt.Value.AddSeconds(4); }
            else if (roomClosed && (now - closedAt).TotalSeconds >= 25)
            {   // normal round breaks last about 6 seconds; a room that stays closed has finished
                endSeen = true; finalizeAt = now.AddSeconds(1);
                Log("The room stayed closed, so the match is treated as finished.");
            }
        }
        if (endSeen && Record == null && now >= finalizeAt)
        {
            if (inWindow) FinishWindow();
            BuildRecord();
            finished = true;   // done here: leave the room
        }
    }

    // After a round ends the game gives every player a bonus: +50 to the winning team, +10 to the losing team
    // (kills add multiples of 100). Reading those bonuses tells us who won each round, and so who is on which team.
    void FinishWindow()
    {
        inWindow = false;
        var cls = new Dictionary<int, char>();
        foreach (var kv in windowDelta)
        {
            long m = ((kv.Value % 100) + 100) % 100;
            if (m == 50) cls[kv.Key] = 'W'; else if (m == 10) cls[kv.Key] = 'L';
        }
        if (cls.Count >= 2)
        {
            roundClasses.Add(cls);
            Log($"round end read: winners [{string.Join(",", cls.Where(k => k.Value == 'W').Select(k => k.Key))}] losers [{string.Join(",", cls.Where(k => k.Value == 'L').Select(k => k.Key))}]");
        }
        else Log("round end: not enough bonus data to tell who won this round.");
    }

    static int FindRoot(Dictionary<int, int> parent, Dictionary<int, int> parity, int a, out int pty)
    {
        pty = 0; int cur = a;
        while (parent[cur] != cur) { pty ^= parity[cur]; cur = parent[cur]; }
        return cur;
    }

    // Works out which players share a team. Players with the same bonus in a round are team-mates, different bonus = opponents.
    Dictionary<int, int> ColourTeams()
    {
        var parent = new Dictionary<int, int>(); var parity = new Dictionary<int, int>();
        foreach (var cls in roundClasses)
        {
            var actors = cls.Keys.ToList();
            foreach (var a in actors) if (!parent.ContainsKey(a)) { parent[a] = a; parity[a] = 0; }
            int r = actors[0];
            foreach (var a in actors.Skip(1))
            {
                int diff = cls[a] == cls[r] ? 0 : 1;
                int pa, pb; int ra = FindRoot(parent, parity, r, out pa), rb = FindRoot(parent, parity, a, out pb);
                if (ra == rb) { if ((pa ^ pb) != diff) Log("team check: a round disagrees with earlier rounds (players may have switched teams)."); continue; }
                parent[rb] = ra; parity[rb] = pa ^ pb ^ diff;
            }
        }
        var teamOf = new Dictionary<int, int>();
        foreach (var a in parent.Keys) { int pty; int root = FindRoot(parent, parity, a, out pty); teamOf[a] = root * 2 + pty; }
        return teamOf;
    }

    static bool ToInt(string s, out int v)
    {
        v = 0;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
        v = (int)Math.Round(d); return true;
    }

    // Reads whole-number arguments of a function call (null if any argument is not a whole number).
    static int[] RpcInts(Hashtable h)
    {
        var a = h.ContainsKey((byte)4) ? h[(byte)4] as object[] : null;
        if (a == null) return null;
        var r = new int[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] is int x) r[i] = x;
            else if (a[i] is short sh) r[i] = sh;
            else if (a[i] is byte bt) r[i] = bt;
            else if (a[i] is long lg) r[i] = (int)lg;
            else return null;
        }
        return r;
    }

    // INFERRED method: who won, from the round-end score bonuses. won: actor -> on the winning side; teamLabel: actor -> "A" (winners) / "B".
    bool InferResult(out Dictionary<int, bool> won, out Dictionary<int, string> teamLabel)
    {
        won = new Dictionary<int, bool>(); teamLabel = new Dictionary<int, string>();
        if (roundClasses.Count == 0) { Log("Bonus method: no round-end bonuses were read, so the winners are unknown."); return false; }
        var finalClass = roundClasses[roundClasses.Count - 1];
        if (!finalClass.Values.Contains('W') || !finalClass.Values.Contains('L')) { Log("Bonus method: the last round's winners and losers are unclear."); return false; }
        var teamOf = ColourTeams();
        int winnersTeam = -1;
        foreach (var kv in finalClass) if (kv.Value == 'W' && teamOf.TryGetValue(kv.Key, out var t)) { winnersTeam = t; break; }
        foreach (var a in teamOf.Keys.Union(finalClass.Keys).ToList())
        {
            bool? w = null; string team = null;
            if (finalClass.TryGetValue(a, out var c)) w = c == 'W';
            if (teamOf.TryGetValue(a, out var ta) && winnersTeam >= 0 && ta / 2 == winnersTeam / 2)
            {
                bool onWinners = ta == winnersTeam;
                team = onWinners ? "A" : "B";
                if (w == null) w = onWinners;
            }
            if (w != null) { won[a] = w.Value; if (team != null) teamLabel[a] = team; }
        }
        return won.Count >= 2;
    }

    // DIRECT method: who won, from the game's own messages. Every player reports their side (blue/red) each round and the host
    // sends the round score as (blue wins, red wins). Sides swap at halftime, so a report made before an odd number of swaps is flipped.
    bool DirectResult(out Dictionary<int, bool> won)
    {
        won = new Dictionary<int, bool>();
        if (directBlue < 0 || directRed < 0 || directBlue == directRed) return false;
        bool blueWon = directBlue > directRed;
        foreach (var kv in sideOf)
        {
            bool blue = kv.Value == 'B';
            if (((swapCount - sideEpoch[kv.Key]) & 1) == 1) blue = !blue;
            won[kv.Key] = blue == blueWon;
        }
        return won.Count >= 2 && won.Values.Contains(true) && won.Values.Contains(false);
    }

    // Says in the log whether the two methods agree, so a few real matches can be checked before relying on the direct one.
    void LogComparison(bool haveDirect, Dictionary<int, bool> directWon, bool haveInferred, Dictionary<int, bool> inferWon, Dictionary<int, string> names)
    {
        if (!haveDirect)
        {
            if (directBlue < 0) Log($"Direct method: no round score was received ({sideOf.Count} side reports). If this keeps happening, the game's message numbers may have changed after an update (see rpc_numbers.py).");
            else Log($"Direct method: could not decide (round score blue {directBlue} - red {directRed}, {sideOf.Count} side reports).");
            return;
        }
        if (!haveInferred) { Log($"Direct method decided the result (blue {directBlue} - red {directRed}); the bonus method could not."); return; }
        var differ = new List<string>(); int same = 0;
        foreach (var kv in directWon)
        {
            if (!inferWon.TryGetValue(kv.Key, out var w)) continue;
            if (w == kv.Value) same++;
            else differ.Add($"{kv.Key} '{(names.TryGetValue(kv.Key, out var nm) ? nm : "?")}'");
        }
        if (differ.Count == 0) Log($"CHECK OK: both methods agree on all {same} players (round score blue {directBlue} - red {directRed}).");
        else Log($"CHECK DISAGREE: the methods differ on {differ.Count} of {same + differ.Count} players: {string.Join(", ", differ)}. Round score blue {directBlue} - red {directRed}, {swapCount} side swap(s) seen.");
    }

    // Builds the finished-match record from what we saw. Leaves Record empty (and says why) if the result is unclear.
    void BuildRecord()
    {
        var room = client.CurrentRoom;
        if (room == null) { Log("No room data, so nothing was recorded."); return; }
        var names = new Dictionary<int, string>(); var ids = new Dictionary<int, string>();
        foreach (var kv in roster) { names[kv.Key] = kv.Value[0]; ids[kv.Key] = kv.Value[1]; }
        foreach (var kv in room.Players) if (!kv.Value.IsLocal) { names[kv.Key] = kv.Value.NickName ?? ""; ids[kv.Key] = PlayerId(kv.Value); }

        bool haveInferred = InferResult(out var inferWon, out var inferTeam);
        bool haveDirect = DirectResult(out var directWon);
        LogComparison(haveDirect, directWon, haveInferred, inferWon, names);

        // Which method fills the record: the direct one if it is switched on and worked, otherwise the bonus method.
        bool useDirect = preferDirect && haveDirect;
        if (!useDirect && !haveInferred)
        {
            Log(haveDirect ? "The bonus method could not tell who won, so nothing was recorded (set Crawl.PreferDirect to true to use the direct method instead)."
                           : "Neither method could tell who won, so nothing was recorded.");
            return;
        }
        var wonBy = useDirect ? directWon : inferWon;

        var players = new List<Dictionary<string, object>>();
        foreach (var a in names.Keys.OrderBy(x => x))
        {
            if (string.IsNullOrEmpty(ids[a])) continue;
            if (!lastState.TryGetValue(a * 1000 + 1, out var st) || st.Length < 11
                || !ToInt(st[8], out var kills) || !ToInt(st[7], out var deaths) || !ToInt(st[9], out var score) || !ToInt(st[6], out var damage))
            { Log($"  skipping actor {a} ({names[a]}): no stats were received for them."); continue; }

            if (!wonBy.TryGetValue(a, out var won)) { Log($"  skipping actor {a} ({names[a]}): could not tell which team they were on."); continue; }
            string team = useDirect ? (won ? "A" : "B") : (inferTeam.TryGetValue(a, out var tl) ? tl : null);
            players.Add(new Dictionary<string, object> {
                ["player_key"] = ids[a], ["name"] = names[a], ["team"] = team, ["kills"] = kills, ["deaths"] = deaths,
                ["score"] = score, ["damage"] = damage, ["won"] = won });
        }
        if (players.Count < 2) { Log("Fewer than 2 players could be recorded. Nothing sent."); return; }

        RecordKey = "rec-" + room.Name + "-" + DateTime.UtcNow.ToString("yyyyMMdd");
        int mapId = RoomInt(room, "Map"), modeId = RoomInt(room, "Game Mode");
        Record = new Dictionary<string, object> {
            ["match_key"] = RecordKey, ["map"] = mapId < 0 ? (object)null : mapId, ["mode"] = modeId < 0 ? null : modeId.ToString(),
            ["ended_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["players"] = players, ["source"] = "photon-recorder",
            ["decided_by"] = useDirect ? "direct" : "bonus" };
        if (directBlue >= 0 && directRed >= 0) { Record["rounds_blue"] = directBlue; Record["rounds_red"] = directRed; }
        Log($"RESULT CAPTURED for '{room.Name}' ({(useDirect ? "direct method" : "bonus method")}): {players.Count} players, {players.Count(p => (bool)p["won"])} on the winning side.");
    }

    static void Deliver(Dictionary<string, object> rec, string key)
    {
        string json = JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            Directory.CreateDirectory("matches");
            string safe = key; foreach (char ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
            File.WriteAllText(Path.Combine("matches", safe + ".json"), json);
            Log($"saved matches/{safe}.json");
        }
        catch (Exception ex) { Log("could not save the file: " + ex.Message); }
        if (crawlUrl.Length == 0 || crawlKey.Length == 0) { Log("No UploadUrl/ApiKey set, so it was only saved to the matches folder."); return; }
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, crawlUrl) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Api-Key", crawlKey);
            using (var resp = http.Send(req))
                Log($"UPLOAD answered {(int)resp.StatusCode}: {resp.Content.ReadAsStringAsync().Result}");
        }
        catch (Exception ex) { Log("upload failed (the saved file is still in the matches folder): " + ex.Message); }
    }

    // A room is worth visiting if it is a competitive match already near its end, with free slots left for real players.
    static bool IsCandidate(RoomInfo r, int minRound)
    {
        if (!r.IsOpen) return false;
        int mode = RoomInt(r, "Game Mode"), type = RoomInt(r, "Match Type"), ms = RoomInt(r, "MatchStatus"), rs = RoomInt(r, "RoundStatus");
        if (ms != 1 || rs < minRound) return false;                      // only matches that are under way and into the later rounds
        if (onlyCompetitive)
        {
            if (mode < 0 || blockedTypes.Contains(type)) return false;
            if (allowedModes.Count > 0 && !allowedModes.Contains(mode)) return false;
        }
        return r.MaxPlayers > 0 && r.MaxPlayers - r.PlayerCount >= crawlMinFree;   // never take one of the last free slots
    }

    static void SleepChecking(int seconds)
    {
        for (int i = 0; i < seconds * 10 && !stopRequested; i++) Thread.Sleep(100);
    }

    // The main loop: look at a region's rooms, visit the most suitable one until its match ends, record it, move on.
    static void RunCrawler(JsonElement cfg, JsonElement cc, bool useSteam, int connectSeconds)
    {
        int I(string k, int d) => cc.TryGetProperty(k, out var v) ? v.GetInt32() : d;
        string Sx(string k) => cc.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";
        int minRound = I("MinRound", 6), maxStay = I("MaxStaySeconds", 600), scanSecs = I("ScanSeconds", 8);
        int pause = I("PauseBetweenSeconds", 10), maxPerHour = I("MaxJoinsPerHour", 15), maxHours = I("MaxHours", 0);
        crawlMinFree = I("MinFreeSlots", 1);
        double idleHours = cc.TryGetProperty("StopAfterIdleHours", out var ih) ? ih.GetDouble() : 0;
        string stopFile = Sx("StopFile").Length > 0 ? Sx("StopFile") : "stop.txt";
        verbose = cc.TryGetProperty("VerboseLog", out var vl) && vl.GetBoolean();
        boardSeconds = I("ScoreboardSeconds", 120);
        crawlUrl = Sx("UploadUrl"); crawlKey = Sx("ApiKey");
        preferDirect = cc.TryGetProperty("PreferDirect", out var pdv) && pdv.GetBoolean();
        if (cc.TryGetProperty("RpcNumbers", out var rn) && rn.ValueKind == JsonValueKind.Object)
        {
            int Rn(string k, int d) => rn.TryGetProperty(k, out var rv) ? rv.GetInt32() : d;
            rpcBlue = Rn("BlueTeam", rpcBlue); rpcRed = Rn("RedTeam", rpcRed); rpcWins = Rn("RoundScore", rpcWins);
            rpcSwap = Rn("SwapSides", rpcSwap); rpcEnd = Rn("MatchResult", rpcEnd); rpcNextMap = Rn("NextMapVote", rpcNextMap);
        }
        Log($"Teams and winners: {(preferDirect ? "read from the game's own messages (the bonus method is the fallback)" : "read from the round-end bonuses (the game's own messages are only logged and compared)")}.");
        var regions = new List<string>();
        if (cc.TryGetProperty("Regions", out var rg)) foreach (var r in rg.EnumerateArray()) regions.Add(r.GetString() ?? "");
        if (regions.Count == 0) regions.AddRange(new[] { "eu", "ru", "us" });
        var ep = cfg.GetProperty("Endpoints")[0];
        string server = ep.TryGetProperty("Server", out var sv) ? (sv.GetString() ?? "") : "";
        int port = ep.GetProperty("Port").GetInt32();
        string proto = ep.GetProperty("Protocol").GetString() ?? "Udp";

        var visited = new Dictionary<string, DateTime>();
        var joinTimes = new Queue<DateTime>();
        var quiet = new Dictionary<string, int>();
        var started = DateTime.UtcNow;
        var lastAvailable = DateTime.UtcNow;     // the last time any suitable room was found

        // "Turn it off" = Ctrl+C, or create a file named stop.txt in this folder (a background check watches for it).
        try { if (File.Exists(stopFile)) { File.Delete(stopFile); Log($"Removed an old '{stopFile}' left over from last time."); } } catch { }
        new Thread(() =>
        {
            while (!stopRequested)
            {
                try { if (File.Exists(stopFile)) { stopRequested = true; Log($"'{stopFile}' found: shutting down after leaving the current room..."); } } catch { }
                Thread.Sleep(2000);
            }
        }) { IsBackground = true }.Start();

        Log($"CRAWLER ON. Regions: {string.Join(",", regions)} | matches from round {minRound} on | keep {crawlMinFree} slots free | at most {maxPerHour} visits/hour | " +
            (crawlUrl.Length > 0 ? "uploading to " + crawlUrl : "NOT uploading (saving to the matches folder only)") +
            $" | runs until stopped{(idleHours > 0 ? $" or until no room is available for {idleHours} h" : "")}{(maxHours > 0 ? $" or for {maxHours} h" : "")}. To stop: Ctrl+C or create '{stopFile}'.");

        while (!stopRequested)
        {
            foreach (var region in regions)
            {
                try
                {
                    if (stopRequested) break;
                    if (maxHours > 0 && (DateTime.UtcNow - started).TotalHours >= maxHours) { Log("MaxHours reached, stopping."); return; }
                    if (idleHours > 0 && (DateTime.UtcNow - lastAvailable).TotalHours >= idleHours) { Log($"No suitable room has been available for {idleHours} hours, stopping."); return; }
                    if (quiet.TryGetValue(region, out var skip) && skip > 0) { quiet[region] = skip - 1; continue; }
                    foreach (var k in visited.Where(kv => (DateTime.UtcNow - kv.Value).TotalHours > 3).Select(kv => kv.Key).ToList()) visited.Remove(k);

                    // 1. look at the region's room list
                    if (useSteam && !GetSteamTicket()) { Log("Could not get a Steam ticket (is Steam running and signed in?). Trying again in 60 seconds."); SleepChecking(60); continue; }
                    scanning = true;
                    var scan = new Program();
                    scan.Attempt(cfg, server, port, proto, true, region, connectSeconds, scanSecs);
                    scanning = false;
                    if (!scan.ReachedMaster) { Log($"[{region}] could not connect."); continue; }
                    var cands = scan.rooms.Values.Where(r => IsCandidate(r, minRound) && !visited.ContainsKey(r.Name))
                                                 .OrderByDescending(r => RoomInt(r, "RoundStatus")).ToList();
                    Log($"[{region}] {scan.rooms.Count} rooms, {cands.Count} worth visiting.");
                    if (scan.rooms.Count == 0) quiet[region] = 4;   // an empty region is checked less often
                    if (cands.Count == 0) continue;
                    lastAvailable = DateTime.UtcNow;

                    // 2. respect the hourly limit
                    while (joinTimes.Count > 0 && (DateTime.UtcNow - joinTimes.Peek()).TotalHours >= 1) joinTimes.Dequeue();
                    if (joinTimes.Count >= maxPerHour)
                    {
                        int wait = (int)Math.Max(5, 3600 - (DateTime.UtcNow - joinTimes.Peek()).TotalSeconds);
                        Log($"Hourly limit of {maxPerHour} visits reached. Waiting {wait / 60} min {wait % 60} s.");
                        SleepChecking(wait);
                        continue;
                    }

                    // 3. visit the best room and record its result
                    var room = cands[0];
                    visited[room.Name] = DateTime.UtcNow; joinTimes.Enqueue(DateTime.UtcNow);
                    if (useSteam && !GetSteamTicket()) { Log("Could not get a Steam ticket (is Steam running and signed in?). Trying again in 60 seconds."); SleepChecking(60); continue; }
                    Log($"VISIT '{room.Name}' in {region}: {room.PlayerCount}/{room.MaxPlayers}, round {RoomInt(room, "RoundStatus")}.");
                    var v = new Program { joinName = room.Name, crawlVisit = true, crawlMinRound = minRound, staySeconds = maxStay };
                    v.Attempt(cfg, server, port, proto, true, region, connectSeconds, maxStay);
                    if (v.Record != null) Deliver(v.Record, v.RecordKey);
                    else if (!v.Skipped) Log("No finished result was recorded for that room (the match was not seen to the end, or the result was unclear).");
                    SleepChecking(pause);

                }
                catch (Exception ex) { Log("Something went wrong in that pass (" + ex.Message + "). Carrying on after a short pause."); SleepChecking(15); }
            }
            SleepChecking(5);   // a short breather after each full round of regions
        }
        Log("Crawler stopped.");
    }

    static string EventName(int code)
    {
        switch (code)
        {
            case 200: return "RPC";
            case 201: return "serialize (unreliable)";
            case 202: return "instantiate";
            case 203: return "close connection";
            case 204: return "destroy";
            case 205: return "remove cached RPCs";
            case 206: return "serialize (reliable)";
            case 207: return "destroy player";
            case 208: return "assign master";
            case 209: return "ownership request";
            case 210: return "ownership transfer";
            case 211: return "vacant view ids";
            case 224: return "lobby stats";
            case 226: return "app stats";
            case 229: return "game list";
            case 230: return "game list update";
            case 253: return "properties changed";
            case 254: return "player left";
            case 255: return "player joined";
            default: return code < 200 ? "game-defined event" : "system event";
        }
    }

    void PrintSummary()
    {
        var parts = new List<string>();
        foreach (var kv in eventCounts) parts.Add($"{kv.Key} {EventName(kv.Key)} x{kv.Value}");
        parts.Sort();
        Log("event counts so far: " + (parts.Count == 0 ? "(none)" : string.Join(" | ", parts)));
        if (rpcNames.Count > 0)
        {
            var names = new List<string>();
            foreach (var kv in rpcNames) names.Add($"{kv.Key} x{kv.Value}");
            names.Sort();
            Log("RPC methods seen: " + string.Join(", ", names));
        }
    }

    // ---------------------------------------------------------------- connection callbacks
    public void OnConnected() => Log("OnConnected (network link is up)");
    public void OnConnectedToMaster()
    {
        ReachedMaster = true;
        Log("OnConnectedToMaster - the server accepted us.");
        if (leaving) { finished = true; return; }   // we are on our way out: do NOT join again
        if (joinName.Length > 0)
        {
            end = DateTime.UtcNow.AddSeconds(20);   // time allowed for the join itself
            Log($"joining room '{joinName}' as '{client.NickName}'...");
            if (!client.OpJoinRoom(new EnterRoomParams { RoomName = joinName }))
            {
                Log("The client refused to send the join request.");
                finished = true;
            }
            return;
        }
        if (joinMatch.Length > 0)
        {   // we only know the number: look through this region's room list for it
            searching = true;
            end = DateTime.UtcNow.AddSeconds(9);
            Log($"looking for a room ending in '{joinMatch}' in this region...");
            client.OpJoinLobby(TypedLobby.Default);
            return;
        }
        end = DateTime.UtcNow.AddSeconds(runSeconds);
        client.OpJoinLobby(TypedLobby.Default);
    }
    public void OnDisconnected(DisconnectCause cause) { Log($"OnDisconnected: {cause}"); finished = true; }
    public void OnRegionListReceived(RegionHandler regionHandler)
    {
        Log("OnRegionListReceived - the name server accepted us and sent its regions:");
        try
        {
            var list = regionHandler.GetType().GetProperty("EnabledRegions")?.GetValue(regionHandler) as IEnumerable;
            if (list != null) foreach (var r in list) Log("    region: " + r);
        }
        catch (Exception ex) { Log("    (could not list regions: " + ex.Message + ")"); }
    }
    public void OnCustomAuthenticationResponse(Dictionary<string, object> data) => Log("OnCustomAuthenticationResponse");
    public void OnCustomAuthenticationFailed(string debugMessage) => Log($"OnCustomAuthenticationFailed: {debugMessage}");

    // ---------------------------------------------------------------- lobby callbacks
    public void OnJoinedLobby() => Log("OnJoinedLobby");
    public void OnLeftLobby() => Log("OnLeftLobby");
    public void OnRoomListUpdate(List<RoomInfo> roomList)
    {
        foreach (var r in roomList)
        {
            if (r.RemovedFromList) rooms.Remove(r.Name); else rooms[r.Name] = r;
        }
        Log($"OnRoomListUpdate: {roomList.Count} changes, {rooms.Count} rooms now");
        if (searching)
        {
            foreach (var r in rooms.Values)
                if (r.Name.EndsWith(joinMatch, StringComparison.Ordinal)) { HandleFound(r, false); return; }
        }
        if (!scanning && !printedRooms && rooms.Count > 0) { printedRooms = true; PrintRooms(); }
    }
    public void OnLobbyStatisticsUpdate(List<TypedLobbyInfo> lobbyStatistics)
    {
        foreach (var l in lobbyStatistics)
            Log($"lobby '{l.Name}' type={l.Type} rooms={l.RoomCount} players={l.PlayerCount}");
    }

    // ---------------------------------------------------------------- matchmaking callbacks
    public void OnFriendListUpdate(List<FriendInfo> friendList) { }
    public void OnCreatedRoom() { }
    public void OnCreateRoomFailed(short returnCode, string message) => Log($"OnCreateRoomFailed {returnCode}: {message}");
    public void OnJoinedRoom()
    {
        JoinedRoom = true;
        end = DateTime.UtcNow.AddSeconds(staySeconds);
        lastSummary = DateTime.UtcNow;
        Log($"JOINED ROOM '{client.CurrentRoom.Name}' - we will stay {staySeconds} seconds, then leave on our own.");
        PrintPlayers("on joining");
        FillRoster();
        if (sendTestChatMessage.Length > 0 || joinChatMessage.Length > 0 || leaveChatMessage.Length > 0)
        {
            // Instantiate our own IDLogger-shaped view right away, the same way a real client does on join -
            // this is the piece a chat message's sender view id needs to resolve to something real instead of
            // template placeholder text. Give it a moment to land before sending any chat through it.
            InstantiateOwnIDLogger();
            if (sendTestChatMessage.Length > 0)
                testChatTimer = new System.Threading.Timer(_ => MaybeSendTestChatMessage(), null, 4000, System.Threading.Timeout.Infinite);
            if (joinChatMessage.Length > 0)
                joinChatTimer = new System.Threading.Timer(_ => SendChatMessage(joinChatMessage, "join"), null, 3000, System.Threading.Timeout.Infinite);
        }
        if (crawlVisit)
        {
            int ms = RoomInt(client.CurrentRoom, "MatchStatus"), rs = RoomInt(client.CurrentRoom, "RoundStatus");
            if (ms != 1 || rs < crawlMinRound)
            {
                Log($"This room no longer qualifies (MatchStatus {ms}, RoundStatus {rs}). Leaving straight away.");
                Skipped = true; finished = true;
            }
        }
    }
    public void OnJoinRoomFailed(short returnCode, string message)
    {
        Log($"OnJoinRoomFailed {returnCode}: {message}");
        finished = true;
    }
    public void OnJoinRandomFailed(short returnCode, string message) => Log($"OnJoinRandomFailed {returnCode}: {message}");
    public void OnLeftRoom() => Log("OnLeftRoom");

    // ---------------------------------------------------------------- in-room callbacks
    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        Log($"player joined: actor {newPlayer.ActorNumber} '{newPlayer.NickName}' userId '{newPlayer.UserId}'");
        if (!newPlayer.IsLocal) roster[newPlayer.ActorNumber] = new[] { newPlayer.NickName ?? "", PlayerId(newPlayer) };
    }
    public void OnPlayerLeftRoom(Player otherPlayer) => Log($"player left: actor {otherPlayer.ActorNumber} '{otherPlayer.NickName}'");
    public void OnRoomPropertiesUpdate(PhotonHashtable propertiesThatChanged)
    {
        var parts = new List<string>();
        foreach (DictionaryEntry kv in propertiesThatChanged) parts.Add($"{kv.Key}={kv.Value}");
        Log("room properties changed: " + string.Join("; ", parts));
        foreach (DictionaryEntry kv in propertiesThatChanged)
        {
            if (kv.Key is byte kb && kb == 253 && kv.Value is bool open)
            {
                if (!open) { roomClosed = true; closedAt = DateTime.UtcNow; windowDelta.Clear(); inWindow = true; }   // a round just ended
                else roomClosed = false;
            }
        }
        if (JoinedRoom && (propertiesThatChanged.ContainsKey("RoundStatus") || propertiesThatChanged.ContainsKey("MatchStatus")))
            PrintScoreboard("room status changed");
    }
    public void OnPlayerPropertiesUpdate(Player targetPlayer, PhotonHashtable changedProps)
    {
        var parts = new List<string>();
        foreach (DictionaryEntry kv in changedProps) parts.Add($"{kv.Key}={kv.Value}");
        Log($"player {targetPlayer.ActorNumber} '{targetPlayer.NickName}' properties changed: " + string.Join("; ", parts));
    }
    public void OnMasterClientSwitched(Player newMasterClient) => Log($"master client is now actor {newMasterClient.ActorNumber}");

    // ---------------------------------------------------------------- game events
    public void OnEvent(EventData photonEvent)
    {
        int code = photonEvent.Code;
        eventCounts[code] = eventCounts.TryGetValue(code, out var n) ? n + 1 : 1;
        int sender = photonEvent.Sender;

        try
        {
            if (code == 202) HandleInstantiate(photonEvent, sender);
            else if (code == 200) HandleRpc(photonEvent, sender);
            else if (code == 201 || code == 206) HandleSerialize(photonEvent, code, sender);
            else if (code == 204) FileLog($"destroy from actor {sender}: {Str(photonEvent.CustomData)}");
            if (captureUnknownEvents && code != 200 && code != 201 && code != 202 && code != 204 && code != 206)
                CaptureEvent(photonEvent, code, sender);
        }
        catch (Exception ex) { FileLog("(error reading event " + code + ": " + ex.Message + ")"); }

        int shown = eventDumped.TryGetValue(code, out var d) ? d : 0;
        if (verbose && shown < 2 && code != 201 && code != 206 && code != 200)
        {
            eventDumped[code] = shown + 1;
            string text;
            try { text = photonEvent.ToStringFull(); } catch { text = "(could not print)"; }
            if (text.Length > 400) text = text.Substring(0, 400) + "...";
            Log($"EVENT {code} ({EventName(code)}) from actor {sender}: {text}");
        }
    }

    // DIAGNOSTIC ONLY (see CaptureUnknownEvents above). Full detail for our own events, or the trusted test
    // account's; any other real player's event is recorded as shape only (code, size, sender), never content.
    void CaptureEvent(EventData e, int code, int sender)
    {
        int myActor = client.LocalPlayer?.ActorNumber ?? -1;
        bool isTrusted = false;
        if (captureTrustedPlayer.Length > 0 && client.CurrentRoom != null && client.CurrentRoom.Players.TryGetValue(sender, out var pl))
            isTrusted = string.Equals(pl.NickName, captureTrustedPlayer, StringComparison.OrdinalIgnoreCase);
        bool isOurs = sender <= 0 || sender == myActor || isTrusted;
        string text;
        try { text = isOurs ? e.ToStringFull() : $"(from another player - content not recorded; CustomData type {e.CustomData?.GetType().Name ?? "null"})"; }
        catch { text = "(could not print)"; }
        string tag = isTrusted ? " [trusted test account]" : (isOurs ? "" : " [not us]");
        CaptureLog($"CAPTURE event {code} ({EventName(code)}) from actor {sender}{tag}: {text}");
    }

    // Instantiates a view shaped like the real game's IDLogger for our own actor, so a chat message's sender
    // view id resolves to something real instead of falling back to template placeholder text. Shape taken
    // from a real captured IDLogger INSTANTIATE:
    //   {6=<verification hash>, 4=[v1,v2], 0="IDLogger", 1=<Vector3, custom type 86>, 7=v1}
    // i.e. TWO view ids at once (4, the full list; 7, the primary one, redundant with 4[0]), a prefab name, the
    // same kind of verification hash ChatFeed_Prefab needs (6), and a position (1, see the Vec3 note above).
    int ownIDLoggerView;   // 0 until InstantiateOwnIDLogger() has run
    void InstantiateOwnIDLogger()
    {
        try
        {
            int myActor = client.LocalPlayer?.ActorNumber ?? -1;
            if (myActor <= 0) { Log("Can't instantiate our own IDLogger: no local actor number yet."); return; }
            int v1 = myActor * 1000 + 1, v2 = myActor * 1000 + 2;   // matches the real per-actor view id scheme (actor*1000 + slot)
            var idLoggerData = new PhotonHashtable {
                { (byte)0, "IDLogger" },
                { (byte)1, new Vec3 { X = 0, Y = 0, Z = 0 } },   // position - doesn't need to be real, just present & correctly typed
                { (byte)4, new int[] { v1, v2 } },
                { (byte)6, Environment.TickCount },               // same role as ChatFeed's hash; exact value not confirmed to matter
                { (byte)7, v1 },
            };
            client.OpRaiseEvent(202, idLoggerData, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
            ownIDLoggerView = v1;
            Log($"Instantiated our own IDLogger-shaped view [{v1},{v2}].");
        }
        catch (Exception ex) { Log($"Instantiating our own IDLogger failed: {ex.Message}"); }
    }

    // PUN's generic "Destroy" op (event code 204, one-field payload {0=viewId}). Used to make our own chat
    // bubble fade away like a real one instead of sitting on screen forever.
    readonly List<System.Threading.Timer> pendingTimers = new List<System.Threading.Timer>();  // keep timers alive until they fire
    void DestroyView(int viewId, string why)
    {
        try
        {
            client.OpRaiseEvent(204, new PhotonHashtable { { (byte)0, viewId } }, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
            Log($"Destroyed view {viewId} ({why}).");
        }
        catch (Exception ex) { Log($"Destroying view {viewId} ({why}) failed: {ex.Message}"); }
    }

    // How long a chat bubble we sent stays up before we destroy its view ourselves. Adjust
    // ChatMessageDisplaySeconds in config.json if it doesn't match how long a real message stays up.
    static int chatDisplaySeconds = 8;

    // Sends one chat message as "us". Used for the real join/leave announcements as well as the diagnostic
    // SendTestChatMessage field. A fresh chatView offset is used each call (chatMessageCount) so a join message
    // and a later leave message don't reuse the same Photon view id.
    int chatMessageCount;
    void SendChatMessage(string text, string why)
    {
        if (text.Length == 0) return;
        try
        {
            int myActor = client.LocalPlayer?.ActorNumber ?? -1;
            if (myActor <= 0) { Log($"Can't send the {why} chat message: no local actor number yet."); return; }
            if (ownIDLoggerView <= 0) InstantiateOwnIDLogger();   // make sure a real sender view exists first
            // Use the view id we actually instantiated an IDLogger-shaped object under, not just a bare
            // computed number - that's the fix that made the sender/message resolve correctly.
            int myLoggerView = ownIDLoggerView > 0 ? ownIDLoggerView : myActor * 1000 + 1;
            int chatView = myActor * 1000 + 500 + chatMessageCount++;   // a free, per-message offset in our own block

            // Key 6 is a required verification-hash field (NetworkInstantiate null-checks it); not confirmed
            // to need this exact value vs. just being present, but this is the one real value we've captured.
            var instantiateData = new PhotonHashtable {
                { (byte)0, "ChatFeed_Prefab" },              // prefab name
                { (byte)6, 165573598 },                      // the field that was missing before - see note above
                { (byte)7, chatView },                       // the view id we are claiming
            };
            client.OpRaiseEvent(202, instantiateData, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);

            var rpcData = new PhotonHashtable {
                { (byte)0, chatView },                       // target view (the one we just instantiated)
                { (byte)4, new object[] { 3, myLoggerView, text } },
                { (byte)5, (byte)197 },                       // RPC index for ShowChatFeed
            };
            client.OpRaiseEvent(200, rpcData, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);

            Log($"{why.ToUpperInvariant()} CHAT SENT: view {chatView}, args [3,{myLoggerView},\"{text}\"]");

            // Make it fade like a real message instead of sitting on screen forever. Harmless to schedule this
            // even for the leave message - the process will have already exited by the time it would fire.
            var t = new System.Threading.Timer(_ => DestroyView(chatView, why + " chat bubble"), null, chatDisplaySeconds * 1000, System.Threading.Timeout.Infinite);
            pendingTimers.Add(t);
        }
        catch (Exception ex) { Log($"Sending the {why} chat message failed: {ex.Message}"); }
    }

    bool testMessageSent;
    System.Threading.Timer testChatTimer;  // must be kept in a field - a Timer in a local variable can be garbage-collected before it fires
    System.Threading.Timer joinChatTimer;  // same reason - see testChatTimer
    void MaybeSendTestChatMessage()
    {
        if (testMessageSent || sendTestChatMessage.Length == 0) return;
        testMessageSent = true;
        SendChatMessage(sendTestChatMessage, "test");
    }

    // A game object was created. Remember its name by view id; objects with "Log" in the name are the ones to watch.
    // capturedChatInstantiate/capturedIDLoggerInstantiate (below) are diagnostic one-shot captures, gated behind
    // CaptureUnknownEvents: the Instantiate payload itself never carries message text (only prefab name, a
    // verification hash, and view ids), so capturing one example of each in full is not a privacy concern the
    // way chat content is - useful if a future game update changes either object's shape.
    bool capturedChatInstantiate;
    bool capturedIDLoggerInstantiate;
    void HandleInstantiate(EventData e, int sender)
    {
        var h = e.CustomData as Hashtable;
        if (h == null) return;
        string prefab = h.ContainsKey((byte)0) ? Convert.ToString(h[(byte)0]) : "?";
        if (captureUnknownEvents && !capturedChatInstantiate && prefab.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 && sender != (client.LocalPlayer?.ActorNumber ?? -1))
        {
            capturedChatInstantiate = true;
            CaptureLog($"FULL ChatFeed INSTANTIATE from actor {sender}: {Str(h)}");
        }
        if (captureUnknownEvents && !capturedIDLoggerInstantiate && prefab.Equals("IDLogger", StringComparison.OrdinalIgnoreCase) && sender != (client.LocalPlayer?.ActorNumber ?? -1))
        {
            capturedIDLoggerInstantiate = true;
            CaptureLog($"FULL IDLogger INSTANTIATE from actor {sender}: {Str(h)}");
        }
        var ids = new List<int>();
        if (h.ContainsKey((byte)7) && h[(byte)7] is int v7) ids.Add(v7);
        var more = h.ContainsKey((byte)4) ? h[(byte)4] as int[] : null;
        if (more != null) foreach (var i in more) if (!ids.Contains(i)) ids.Add(i);
        foreach (var id in ids)
        {
            viewNames[id] = prefab;
            if (prefab.IndexOf("log", StringComparison.OrdinalIgnoreCase) >= 0) watch.Add(id);
        }
        string line = $"INSTANTIATE '{prefab}' by actor {sender}, view ids [{string.Join(",", ids)}]{(watch.Contains(ids.Count > 0 ? ids[0] : -1) ? "  <== WATCHING" : "")}";
        if (prefab.IndexOf("log", StringComparison.OrdinalIgnoreCase) >= 0 || eventDumped.TryGetValue(202, out var c) == false || c < 6)
        {
            eventDumped[202] = (eventDumped.TryGetValue(202, out var c2) ? c2 : 0) + 1;
            Log(line);
        }
        else if (verbose) FileLog(line);
    }

    // A function call. The game sends a short index instead of the name, so we keep the index, the object it was
    // aimed at, and the arguments (these often contain names and numbers that show what the call means).
    void HandleRpc(EventData e, int sender)
    {
        var h = e.CustomData as Hashtable;
        if (h == null) return;
        string method = h.ContainsKey((byte)3) ? Convert.ToString(h[(byte)3])
                      : h.ContainsKey((byte)5) ? "index " + Convert.ToString(h[(byte)5]) : "?";
        rpcNames[method] = rpcNames.TryGetValue(method, out var c) ? c + 1 : 1;
        int viewId = h.ContainsKey((byte)0) && h[(byte)0] is int vi ? vi : -1;
        string target = viewNames.TryGetValue(viewId, out var nm) ? nm : "?";
        bool isChat = target.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0;
        // Same trusted-account exception as CaptureEvent: show the real args only when the sender is the one
        // consented test account named in CaptureTrustedPlayer, so this investigation can see its own test
        // message's real arguments while any other real player's chat stays redacted as before.
        if (isChat && captureTrustedPlayer.Length > 0 && client.CurrentRoom != null
            && client.CurrentRoom.Players.TryGetValue(sender, out var chatPl)
            && string.Equals(chatPl.NickName, captureTrustedPlayer, StringComparison.OrdinalIgnoreCase))
            isChat = false;
        if ((method == "index " + rpcEnd || method == "index " + rpcNextMap) && target == "MatchLog" && endSignalAt == null)
        {
            endSignalAt = DateTime.UtcNow;
            Log("MATCH-END signal received (" + method + ").");
        }
        // Direct signals: every player reports their side each round, the host sends the round score, and sides swap at halftime.
        int rpcIdx = -1;
        if (h.ContainsKey((byte)5) && !int.TryParse(Convert.ToString(h[(byte)5]), out rpcIdx)) rpcIdx = -1;
        if (rpcIdx >= 0 && target == "MatchLog")
        {
            if ((rpcIdx == rpcBlue || rpcIdx == rpcRed) && sender > 0)
            {
                sideOf[sender] = rpcIdx == rpcBlue ? 'B' : 'R';
                sideEpoch[sender] = swapCount;
            }
            else if (rpcIdx == rpcWins)
            {
                var ints = RpcInts(h);
                if (ints != null && ints.Length >= 2) { directBlue = ints[0]; directRed = ints[1]; }
            }
            else if (rpcIdx == rpcSwap) swapCount++;
        }
        string args = isChat ? "(chat text not recorded)" : (h.ContainsKey((byte)4) ? Str(h[(byte)4]) : "[]");
        string line = $"RPC {method} from actor {sender} to view {viewId} ({target}) args {args}";
        int count = rpcNames[method];
        if (verbose && (count <= 20 || count % 50 == 0)) FileLog(line + (count > 20 ? $"   (call number {count})" : ""));
        if (verbose && rpcSampled.Add(method) && rpcSampled.Count <= 60)
        {
            string shown = $"[{DateTime.Now:HH:mm:ss}] first {line}";
            Console.WriteLine(shown.Length > 260 ? shown.Substring(0, 260) + "..." : shown);
        }
    }

    // Continuous data updates. The first-level arrays inside the message are one object each (view id first).
    // We record every change in the reliable ones, and in the fast ones only for the game's "Log" objects.
    void HandleSerialize(EventData e, int code, int sender)
    {
        var top = e.CustomData as object[];
        if (top == null) return;
        foreach (var item in top)
        {
            var arr = item as object[];
            if (arr == null || arr.Length < 2 || !(arr[0] is int id)) continue;
            bool watched = watch.Contains(id);
            if (code == 201 && !watched) continue;
            var now = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++) now[i] = Str(arr[i]);
            string name = viewNames.TryGetValue(id, out var nm) ? nm : "?";
            if (!lastState.TryGetValue(id, out var before))
            {
                lastState[id] = now;
                if (verbose) FileLog($"[view {id} {name} actor {sender}] first state ({code}): {string.Join(" | ", now)}");
                continue;
            }
            var changes = new List<string>();
            for (int i = 0; i < now.Length && i < before.Length; i++)
            {
                if (now[i] == before[i]) continue;
                if (IsDecimal(now[i]) && IsDecimal(before[i])) continue;   // timers and positions: pure noise
                changes.Add($"#{i}: {before[i]} -> {now[i]}");
            }
            if (now.Length != before.Length) changes.Add($"length {before.Length} -> {now.Length}");
            if (inWindow && id % 1000 == 1 && name == "IDLogger" && now.Length > 9 && before.Length > 9
                && long.TryParse(before[9], out var oldScore) && long.TryParse(now[9], out var newScore))
            {
                int actorN = id / 1000;
                windowDelta[actorN] = (windowDelta.TryGetValue(actorN, out var cur) ? cur : 0) + (newScore - oldScore);
            }
            lastState[id] = now;
            if (changes.Count == 0) continue;
            string line = $"[view {id} {name} actor {sender}] changed ({code}): {string.Join("; ", changes)}";
            if (verbose) FileLog(line);
            if (verbose && watched && consoleChanges++ < 120) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
        }
    }
}
