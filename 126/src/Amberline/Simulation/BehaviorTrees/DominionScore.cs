using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>The team control counts the score trees read: the capture points' owners (BehaviorTreeQuestHost, batch 448).</summary>
public interface IDominionControl
{
    /// <summary>The capture points the team holds (0 to 5).</summary>
    int ControlCount(Team team);
}

/// <summary>What the score does to the match: the 0xDD meter, and the nexus damage of the trailing team.</summary>
public interface IDominionScoreSink
{
    /// <summary>0xDD: the team's whole nexus health (batch 450: the meter reads nexus health, see <see cref="DominionScore"/>).</summary>
    void SetGameScore(Team team, int score);

    /// <summary>The team's nexus health (0 once it is down).</summary>
    double NexusHealth(Team team);

    /// <summary>Damage to the team's nexus through the damage path (see <see cref="DominionScore"/>).</summary>
    void DamageNexus(Team team, double amount);

    /// <summary>Batch 508: the drain's damage, the same path without the floating text (owner: the text is for kills, captures and quests).</summary>
    void DrainNexus(Team team, double amount) => DamageNexus(team, amount);
}

/// <summary>
/// The Dominion score. The shipped <c>DualUpdateScoreMath</c> tree (LEVELS/Map8/Scripts/OdinScriptBehaviorTrees.xml:3907) runs once per
/// tick and keeps the team totals; no shipped file invokes it (Reconstruction: the caller and the cadence, one tick per simulation step).
/// Batch 450: the 0xDD meter is each team's whole nexus health (500 down to 0), sent at the start and on every whole change (see
/// <see cref="SendNexusScores"/>); the tree's totals are not sent. The drain is a Reconstruction (batch 505, see <see cref="Tick"/>).
/// </summary>
public sealed class DominionScore
{
    private readonly BehaviorTreeInstance _tree;
    private readonly BehaviorTreeContext _context;
    private readonly IDominionControl _control;
    private readonly IDominionScoreSink _sink;
    private readonly Dictionary<Team, int> _sent = new();
    // Batch 505: each team's victory-point stage already announced (0 to 3, see AnnounceThreshold).
    private readonly Dictionary<Team, int> _stage = new();
    // Batch 747: the music phase already sent, as a stage (0 to 4: phases 1 to 5, see PlayMusicPhase).
    private int _musicStage;

    // Seconds of game time since the last drain application (see DrainIntervalSeconds); the epsilon absorbs the sum of the steps' float error.
    private double _drainClock;
    private const double DrainClockEpsilon = 1e-9;

    public DominionScore(BehaviorTreeLibrary library, BehaviorTreeContext context, IDominionControl control, IDominionScoreSink sink)
    {
        _context = context;
        _control = control;
        _sink = sink;
        _tree = new BehaviorTreeInstance(library, "DualUpdateScoreMath", context);
        _tree.SetVariable("TotalOrderScore", 0.0);
        _tree.SetVariable("TotalChaosScore", 0.0);
    }

    /// <summary>A team's total as the tree holds it (the points counted so far, on the time base of BehaviorLeaves.ReferenceTickSeconds).</summary>
    public double Total(Team team) => _tree.Variable(team == Team.Order ? "TotalOrderScore" : "TotalChaosScore") is double total
        ? total : 0.0;

    /// <summary>
    /// Batch 505 (Reconstruction): the drain's tick, in seconds of game time. No shipped file gives it (OverallScoreMath and DualUpdateScoreMath
    /// have no caller). Gamerevolution's hands-on of 4 August 2011: the points "tick down every two seconds" when the other team holds more
    /// capture points; the forum post of 15 February 2012 gives its table "per tick" (<see cref="DrainAmount"/>). A steady 3-0-2 hold (1 per
    /// tick) drains a full 500 nexus in about 16.7 minutes. Replaces batch 478b's 6 s stand-in.
    /// </summary>
    public const double DrainIntervalSeconds = 2.0;

    /// <summary>
    /// Batch 505 (Reconstruction): the nexus damage one drain tick costs the trailing team, by the leader's point count
    /// (<paramref name="leader"/>) and the trailing team's (<paramref name="trailing"/>); the rest of the five are neutral. The forum post of
    /// 15 February 2012 (the owner's evidence) lists "You-Neutral-Enemy: nexus reduction per tick": 1-4-0 1, 2-2-1 1, 3-0-2 1, 3-1-1 1.5,
    /// 3-2-0 2, 4-0-1 2.5, 4-1-0 3, 5-0-0 5, and 0 for every tie (0-5-0, 1-3-1, 2-1-2; wiki, Dominion: "When both teams have the same amount of
    /// capture points, neither nexus will be damaged"). The post leaves out 2-3-0: 1.5 here, because in every row it gives each trailing point
    /// takes 0.5 off the leader's rate (3-0-2, 3-1-1, 3-2-0 and 4-0-1, 4-1-0), and 2-2-1 is 1.
    /// Neither shipped tree reproduces the table. OverallScoreMath (OdinScriptBehaviorTrees.xml:3226-3327) is a function of the lead alone
    /// (3, 5, 8, 13, 21), and 3-2-0 and 4-0-1 share a lead of 3 but drain 2 and 2.5. DualUpdateScoreMath's own-count rates (1, 1.5, 2, 2.5,
    /// 10; :3907-4165) differenced give 0.5 for 2-2-1 (the post: 1) and 10 for 5-0-0 (the post: 5). The post postdates V1.0.0.127; no
    /// 1.0.0.126 table is known. Anything else (a tie, or no lead) drains nothing.
    /// </summary>
    public static double DrainAmount(int leader, int trailing) => (leader, trailing) switch
    {
        (1, 0) => 1.0,
        (2, 0) => 1.5,
        (2, 1) => 1.0,
        (3, 0) => 2.0,
        (3, 1) => 1.5,
        (3, 2) => 1.0,
        (4, 0) => 3.0,
        (4, 1) => 2.5,
        (5, 0) => 5.0,
        _ => 0.0,
    };

    /// <summary>Batch 472: each team's 0xDD meter as last sent (what a client that stayed shows), for a returning client.</summary>
    internal IReadOnlyDictionary<Team, int> SentScores => _sent;

    /// <summary>Batch 471: nexus damage outside the drain (a completed quest saps the other team's nexus).</summary>
    public void DamageNexus(Team team, double amount) => _sink.DamageNexus(team, amount);

    /// <summary>Runs the score for one simulation step.</summary>
    public void Tick(double now, double dt)
    {
        var orderCount = _control.ControlCount(Team.Order);
        var chaosCount = _control.ControlCount(Team.Chaos);
        _tree.SetVariable("OrderControlCount", (double)orderCount);
        _tree.SetVariable("ChaosControlCount", (double)chaosCount);
        _tree.Tick(now, dt);

        // Batch 505 (Reconstruction): every DrainIntervalSeconds the trailing team's nexus takes DrainAmount for the two counts. A tie drains
        // nothing: the shipped trees are not run for the drain (OverallScoreMath's "Rofl stomp" fallback, :3313-3324, would add 21 on a tie),
        // and the guard below and DrainAmount's default both answer 0.
        _drainClock += dt;
        if (_drainClock + DrainClockEpsilon >= DrainIntervalSeconds)
        {
            _drainClock -= DrainIntervalSeconds;
            if (orderCount != chaosCount)
                _sink.DrainNexus(orderCount > chaosCount ? Team.Chaos : Team.Order,
                    DrainAmount(Math.Max(orderCount, chaosCount), Math.Min(orderCount, chaosCount)));
        }
        SendNexusScores();
    }

    /// <summary>
    /// 0xDD: each team's whole nexus health, at the start and on every whole change, sent to every client with the real team id.
    /// Batch 496 (proven in the client and its HUD movie, DATA/Menu/VictoryPointDisplay.gfx): the meter is the team's crystal health out of
    /// a fixed 500 (the client's Init "5,500,side"), so a count-up total (DualUpdateScoreMath) is wrong here.
    /// The client itself orders the pair (own, enemy) by the local hero's team and the movie draws
    /// Order on the left and Chaos on the right for every viewer, the viewer's own team green and the other red (ScoreComponent,
    /// ColorFunctions). So the server must not swap or filter 0xDD per viewer.
    /// </summary>
    private void SendNexusScores()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var whole = (int)Math.Floor(_sink.NexusHealth(team));
            if (_sent.TryGetValue(team, out var sent) && sent == whole) continue;
            _sent[team] = whole;
            _sink.SetGameScore(team, whole);
            AnnounceThreshold(team, whole);
        }
        PlayMusicPhase();
    }

    /// <summary>
    /// Batch 747: the FMOD music cues of the Crystal Scar's phases 2 to 5: Global_Phase2 15, Global_Phase3 16, Global_Phase4 17 and (batch
    /// 821) Global_Phase5 18 in the composition's cue list of DATA/Sounds/FMOD/LoL_Audio.fev. Its scene maps each to a concurrent theme (26 to
    /// 29) whose cue the Map_CrystalScar theme's conditional links test to go on to that phase's loop. Phase 1 is Map_CrystalScar itself (cue 13,
    /// the map's aud_FMODMusicCueID), which the client starts on StartGame (0x5F): Global_Phase1 (14) has no theme in the scene.
    /// </summary>
    public static readonly uint[] MusicPhaseCues = [15, 16, 17, 18];

    /// <summary>
    /// Batch 821 (Reconstruction: retail's server code did not ship): phase 5 begins when both nexuses are at or below 70 on the meter. The
    /// wiki (Nexus, Trivia, undated): "In the Dominion mode, a unique track began to play whenever both team's Nexai were at or below 70
    /// health. This track was a more intense variant of the standard late-game track." A fan upload is titled "Dominion Music - 70 vs. 70
    /// Nexuspoints". The music data agrees that phase 5 is a variant of phase 4 (GameMusicEvents_bank00.fsb: crytalscar_phase5_loop and
    /// crytalscar_phase4_loop are both 8,176,640 samples, 185.41 s; their own crytalscar_phase4-5_drumtrans), not that the threshold is 70.
    /// No shipped script, tree or announcement names a fourth stage (the announcer speaks at 375, 250 and 125 only), on 126 or 132. The kill
    /// floor (125) is above 70: kills alone bring neither nexus to it.
    /// </summary>
    public const int FinalMusicPhaseHealth = 70;

    /// <summary>
    /// Batch 747 (Reconstruction: the owner's memory, "dominion changes the background music when one Nexus reaches 250HP", and the shipped
    /// cues): as the lower nexus passes 375, 250 and 125 (<see cref="VictoryPointThresholds"/>) every client gets the 0xE5 pair of
    /// <see cref="MusicPhaseCues"/> (<see cref="MusicCue"/>). No shipped script or tree prompts a music cue, and 0xE5 is the stock client's
    /// only way to begin one (its handler is the one call of BeginMusicCue through the audio singleton). The phase only moves forward and no
    /// cue is ended: at the end of the match the client's own StopMusic resets its music system, then it plays its victory or defeat cue
    /// itself (the 0xCE path, so a fallen nexus sends nothing here). The header names a hero, as the client routes 0xE5 to one; every client
    /// holds every hero, and the handler ignores which.
    /// Batch 804: every phase passed goes out, in order (batch 747 sent only the deepest of a step). The Map_CrystalScar theme's links
    /// (LoL_Audio.fev, the theme's segments and their links' cue conditions) go from its start, intro and phase 1 loop to any later phase's
    /// loop, but out of the phase 2, 3 and 4 loops only on the next phase's cue, so a phase whose cue never began would hold the music where
    /// it is. One threshold per step is the normal case.
    /// Batch 821: phase 5 (cue 18) when the higher nexus too is at or below <see cref="FinalMusicPhaseHealth"/>; the lower one is then below
    /// 125, so phase 4 has always gone out first, and the phase 4 loop's one conditional link is to the phase 5 loop on cue 18.
    /// </summary>
    private void PlayMusicPhase()
    {
        var lower = _sent.Values.Min();
        var stage = VictoryPointThresholds.Count(threshold => lower <= threshold) + (_sent.Values.Max() <= FinalMusicPhaseHealth ? 1 : 0);
        if (lower <= 0 || stage <= _musicStage) return;
        var passed = _musicStage;
        _musicStage = stage;
        if (_context.World.Heroes().FirstOrDefault() is not { } hero)
        {
            _context.Note("score:music-hero", "no champion to address 0xE5 to: the music phase is not sent");
            return;
        }
        if (_context.Capture?.Send is not { } send)
        {
            _context.Note("score:music-send", "this context has no packet sender: the music phase is not sent");
            return;
        }
        for (var phase = passed + 1; phase <= stage; phase++)
            foreach (var packet in MusicCue(hero.NetId, phase)) send(packet);
    }

    /// <summary>
    /// Batch 747: the current music phase's 0xE5 pair addressed to a hero, for a returning client; none in phase 1 (the client's own theme).
    /// The current phase alone: a restarted client's theme begins at its start segment, whose links reach every phase's loop on that phase's
    /// cue (LoL_Audio.fev).
    /// </summary>
    internal IReadOnlyList<PlaintextPacket> MusicPhaseCue(uint heroNetId) => _musicStage == 0 ? [] : MusicCue(heroNetId, _musicStage);

    /// <summary>
    /// Batch 804 (owner, 15 November 2026: the music still did not change natively): a phase's cue goes as a Prepare, then a Begin. Proven in
    /// the 126 client: the client (the AB build's name) opens with <c>cmp [edi+0x14], 0; je</c>, so it does
    /// nothing while the manager's cue map (<c>mMusicCues</c>, +0x14) is null; the constructor zeroes it, the theme's own
    /// start (StartThemeMusic) keeps its cue in another list (+0xC), and the one function that
    /// creates the map is PrepareMusicCue, which adds the cue's MusicCue to it. With the map in place, Begin begins the cue
    ///. Batch 747's lone Begin was dropped by every client.
    /// Reconstruction: when retail's server prepared the cues is not known; the pair is the order the client asks for ("For better
    /// performance call PrepareMusicCue before calling Begin on this cue").
    /// </summary>
    private static PlaintextPacket[] MusicCue(uint heroNetId, int phase) =>
    [
        CombatPackets.MusicCueCommand(heroNetId, MusicCueAction.Prepare, MusicPhaseCues[phase - 1]),
        CombatPackets.MusicCueCommand(heroNetId, MusicCueAction.Begin, MusicPhaseCues[phase - 1]),
    ];

    /// <summary>
    /// Batch 505: the nexus health of the victory-point announcements 1 to 3: 75 %, 50 % and 25 % of 500 (fontconfig_en_US.txt:709-720,
    /// "... has 375 / 250 / 125 nexus health remaining!"). Threshold 4 (event 98) is the quest announcement ("New Capture Point Objective!",
    /// :721-724, <see cref="CombatPackets.QuestReceived"/>), not a health threshold.
    /// </summary>
    public static readonly int[] VictoryPointThresholds = [375, 250, 125];

    /// <summary>
    /// Batch 505: the deepest threshold a team's nexus has newly reached goes out once, to every client, as 0x4A
    /// <see cref="CombatPackets.VictoryPointThreshold1"/> + stage - 1, with a champion of that team as the source.
    /// Proven in the client: its handlers for events 95 to 98 take the team of the
    /// source unit (then the unit's team) and call the HUD's OnPointThreshold("stage,side"), side 0 for the viewer's own team and
    /// 1 for the other. VictoryPointDisplay.gfx CrystalHealth.OnPointThreshold moves that team's crystal to frame
    /// stage + 1 (the crystal sprites 34 and 49 have 4 frames, stage0 to stage3, so the quest's event 98, stage 4, changes no crystal). The
    /// announcer's "@src your team | enemy team@" (Announcements_ODIN.ini:704-742) reads the same source field (the "src" functor,
    /// its global parameter), so the team whose nexus fell hears "Your team has 375 nexus health remaining!" and the other "The enemy has ...".
    /// Reconstruction: no shipped tree raises the thresholds (batch 455), so the trigger is the server's, from the texts above and the wiki
    /// (Nexus, Dominion: the announcer spoke "Whenever a Nexus reached 75% (375), 50% (250), or 25% (125) health"). Several thresholds reached in
    /// one step send only the deepest; a fallen nexus (0) sends none. A team with no champion gets none: an unknown source would shatter each
    /// viewer's enemy crystal and print the raw text key.
    /// </summary>
    private void AnnounceThreshold(Team team, int whole)
    {
        var stage = VictoryPointThresholds.Count(threshold => whole <= threshold);
        if (whole <= 0 || stage <= _stage.GetValueOrDefault(team)) return;
        _stage[team] = stage;
        if (_context.World.Heroes().FirstOrDefault(hero => hero.Team == team) is not { } source)
        {
            _context.Note("score:threshold-source", $"no {team} champion: the victory-point announcement is not sent");
            return;
        }
        if (_context.Capture?.Send is not { } send)
        {
            _context.Note("score:threshold-send", "this context has no packet sender: the victory-point announcement is not sent");
            return;
        }
        send(CombatPackets.OnEventWorld((byte)(CombatPackets.VictoryPointThreshold1 + stage - 1), source.NetId, 0, GameMaps.CrystalScar));
    }
}
