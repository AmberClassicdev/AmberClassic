using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    /// <summary>
    /// Batch 447: a team's spawn point on this map (MapDefinitions.TeamSpawn: Summoner's Rift's or the Crystal Scar's own). Any
    /// team that is not Order gets the Chaos spawn, as before.
    /// </summary>
    private static Position2 FountainOf(Team team) => MapDefinitions.TeamSpawn(team == Team.Order ? Team.Order : Team.Chaos);

    /// <summary>
    /// Called on the inhibitor edge, with the barracks and its new state
    /// (<c>true</c> = destroyed). Map1's shipped LevelScript answers the wave
    /// consequence in ApplyBarracksDestructionBonuses / ApplyBarracksRespawnReductions.
    /// </summary>
    public Action<Entity, bool>? BarracksChanged { get; set; }

    /// <summary>Schedules the normal hero respawn after lethal damage.</summary>
    public void ScheduleRespawn(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.UnitType != UnitType.Hero || unit.Alive || unit.IsTrainingDummy)
            return;
        // Batch 833: the timer of a death that started it is the value its death packet carried (Kill), read once: the death's own nexus loss
        // on the Crystal Scar sits between the two and moves the lead (DominionLeadAdjustment).
        unit.RespawnAt = _time + (_deathSecondsAtKill.Remove(unit.NetId, out var atKill) && atKill.At == _time
            ? atKill.Seconds
            : RespawnDuration(unit));
    }

    /// <summary>Batch 833: the respawn duration a hero's death packet carried, by net id, with the time of that death (<see cref="ScheduleRespawn"/>).</summary>
    private readonly Dictionary<uint, (double At, double Seconds)> _deathSecondsAtKill = new();

    /// <summary>
    /// Batch 771: the end of a zombie life (BBForceDead, or a kill in the life): a hero whose death did not start its timer (the map's
    /// DeathTimeSettings*StartDeathTimerForZombies off, the 1.0.0.132 Crystal Scar; V1.0.0.131: "zombie-state units start respawn timers when
    /// the zombie state ends") starts it now. A timer the death already started is kept.
    /// </summary>
    public void StartZombieDeathTimer(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.RespawnAt <= 0.0) ScheduleRespawn(unit);
    }

    /// <summary>
    /// Ends Annie's Tibbers and Yorick's ghouls (Omen of War, Pestilence and Famine) at their owner's death; the same
    /// <see cref="Kill"/> path as any pet death sends their death packet, effects and buff removals, and no one is credited.
    /// <para>
    /// Tibbers, retail: InfernalGuardianBurning tests on every update whether its Attacker, Annie, is dead and removes itself, and
    /// its OnBuffDeactivate then deals 10000 InternalRaw damage to Tibbers; the LoL wiki Annie patch history (V0.8.22.115) says
    /// "Tibbers now dies after 60 seconds or when Annie dies", unchanged until V6.9.
    /// </para>
    /// <para>
    /// Yorick's ghouls, retail: they die with Yorick (owner research 18 Oct 2026). This replaces the earlier 18 October rule that
    /// let them live out their decay.
    /// </para>
    /// <para>
    /// Owner decision, 18 October 2026 (owner's information): no other pet dies with its owner. Shaco's Hallucinate clone and the
    /// Omen of Death revenant live out their own duration (the clone's 18 s, the revenant's 10 s) and keep running their Pet.lua AI,
    /// which never tests the owner's life; only a nil owner kills a pet there. Reconstruction: for the revenant the web research of
    /// 18 October 2026 found no 2011 source tying it to Yorick's death and the scripts have no death link; the clone rests on the
    /// owner's information, consistent with the Shaco patch history. Nothing else here ties a pet to its owner: Jack in the Box,
    /// Nidalee's Bushwhack traps (a 240 s lifetime and no owner test) and wards never died with the owner either.
    /// </para>
    /// </summary>
    public int DespawnPetsOf(uint ownerNetId)
    {
        var pets = _world.Entities.Values
            .Where(candidate => candidate.OwnerNetId == ownerNetId &&
                                candidate.PetState is { Kind: Pets.CloneKind.Tibbers or Pets.CloneKind.YorickGhoul } &&
                                candidate.Alive)
            .ToArray();
        foreach (var pet in pets) Kill(pet, 0);
        return pets.Length;
    }

    /// <summary>
    /// The barracks' countdown to its dampener's return, armed by the map script's <c>SetDisableMinionSpawn</c> (the barracks record holds the countdown
    /// seconds and a flag). The shared obj_Barracks update counts it down and at zero sets the linked
    /// dampener's state to 1 unless it already is; <see cref="ProcessRespawns"/> is that update's zero test here.
    /// </summary>
    public void ArmInhibitorReturn(Entity inhibitor, double seconds)
    {
        ArgumentNullException.ThrowIfNull(inhibitor);
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        inhibitor.RespawnAt = _time + seconds;
    }

    public int ProcessRespawns()
    {
        // End-game ceremony/result packets continue through CombatTick, but a
        // death whose timer expires during that ceremony must not re-enter the
        // match after the Nexus has already ended it.
        if (_gameOver) return 0;
        var respawned = 0;
        foreach (var unit in _world.Entities.Values.ToArray())
        {
            if (unit.Alive || unit.RespawnAt <= 0.0 || unit.RespawnAt > _time) continue;
            if (unit.UnitType == UnitType.Hero)
            {
                if (RespawnImmediately(unit)) respawned++;
                continue;
            }
            unit.Alive = true;
            unit.RespawnAt = 0.0;
            unit.Health = unit.MaxHealth;
            unit.Mana = unit.MaxMana;
            if (unit.Kind == EntityKind.Inhibitor)
            {
                BarracksChanged?.Invoke(unit, false);
                if (_transport.IsWireReady(unit)) _transport.SendInhibitorState(unit, false, 0.0);
            }
            _hooks.OnRespawn(unit);
            respawned++;
        }

        foreach (var unit in _world.Entities.Values.ToArray())
        {
            if (!unit.Alive && unit.DeathTime is double death && unit.RespawnAt <= 0.0 &&
                unit.UnitType is not UnitType.Turret and not UnitType.Structure &&
                _time - death >= CorpseReapSeconds)
                _world.Remove(unit.NetId);
        }
        RecomputeProtection();
        return respawned;
    }

    /// <summary>
    /// Applies the same hero state transition as ProcessRespawns without
    /// advancing the match clock or creating a kill/reward event.
    /// </summary>
    public bool RespawnImmediately(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.UnitType != UnitType.Hero || unit.Alive || unit.RespawnAt <= 0.0)
            return false;
        _deathRecapHistories.Remove(unit.NetId);
        _lastDeathRecaps.Remove(unit.NetId);
        unit.Alive = true;
        unit.RespawnAt = 0.0;
        unit.DeathTime = null;
        unit.MatchStatistics.RecordRespawn(_time);
        unit.Health = unit.MaxHealth;
        // The hero's reincarnation handler sets the pool to 0 for a Shield or Other character (rage, heat, flow, Mordekaiser's shield): a life
        // starts with an empty bar there, and with a full one for mana and energy.
        unit.Mana = _gameData.Champion(unit.StatCharacterName).PrimaryResource.StartsLifeEmpty() ? 0.0 : unit.MaxMana;
        // Owner verification, 18 October 2026: a retail video shows respawned champions appearing in the middle of the
        // fountain, so every seat respawns on its team's spawn point, not on a per-seat HeroSpawnOffsets slot (those
        // slots are the first spawn only, MapDefinitions.InitialHeroSpawn). The spawn point is the central point of the map's
        // Scene/__Spawn_T1.SCB and __Spawn_T2.SCB (MapLayout, read from the player's files since batch 595). Owner decision, 18
        // October 2026: the Order anchor is that exact point (MapDefinitions.HeroSpawn), no longer rounded to whole units.
        // The hero's respawn-location getter is a stub in the client (server only).
        unit.Position = unit.RespawnPoint ?? FountainOf(unit.Team);
        var stats = _heroStats ?? throw new InvalidOperationException(
            "hero respawns require the match HeroStatsService");
        stats.Refresh(unit);
        if (_transport.IsWireReady(unit))
        {
            _transport.SendRespawn(unit);
            _transport.SendHeroStats(unit);
        }
        _hooks.OnRespawn(unit);
        RecomputeProtection();
        return true;
    }

    /// <param name="startsTimer">
    /// False for the death packet of a death whose timer does not start yet (batch 771: a zombie life on a map whose
    /// StartDeathTimerForZombies is off): the duration is read without moving the team's respawn wave, which the timer's real start
    /// (<see cref="StartZombieDeathTimer"/>) does.
    /// </param>
    private double RespawnDuration(Entity unit, bool startsTimer = true)
    {
        // Retail: stock respawn-time getter is
        //   max(0, (FlatTimeDead + FlatTimeDeadPerLevel * level + base) *
        //          (1 - (PercentTimeDead + ScriptPercentRespawn + level * PercentTimeDeadPerLevel)))
        // with base = the death timer manager's lookup (team, level) and the
        // mods applied only while DeathTimes' AllowDeathTimeMods (absent = on) holds.
        // The percent terms are the rune/mastery sum (rPercentTimeDeadMod, rune 5366
        // +0.05) and the script term (IncPercentRespawnTimeMod). Batch 159 part 3: that script term is
        // Spells/PositiveChampionDelta.luaobj's own row, summed into HeroStats.TimeDeadPercent with every other buff payload,
        // so the hand-written TeamSizeCatchUp.RespawnPercent reader that stood in for it is gone.
        // The flat terms (rFlatTimeDeadMod*) are 0 in every shipped item, rune and
        // talent (scanned), so they are not modelled. Only the result is floored at
        // 0: a negative sum lengthens the timer, a sum above 1 gives 0.
        // gcd_PercentRespawnTimeModMinimum (-0.95) is copied by the client and never applied.
        // Batch 454: AllowDeathTimeMods (map 8 ships it on, Map1 leaves it absent = on) gates the mods; the base and the scaling are the map's.
        // Batch 584: the base is the team's respawn wave (MapProgressionRules.DeathTime with the wave start; map 8 only). Batch 833: read once
        // per death (Kill keeps the value its death packet carries for ScheduleRespawn), with the Crystal Scar's nexus-lead adjustment.
        var reduction = _progression.AllowDeathTimeMods ? RespawnReduction(unit) : 0.0;
        var waveStart = _deathWaveStarts.GetValueOrDefault(unit.Team);
        var (flat, window) = DominionLeadAdjustment(unit.Team);
        var deathTime = _progression.DeathTime(unit.Level, _time, ref waveStart, flat, window);
        if (startsTimer) _deathWaveStarts[unit.Team] = waveStart;
        return Math.Max(0.0, deathTime * (1.0 - reduction));
    }

    /// <summary>Each team's respawn-wave start (GlobalDeathTimerManager keeps one DeathTimerManager per team, +0x60).</summary>
    private readonly Dictionary<Team, double> _deathWaveStarts = new();

    /// <summary>Batch 833: the seconds a nexus lead adds to the leading team's minimum respawn time and takes off the trailing team's (V1.0.0.128).</summary>
    public const double DominionLeadRespawnSeconds = 2.0;

    /// <summary>
    /// Batch 833: the Crystal Scar's respawn adjustment by the nexus lead, (seconds added to the minimum respawn time, seconds added to the
    /// respawn window). V1.0.0.128: "Respawn time adjustments are now +2/-2 seconds regardless of the size in score difference." The rule it
    /// replaced is Riot's own explanation (Brackhar, Technical Designer, values of 21 October 2011, "Dominion Respawn Explained", Surrender at
    /// 20, 25 October 2011): with a lead of one point or more the leading team's minimum respawn time grows by 2 s and its window by half of
    /// that, the trailing team's minimum shrinks by 2 s with no window change, and leads of 50 and 100 points widened it to 3 and 4 s; 1.0.0.128
    /// keeps the 2 s for every lead. The rule is server code: lol132.exe's GetDeathTime and GetRespawnTime have
    /// the 126 shape and no shipped file carries it.
    /// Reconstruction: the score is the nexus health the 0xDD meter shows (<see cref="DominionNexusHealth(Team)"/>), compared at the death,
    /// and the adjustment is added to the base before the time-dead mods, as the minimum respawn time the explanation names.
    /// </summary>
    private (double Flat, double Window) DominionLeadAdjustment(Team team)
    {
        if (_gameData.Paths.MapId != GameMaps.CrystalScar || team is not (Team.Order or Team.Chaos)) return (0.0, 0.0);
        var lead = DominionNexusHealth(team) - DominionNexusHealth(team == Team.Order ? Team.Chaos : Team.Order);
        return lead >= 1.0 ? (DominionLeadRespawnSeconds, DominionLeadRespawnSeconds / 2.0)
            : lead <= -1.0 ? (-DominionLeadRespawnSeconds, 0.0)
            : (0.0, 0.0);
    }

    private static double RespawnReduction(Entity hero) => hero.LoadoutRespawnTimePercent;
}
