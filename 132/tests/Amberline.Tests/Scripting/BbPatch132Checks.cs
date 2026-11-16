using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 769: the script engine against the 1.0.0.132 game data (master's default data root, the 132_Game folder). The natives, constants,
/// comparison, status and hook the 132 scripts use that no 126 script did, each run the way its shipped call site runs it, and the seven
/// champions the 132 data adds. Expected values are the 132 client's (lol132.exe, cited per check) and the shipped scripts' literals.
/// </summary>
public static class BbPatch132Checks
{
    // ------------------------------------------------------------------------------------------------------------
    // Helpers: run block trees in the match's unit-character host (the host of the map's own units), inside a frame
    // ------------------------------------------------------------------------------------------------------------

    private static BbSpellHost UnitHost(Match match) =>
        (BbSpellHost)typeof(BbUnitCharacterModule).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(match.Runtime.Spells.UnitScripts)!;

    private static void Run(Match match, Entity owner, BbTable vars, params BbBlock[] blocks)
    {
        var host = UnitHost(match);
        host.Push(new BbHookFrame("CheckBlocks", "BbPatch132Checks", owner));
        try { new BbInterpreter(host).Execute(blocks, new BbScope(vars)); }
        finally { host.Pop(); }
    }

    private static BbBlock Block(string function, params (string Key, BbValue Value)[] fields) =>
        new(function, fields.Select(field => new BbField(field.Key, field.Value)).ToArray(), []);

    private static BbBlock Block(string function, BbBlock[] subBlocks, params (string Key, BbValue Value)[] fields) =>
        new(function, fields.Select(field => new BbField(field.Key, field.Value)).ToArray(), subBlocks);

    private static BbValue Txt(string value) => new BbValue.Text(value);
    private static BbValue Num(double value) => new BbValue.Number(value);
    private static BbValue Ref(string name) => new BbValue.Reference(name);
    private static BbValue Flag(bool value) => new BbValue.Bool(value);

    private static BbTable Vars(params (string Key, object? Value)[] entries)
    {
        var table = new BbTable();
        foreach (var (key, value) in entries) table[key] = value;
        return table;
    }

    private static Entity Monster(Match match, string character, Position2 at, Team team = Team.Neutral, EntityKind kind = EntityKind.Neutral)
    {
        var data = GameData.Default.Champion(character);
        var unit = new Entity(match.Runtime.World.Allocate(UnitType.Monster), kind, UnitType.Monster, team, at)
        {
            Name = character, ChampionName = character, Health = data.BaseHp, MaxHealth = data.BaseHp, AttackDamage = data.BaseDamage,
            Speed = data.MoveSpeed, Created = true, Visible = true,
        };
        match.Runtime.World.Register(unit);
        return unit;
    }

    private static IEnumerable<BbScript> Corpus()
    {
        var paths = GameData.Default.Paths;
        var data = Path.Combine(paths.Root, "DATA");
        var files = new List<string>();
        foreach (var area in new[] { "Spells", "Items", "Talents" })
            files.AddRange(Directory.EnumerateFiles(Path.Combine(data, area), "*.luaobj", SearchOption.AllDirectories));
        foreach (var character in Directory.EnumerateDirectories(Path.Combine(data, "Characters")))
            if (Directory.Exists(Path.Combine(character, "Scripts")))
                files.AddRange(Directory.EnumerateFiles(Path.Combine(character, "Scripts"), "*.luaobj", SearchOption.AllDirectories));
        foreach (var file in files)
            if (BbScriptLoader.Load(paths, Path.GetRelativePath(data, file).Replace(Path.DirectorySeparatorChar, '/')).Script is { } script)
                yield return script;
    }

    private static IEnumerable<string> References(BbValue? value) => value switch
    {
        BbValue.Reference reference => [reference.Name],
        BbValue.Table table => table.Items.SelectMany(References).Concat(table.Fields.SelectMany(field => References(field.Value))),
        _ => [],
    };

    // ------------------------------------------------------------------------------------------------------------
    // Corpus: every block function and every reference of the 132 scripts is one the engine runs
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every block function the 2600 shipped 132 scripts call is a base-library function or a bound native (the f-scripts measurement: 187 functions,
    /// 45 base, 134 catalogued natives and 8 that were in no catalogue), and every global they reference resolves except the four names retail
    /// never defines (BUFF_Shred, CO_IS_CLONE and SetImmovable were the three new unresolved ones).
    /// </summary>
    [Check("scripting.p132-corpus-functions-and-references-resolve")]
    public static void EveryFunctionAndReferenceOfThe132CorpusResolves()
    {
        var functions = new SortedSet<string>(StringComparer.Ordinal);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var scripts = 0;
        foreach (var script in Corpus())
        {
            scripts++;
            foreach (var (_, block) in script.Walk())
            {
                functions.Add(block.Function);
                foreach (var name in block.Params.SelectMany(field => References(field.Value)))
                    if (BbRuntime.ResolveReference(name) is null) unresolved.Add(name);
            }
        }
        TestAssert.Equal(2598, scripts, "the 132 scripts under Spells, Items, Talents and Characters/*/Scripts");
        var missing = functions.Where(name => !BbBaseLibrary.TryGet(name, out _) &&
            (BbNativeCatalog.OwnerOf(name) is null || !BbNativeHandlers.IsBound(name))).ToArray();
        TestAssert.Equal(Array.Empty<string>(), missing, "block functions neither in the base library nor bound: " + string.Join(", ", missing));
        foreach (var name in new[] { "BBIncPermanentExpReward", "BBIncPermanentGoldReward", "BBSetSlotSpellIcon", "BBSpellBuffRenew",
                     "BBSetAutoAcquireTargets", "BBCreateItem", "BBForEachVisibleUnitInTargetAreaRandom", "BBUpdateCanCast" })
            TestAssert.True(functions.Contains(name) && BbNativeHandlers.IsBound(name), name + " is called by the 132 scripts and bound");
        // Block parameters only (the 126 corpus's global "False", VayneCondemnMissile's DoesntBreakShields, is not a block parameter).
        TestAssert.Equal("NoTargetYet,NoValidTarget,True", string.Join(",", unresolved), "the only unresolved references are names retail never defines");
        TestAssert.True(unresolved.All(BbConstants.UndefinedInRetail.Contains), "every one is in BbConstants.UndefinedInRetail");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Constants: the 132 buff types (BUFF_SpellShield 4 inserted, BUFF_Shred 0x1B) and DAMAGESOURCE_PET
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The BUFF_* registration of lol132.exe: BUFF_SpellShield 4 is new, Stun to AmmoStack moved up by one, BUFF_Shred is 0x1B;
    /// DAMAGESOURCE_PET 0xB is new. The server's wire enum (BuffType, the byte of the buff add) carries the same numbers.
    /// </summary>
    [Check("scripting.p132-buff-types-are-the-132-registration")]
    public static void BuffTypesAreThe132Registration()
    {
        var registration = new (string Name, double Value, BuffType Wire)[]
        {
            ("BUFF_Internal", 0, BuffType.Internal), ("BUFF_Aura", 1, BuffType.Aura), ("BUFF_CombatEnchancer", 2, BuffType.CombatEnhancer),
            ("BUFF_CombatDehancer", 3, BuffType.CombatDehancer), ("BUFF_SpellShield", 4, BuffType.SpellShield), ("BUFF_Stun", 5, BuffType.Stun),
            ("BUFF_Invisibility", 6, BuffType.Invisibility), ("BUFF_Silence", 7, BuffType.Silence), ("BUFF_Pacified", 7, BuffType.Silence),
            ("BUFF_Disarm", 7, BuffType.Silence), ("BUFF_Taunt", 8, BuffType.Taunt), ("BUFF_Polymorph", 9, BuffType.Polymorph),
            ("BUFF_Slow", 10, BuffType.Slow), ("BUFF_Snare", 11, BuffType.Snare), ("BUFF_Damage", 12, BuffType.DamageOverTime),
            ("BUFF_Heal", 13, BuffType.Heal), ("BUFF_Haste", 14, BuffType.Haste), ("BUFF_SpellImmunity", 15, BuffType.SpellImmunity),
            ("BUFF_PhysicalImmunity", 16, BuffType.PhysicalImmunity), ("BUFF_Invulnerability", 17, BuffType.Invulnerability),
            ("BUFF_Sleep", 18, BuffType.Sleep), ("BUFF_NearSight", 19, BuffType.NearSight), ("BUFF_Frenzy", 20, BuffType.Frenzy),
            ("BUFF_Fear", 21, BuffType.Fear), ("BUFF_Net", 22, BuffType.Net), ("BUFF_Poison", 23, BuffType.Poison),
            ("BUFF_Suppression", 24, BuffType.Suppression), ("BUFF_Blind", 25, BuffType.Blind), ("BUFF_AmmoStack", 26, BuffType.AmmoStack),
            ("BUFF_Shred", 27, BuffType.Shred),
        };
        foreach (var (name, value, wire) in registration)
        {
            TestAssert.True(BbConstants.TryGetValue(name, out var number), name + " is a constant");
            TestAssert.Equal(value, number, name);
            TestAssert.Equal(wire, BbBuffRuntime.ServerBuffType(number), name + " on the wire");
        }
        TestAssert.True(BbConstants.TryGetValue("DAMAGESOURCE_PET", out var pet) && pet == 0xB, "DAMAGESOURCE_PET 0xB");
        // Batch 780: the Lua AI's BUFF_Taunt (Hero.luaobj's SpellBuffRemoveType) is the same 132 number, not 126's 7 (Silence in 132).
        TestAssert.Equal(8.0, Amberline.Simulation.Ai.AiGlobals.Table.Single(item => item.Name == "BUFF_Taunt").Value, "the AI global BUFF_Taunt");
        // The shipped shreds moved from CombatDehancer to Shred (Items/3071 BlackCleaver), and the dispel sweep keeps taking them.
        var cleaver = BbScriptLoader.Load(GameData.Default.Paths, "Items/3071.luaobj").Script!.Walk()
            .First(item => item.Block.Function == "BBSpellBuffAdd" && item.Block.Text("BuffName") == "BlackCleaver").Block;
        TestAssert.Equal("BUFF_Shred", ((BbValue.Reference)cleaver.Param("BuffType")!).Name, "Black Cleaver's armor reduction is a shred in 132");
    }

    // ------------------------------------------------------------------------------------------------------------
    // CO_IS_CLONE / CO_IS_NOT_CLONE
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// CO_IS_CLONE is <c>IsClone(value1)</c> (132 BuildingBlocksBase proto 22) and CO_IS_NOT_CLONE its negation (proto 23); IsClone (lol132.exe
    /// The client) answers whether an AI unit's clone source is set and is another unit. A BBCloneUnitPet clone is one; Tibbers (BBSpawnPet writes
    /// no clone link), a hero and a non-unit are not. Sejuani's scripts branch on it (SejuaniFrost: a clone gets the champion's indicator).
    /// </summary>
    [Check("scripting.p132-co-is-clone")]
    public static void CoIsCloneAnswersForBbCloneUnitPetClonesOnly()
    {
        using var match = new Match("Sejuani");
        var host = UnitHost(match);
        var clone = Monster(match, "Shaco", new Position2(7400, 7000), Team.Chaos, EntityKind.ShacoClone);
        clone.PetState = new PetState(CloneKind.ShacoClone, match.Foe.NetId) { CloneSourceNetId = match.Foe.NetId };
        var tibbers = Monster(match, "AnnieTibbers", new Position2(7500, 7000), Team.Chaos, EntityKind.Pet);
        tibbers.PetState = new PetState(CloneKind.Tibbers, match.Foe.NetId);
        var selfLinked = Monster(match, "Shaco", new Position2(7600, 7000), Team.Chaos, EntityKind.ShacoClone);
        selfLinked.PetState = new PetState(CloneKind.ShacoClone, match.Foe.NetId) { CloneSourceNetId = selfLinked.NetId };
        TestAssert.True(BbBaseLibrary.Compare(host, BbComparison.CO_IS_CLONE, clone, null), "a clone of a champion is a clone");
        TestAssert.False(BbBaseLibrary.Compare(host, BbComparison.CO_IS_NOT_CLONE, clone, null), "and not a non-clone");
        foreach (var (unit, what) in new (object?, string)[] { (tibbers, "Tibbers"), (match.Foe, "a hero"), (selfLinked, "a unit linked to itself"), (null, "nil") })
        {
            TestAssert.False(BbBaseLibrary.Compare(host, BbComparison.CO_IS_CLONE, unit, null), what + " is no clone");
            TestAssert.True(BbBaseLibrary.Compare(host, BbComparison.CO_IS_NOT_CLONE, unit, null), what + " is a non-clone");
        }
        // The shipped shape: BBIf/BBElseIf with CompareOp CO_IS_CLONE (SejuaniFrost's OnBuffActivate) runs through the base library.
        foreach (var (unit, expected) in new[] { (clone, true), (tibbers, false) })
        {
            var vars = Vars(("Unit", unit));
            Run(match, match.Hero, vars, Block("BBIf", [Block("BBSetVarInTable", ("DestVar", Txt("Hit")), ("SrcValue", Flag(true)))],
                ("Src1Var", Txt("Unit")), ("CompareOp", Ref("CO_IS_CLONE"))));
            TestAssert.Equal(expected, vars["Hit"] is true, $"BBIf CO_IS_CLONE on {unit.Name}");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // SetImmovable and the 132 status word
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// BBSetStatus SetImmovable sets bit 3 of the unit's status word and leaves CanMove alone; GetCanMove reads CanMove and
    /// not Immovable, so the unit cannot move. Every bit from Stealthed up is one higher in the 132 word (GetStealthed 4, GetNoRender 14,...).
    /// CharScriptWorm's CharOnActivate is the shipped call (with CharScriptOdinNeutralGuardian's).
    /// </summary>
    [Check("scripting.p132-set-immovable-and-the-132-status-word")]
    public static void SetImmovableSetsBitThreeAndTheWordIsThe132Layout()
    {
        TestAssert.Equal(1u << 3, CharacterState.ImmovableBit, "Immovable is bit 3");
        TestAssert.Equal(1u << 11, CharacterState.GhostedBit, "Ghosted is bit 11 (10 in 126)");
        TestAssert.Equal(1u << 12, CharacterState.GhostProofBit, "GhostProof is bit 12 (11 in 126)");
        TestAssert.Equal(1u << 14, CharacterState.NoRenderBit, "NoRender is bit 14 (13 in 126)");
        TestAssert.Equal(1u << 15, CharacterState.ForceRenderParticlesBit, "ForceRenderParticles is bit 15 (14 in 126)");

        using var match = new Match("Annie");
        var unit = Monster(match, "TestCube", new Position2(5000, 5000));
        TestAssert.Equal(7u, CharacterState.ActionWord(unit), "a fresh unit holds CanAttack, CanCast and CanMove");
        Run(match, unit, Vars(("Unit", unit)), Block("BBSetStatus", ("TargetVar", Txt("Unit")), ("SrcValue", Flag(true)), ("Status", Ref("SetImmovable"))));
        TestAssert.Equal(7u | CharacterState.ImmovableBit, CharacterState.ActionWord(unit), "SetImmovable adds bit 3 and keeps CanMove set");
        TestAssert.False(CharacterState.Of(unit).CanMove, "an immovable unit cannot move (GetCanMove)");
        TestAssert.True(BuffLifecycle.Rooted(unit), "the movement gates hold it");
        Run(match, unit, Vars(("Unit", unit)), Block("BBSetStatus", ("TargetVar", Txt("Unit")), ("SrcValue", Flag(false)), ("Status", Ref("SetImmovable"))));
        TestAssert.Equal(7u, CharacterState.ActionWord(unit), "SetImmovable false releases it");
        Run(match, unit, Vars(("Unit", unit)), Block("BBSetStatus", ("TargetVar", Txt("Unit")), ("SrcValue", Flag(true)), ("Status", Ref("SetStealthed"))));
        TestAssert.Equal(7u | (1u << 4) | CharacterState.GhostedBit, CharacterState.ActionWord(unit), "SetStealthed is bit 4 with Ghosted 11 in the 132 word");

        // The shipped call site: the Baron's character script (bound as the map binds it) makes it immovable at its activation.
        var worm = Monster(match, "Worm", new Position2(4600, 10200));
        TestAssert.True(match.Runtime.Spells.UnitScripts!.BindUnit(worm, "Worm"), "the Worm binds its CharScriptWorm");
        TestAssert.True((CharacterState.ActionWord(worm) & CharacterState.ImmovableBit) != 0, "CharScriptWorm's CharOnActivate sets SetImmovable");
        TestAssert.Equal(0, match.Runtime.Spells.UnitScripts.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", match.Runtime.Spells.UnitScripts.ScriptFailures.Select(failure => $"{failure.Script}.{failure.Hook}: {failure.Message}")));
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBIncPermanentExpReward / BBIncPermanentGoldReward
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The camp monsters' growing rewards (V1.0.0.130): BBIncPermanentExpReward and BBIncPermanentGoldReward
    /// add Delta to the unit's experience and gold reward. CharScriptGiantWolf's CharOnActivate adds 14 and 6; GlobalMonsterBuff, which it adds,
    /// adds the reward of the monster's life before its spawn time (101 s), nothing at the start of a match.
    /// </summary>
    [Check("scripting.p132-inc-permanent-rewards")]
    public static void CampMonstersGrowTheirRewards()
    {
        using var match = new Match("Annie");
        var wolf = Monster(match, "GiantWolf", new Position2(3500, 6500));
        wolf.GoldReward = 50.0;
        wolf.ExperienceReward = 100.0;
        TestAssert.True(match.Runtime.Spells.UnitScripts!.BindUnit(wolf, "GiantWolf"), "the wolf binds its CharScriptGiantWolf");
        TestAssert.Near(56.0, wolf.GoldReward, 1e-6, "CharOnActivate: BBIncPermanentGoldReward Delta 6");
        TestAssert.Near(114.0, wolf.ExperienceReward, 1e-6, "CharOnActivate: BBIncPermanentExpReward Delta 14");
        TestAssert.True(wolf.Buffs.Any(buff => buff.Name == "GlobalMonsterBuff"), "the growth buff is on the wolf");
        TestAssert.Equal(0, match.Runtime.Spells.UnitScripts.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", match.Runtime.Spells.UnitScripts.ScriptFailures.Select(failure => $"{failure.Script}.{failure.Hook}: {failure.Message}")));

        // The variable form GlobalMonsterBuff uses (DeltaVar in InstanceVars).
        var vars = Vars(("Owner", wolf), ("InstanceVars", Vars(("goldPerMinute", 0.43), ("expPerMinute", 1.5))));
        Run(match, wolf, vars,
            Block("BBIncPermanentExpReward", ("TargetVar", Txt("Owner")), ("DeltaVar", Txt("expPerMinute")), ("DeltaVarTable", Txt("InstanceVars")), ("Delta", Num(0))),
            Block("BBIncPermanentGoldReward", ("TargetVar", Txt("Owner")), ("DeltaVar", Txt("goldPerMinute")), ("DeltaVarTable", Txt("InstanceVars")), ("Delta", Num(0))));
        NearF32(56.43, wolf.GoldReward, "gold per minute");
        NearF32(115.5, wolf.ExperienceReward, "experience per minute");

        // The base the scripts raise or lower: the 132 Summoner's Rift NeutralMinionSpawn pays no experience (its column is 0) and the record carries
        // it, so a camp member spawns with its ExpGivenOnDeath (wolf 15, which CharScriptwolf lowers by 5; SmallGolem 50, by 12).
        foreach (var (name, record) in new[] { ("wolf", 15.0), ("SmallGolem", 50.0), ("Golem", 93.0), ("GiantWolf", 114.0) })
            TestAssert.Near(record, Amberline.Simulation.Neutral.NeutralStatsCalculator.Calculate(GameData.Default, name).ExperienceReward, 1e-9,
                name + " spawns with its record's experience");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSetSlotSpellIcon
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// BBSetSlotSpellIcon sends ChangeSlotSpellIcon 0x36 with the stored slot and IconIndex - 1, to the unit's own client.
    /// FizzSeastonePassive switches Fizz's W icon to 2 while it lasts (OnBuffActivate) and back to 1 (OnBuffDeactivate).
    /// </summary>
    [Check("scripting.p132-set-slot-spell-icon")]
    public static void SlotIconGoesToTheOwnersClient()
    {
        var packet = SpellPackets.ChangeSlotSpellIcon(0x40000001, 1, 1).Plaintext.ToArray();
        TestAssert.Equal(new byte[] { 0x36, 0x01, 0x00, 0x00, 0x40, 0x01, 0x01 }, packet, "0x36: unit, slot, icon");
        TestAssert.Equal((byte)0x83, SpellPackets.ChangeSlotSpellIcon(0x40000001, 3, 0, summoner: true).Plaintext.Span[5], "the summoner book's bit 7");

        using var match = new Match("Fizz");
        var own = Observe(match, Team.Order, match.Hero, 9101);
        var foe = Observe(match, Team.Chaos, match.Foe, 9102);
        match.Runtime.Delivery.Flush();
        own.Packets.Clear();
        foe.Packets.Clear();
        match.Module.Buffs!.Add(new BbBuffRequest(match.Hero, match.Hero, "FizzSeastonePassive", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_Internal, 1, 3.0, 0.0, false, new BbTable()));
        match.Advance(0.1);
        TestAssert.Equal((byte)1, match.Hero.SpellSlotIcons[1], "W's icon byte is IconIndex 2 - 1");
        var icons = own.Packets.Where(entry => entry.Packet.Plaintext.Span[0] == 0x36).Select(entry => entry.Packet.Plaintext.ToArray()).ToArray();
        TestAssert.Equal(1, icons.Length, "one 0x36 to Fizz's client");
        TestAssert.Equal(new byte[] { 1, 1 }, icons[0][5..], "slot 1, icon 1");
        TestAssert.False(foe.Packets.Any(entry => entry.Packet.Plaintext.Span[0] == 0x36), "no 0x36 to another client");
        match.Advance(3.2);
        TestAssert.Equal((byte)0, match.Hero.SpellSlotIcons[1], "the deactivation puts IconIndex 1 back");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSpellBuffRenew
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// BBSpellBuffRenew finds the target's buff of that name, whoever cast it, and resets its timer: start = now, end = now +
    /// ResetDuration. Graves keeps GravesPassiveGrit at 4 s this way; Sejuani adds 0.5 s to her Frost.
    /// </summary>
    [Check("scripting.p132-spell-buff-renew")]
    public static void RenewRestartsTheNamedBuffsTimer()
    {
        using var match = new Match("Graves");
        match.Module.Buffs!.Add(new BbBuffRequest(match.Hero, match.Foe, "GravesPassiveGrit", (int)BbBuffAddType.BUFF_STACKS_AND_RENEWS,
            (int)BbBuffType.BUFF_CombatEnchancer, 10, 1.0, 0.0, false, new BbTable()));
        match.Advance(0.5);
        var grit = Buff(match.Hero, "GravesPassiveGrit")!;
        TestAssert.True(grit.ExpiresAt is > 0.4 and < 0.6, $"the buff has about half a second left ({grit.ExpiresAt:0.000})");
        Run(match, match.Hero, Vars(("Owner", match.Hero)),
            Block("BBSpellBuffRenew", ("TargetVar", Txt("Owner")), ("BuffName", Txt("GravesPassiveGrit")), ("ResetDuration", Num(4))));
        TestAssert.Near(4.0, Buff(match.Hero, "GravesPassiveGrit")!.ExpiresAt, 1e-6, "renewed to the full 4 s, whatever its caster");
        Run(match, match.Hero, Vars(("Owner", match.Foe)),
            Block("BBSpellBuffRenew", ("TargetVar", Txt("Owner")), ("BuffName", Txt("GravesPassiveGrit")), ("ResetDuration", Num(4))));
        TestAssert.True(Buff(match.Foe, "GravesPassiveGrit") is null, "a target without the buff is left alone");
        // The shipped call site: CharScriptGraves renews the grit in CharOnPreDealDamage, so a swing at the foe puts it back at 4 s.
        match.Advance(1.0);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSetAutoAcquireTargets, BBCreateItem, BBUpdateCanCast, BBForEachVisibleUnitInTargetAreaRandom
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>BBSetAutoAcquireTargets is the hero's idle acquisition switch: Twisted Fate holds a locked card with it.</summary>
    [Check("scripting.p132-set-auto-acquire-targets")]
    public static void AutoAcquireSwitchFollowsTheScript()
    {
        using var match = new Match("TwistedFate");
        TestAssert.True(match.Hero.CanAutoAcquireTargets, "a hero acquires by default");
        Run(match, match.Hero, Vars(("Owner", match.Hero)), Block("BBSetAutoAcquireTargets", ("TargetVar", Txt("Owner")), ("Value", Flag(false))));
        TestAssert.False(match.Hero.CanAutoAcquireTargets, "Value false stops the idle acquisition");
        Run(match, match.Hero, Vars(("Owner", match.Hero)), Block("BBSetAutoAcquireTargets", ("TargetVar", Txt("Owner")), ("Value", Flag(true))));
        TestAssert.True(match.Hero.CanAutoAcquireTargets, "Value true restores it");
    }

    /// <summary>BBCreateItem (a bare return in the client; the free grant): CharScriptViktor's CharOnActivate gives every Viktor the Prototype Hex Core.</summary>
    [Check("scripting.p132-create-item")]
    public static void ViktorStartsWithTheHexCore()
    {
        using var match = new Match("Viktor");
        TestAssert.True(match.Hero.Inventory.Any(slot => slot?.ItemId == 3200), "Viktor holds item 3200 from his CharOnActivate");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
    }

    /// <summary>
    /// BBUpdateCanCast re-runs a hero's CanCast gates into its castable bits: the engine pushes the hero's stat block, which
    /// carries them. VolibearW calls it on each hit.
    /// </summary>
    [Check("scripting.p132-update-can-cast")]
    public static void UpdateCanCastPushesTheHerosStats()
    {
        using var match = new Match("Volibear");
        var own = Observe(match, Team.Order, match.Hero, 9103);
        match.Runtime.Delivery.Flush();
        own.Packets.Clear();
        Run(match, match.Hero, Vars(("Attacker", match.Hero)), Block("BBUpdateCanCast", ("TargetVar", Txt("Attacker"))));
        match.Runtime.Delivery.Flush();
        TestAssert.True(own.Packets.Count > 0, "the hero's stat block goes out at once");
        Run(match, match.Hero, Vars(("Attacker", match.Foe), ("Minion", Monster(match, "TestCube", new Position2(7100, 7000)))),
            Block("BBUpdateCanCast", ("TargetVar", Txt("Minion"))));
    }

    /// <summary>
    /// BBForEachVisibleUnitInTargetAreaRandom: the random pick of BBForEachUnitInTargetAreaRandom over the units the Attacker's team can see (the Visible
    /// thunk pushes 0 to the shared body, the plain one IgnoreVisibilityCheck). Volibear's Majestic Roar chain (VolibearRChain) bounces with it.
    /// </summary>
    [Check("scripting.p132-for-each-visible-unit-in-target-area-random")]
    public static void VisibleRandomPicksOnlyWhatTheTeamSees()
    {
        using var match = new Match("Volibear");
        match.FarFoe.Position = new Position2(7400, 7000);
        // The far foe is stealthed: the hero's team does not see it.
        Run(match, match.FarFoe, Vars(("Unit", match.FarFoe)), Block("BBSetStatus", ("TargetVar", Txt("Unit")), ("SrcValue", Flag(true)), ("Status", Ref("SetStealthed"))));
        match.Advance(0.3);
        double Count(string function)
        {
            var vars = Vars(("Attacker", match.Hero), ("Owner", match.Hero), ("Count", 0.0));
            Run(match, match.Hero, vars, Block(function,
                [Block("BBMath", ("Src1Var", Txt("Count")), ("Src1Value", Num(0)), ("Src2Value", Num(1)), ("DestVar", Txt("Count")), ("MathOp", Ref("MO_ADD")))],
                ("AttackerVar", Txt("Attacker")), ("CenterVar", Txt("Owner")), ("Range", Num(1000)), ("Flags", Txt("AffectEnemies AffectHeroes ")),
                ("IteratorVar", Txt("Unit")), ("MaximumUnitsToPick", Num(5)), ("InclusiveBuffFilter", Flag(true))));
            return (double)vars["Count"]!;
        }
        TestAssert.Equal(2.0, Count("BBForEachUnitInTargetAreaRandom"), "the plain pick takes both foes");
        TestAssert.Equal(1.0, Count("BBForEachVisibleUnitInTargetAreaRandom"), "the visible pick takes the foe the team sees");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSpellBuffAdd MaxStackVar
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// BBSpellBuffAdd reads MaxStack with the integer getter: the MaxStackVar variable wins over the literal (FizzTempestTrap MaxStacks 2, JaxPassive and
    /// JaxRelentlessAssault CharVars UltStacks, each with MaxStack 0).
    /// </summary>
    [Check("scripting.p132-spell-buff-add-max-stack-var")]
    public static void MaxStackVarCapsTheStacks()
    {
        using var match = new Match("Jax");
        var add = Block("BBSpellBuffAdd", ("TargetVar", Txt("Owner")), ("AttackerVar", Txt("Owner")), ("BuffName", Txt("JaxRelentlessAssaultAS")),
            ("BuffAddType", Ref("BUFF_STACKS_AND_RENEWS")), ("BuffType", Ref("BUFF_CombatEnchancer")), ("MaxStack", Num(0)),
            ("MaxStackVar", Txt("UltStacks")), ("MaxStackVarTable", Txt("CharVars")), ("NumberOfStacks", Num(1)), ("Duration", Num(2.5)));
        var vars = Vars(("Owner", match.Hero), ("CharVars", Vars(("UltStacks", 3.0))));
        for (var press = 0; press < 5; press++) Run(match, match.Hero, vars, add);
        TestAssert.Equal(3, Buff(match.Hero, "JaxRelentlessAssaultAS")!.Stacks, "five adds stop at the variable's three stacks");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BuffOnDodge
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// BuffOnDodge (new in 132) runs on the dodger's script buffs when a basic attack is dodged: Jax's Counter Strike counts each dodged attack (10 for a
    /// champion's at rank 1, CharVars NumCounter) for the stun its end casts.
    /// </summary>
    [Check("scripting.p132-buff-on-dodge")]
    public static void CounterStrikeCountsDodgedAttacks()
    {
        using var match = new Match("Jax");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(2, match.Hero), "Counter Strike is cast: " + match.CanCast(2, match.Hero).Reason);
        match.Advance(0.2);
        TestAssert.True(Buff(match.Hero, "JaxCounterStrike") is not null, "Jax holds Counter Strike");
        TestAssert.Equal(0.0, match.Module.CharVars(match.Hero)["NumCounter"], "the counter starts at 0");
        match.Module.Router!.RunAttackAvoided(match.Foe, match.Hero, BasicAttackOutcome.Dodge);
        TestAssert.Equal(10.0, match.Module.CharVars(match.Hero)["NumCounter"], "a dodged champion's attack adds ChampionBonus 10");
        match.Module.Router.RunAttackAvoided(match.Foe, match.Hero, BasicAttackOutcome.Miss);
        TestAssert.Equal(10.0, match.Module.CharVars(match.Hero)["NumCounter"], "a miss is no dodge");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSpawnPet of Viktor's Chaos Storm, BBSpawnMinion of the guide marker
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// ViktorChaosStorm's SelfExecute spawns its storm with BBSpawnPet (Skin TempMovableChar, AiScript StormIdle.lua, 7 s): a 0x3C pet whose chunk
    /// (StormIdle.luaobj, SetState(AI_HALTED)) the spawned-unit host runs. The second press (ViktorChaosStormGuide) with no champion at the point
    /// spawns its GuideMarker (TestCube, no AiScript) and the storm walks to it.
    /// </summary>
    [Check("scripting.p132-spawn-pet-chaos-storm")]
    public static void ChaosStormIsAPetOfItsOwnAiScript()
    {
        using var match = new Match("Viktor");
        match.Hero.Level = 6;
        match.Learn(1);
        match.Refill();
        var at = new Position2(7600, 7400);
        TestAssert.True(match.Cast(3, at: at), "Chaos Storm is cast: " + match.CanCast(3).Reason);
        match.Advance(0.6);
        var storm = match.Runtime.World.Entities.Values.SingleOrDefault(unit => unit.Name == "TempMovableChar" && unit.Alive);
        TestAssert.True(storm is not null, "the storm is spawned");
        TestAssert.Equal(CloneKind.SpawnedPet, storm!.PetState!.Kind, "a BBSpawnPet pet of its own AI script");
        TestAssert.Equal(SpawnedUnitAi.StormIdle, storm.AiScript, "StormIdle.lua");
        TestAssert.Equal("StormIdle", SpawnedUnitAi.HostScript(storm.AiScript), "the spawned-unit host runs StormIdle.luaobj");
        TestAssert.Equal(match.Hero.NetId, storm.OwnerNetId, "Viktor owns it");
        TestAssert.False(match.Runtime.World.Entities.Values.Any(unit => unit.PetState is { Kind: CloneKind.Tibbers }), "no Tibbers");
        match.Refill();
        TestAssert.True(match.Cast(3, at: new Position2(8200, 7400)), "the guide is cast: " + match.CanCast(3).Reason);
        match.Advance(0.6);
        var marker = match.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Name == "TestCube" && unit.Alive && unit.AiScript is null);
        TestAssert.True(marker is not null, "the GuideMarker cube stands at the point (no AiScript)");
        var before = storm.Position;
        match.Advance(1.0);
        TestAssert.True(storm.Position.X > before.X + 10.0, $"the storm walks to its guide ({before.X:0} -> {storm.Position.X:0})");
        match.Advance(7.0);
        TestAssert.False(storm.Alive, "the storm ends after its 7 s");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(failure => $"{failure.Script}.{failure.Hook}: {failure.Message}")));
    }

    // ------------------------------------------------------------------------------------------------------------
    // The seven new champions
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Ahri, Fizz, Graves, Sejuani, Shyvana, Viktor and Volibear are registered and served (PlayableNames, the dispatcher) and run from their shipped
    /// closures: every native the closures call is bound, and each slot at rank 1 casts at the foe (or its point) in a production match with no
    /// script failure (no nil call, no unbound native) over the cast and the seconds after it.
    /// </summary>
    [Check("scripting.p132-new-champions-cast-every-spell", TimeoutSeconds = 300)]
    public static void TheSevenNewChampionsCastEverySpell()
    {
        var failures = new List<string>();
        foreach (var champion in new[] { "Ahri", "Fizz", "Graves", "Sejuani", "Shyvana", "Viktor", "Volibear" })
        {
            TestAssert.True(ChampionCatalog.PlayableNames.Contains(champion), champion + " is served");
            var closure = new BbScriptResolver(GameData.Default.Paths).Resolve(champion);
            TestAssert.Equal(0, closure.Diagnostics.Count, champion + " closure loads cleanly");
            var unbound = closure.Scripts.SelectMany(script => script.Script.Walk()).Select(item => item.Block.Function).Distinct()
                .Where(function => !BbBaseLibrary.TryGet(function, out _) && !BbNativeHandlers.IsBound(function)).ToArray();
            TestAssert.Equal(Array.Empty<string>(), unbound, champion + ": every native of the closure is bound");
            for (var slot = 0; slot < 4; slot++)
            {
                using var match = new Match(champion);
                match.Hero.Level = 6;
                match.Learn(1);
                match.Refill();
                match.Foe.Position = new Position2(7250, 7000);
                var spell = match.Module.SpellName(match.Hero, slot);
                // Winter's Claw's CanCast wants a Frost-marked enemy within 900 (SejuaniWintersClaw: BuffNameFilter SejuaniFrost): her passive marks
                // the foe first, as her basic attacks do (CharScriptSejuani's CharOnHitUnit casts the Frost extra spell).
                if (champion == "Sejuani" && slot == 2)
                    match.Module.Buffs!.Add(new BbBuffRequest(match.Foe, match.Hero, "SejuaniFrost", (int)BbBuffAddType.BUFF_RENEW_EXISTING,
                        (int)BbBuffType.BUFF_Slow, 1, 3.0, 0.0, false, Vars(("MovementSpeedMod", -0.1))));
                // Volibear's Frenzy is castable at three stacks of VolibearWStats (VolibearW's CanCast), which his attacks build (BuffOnHitUnit).
                if (champion == "Volibear" && slot == 1)
                    for (var hit = 0; hit < 3; hit++)
                        match.Module.Buffs!.Add(new BbBuffRequest(match.Hero, match.Hero, "VolibearWStats", (int)BbBuffAddType.BUFF_STACKS_AND_RENEWS,
                            (int)BbBuffType.BUFF_CombatEnchancer, 3, 4.5, 0.0, false, Vars(("VolibearWAS", 0.08))));
                var cast = match.Cast(slot, match.Foe) || match.Cast(slot, at: match.Foe.Position) || match.Cast(slot);
                if (!cast)
                    failures.Add($"{champion} slot {slot} ({spell}) refused: {match.CanCast(slot, match.Foe).Reason}");
                match.Advance(3.0);
                foreach (var failure in match.Module.ScriptFailures.Concat(match.Runtime.Spells.UnitScripts!.ScriptFailures))
                    failures.Add($"{champion} slot {slot} ({spell}): {failure.Script}.{failure.Hook}: {failure.Message}");
            }
        }
        // Batch 772: Ahri's Fox-Fire children (AhriFoxFireMissile, CastType 4) run as circle missiles (BbCircleMissile), so no exception is left.
        TestAssert.Equal(Array.Empty<string>(), failures.ToArray(), string.Join(Environment.NewLine, failures));
    }
}
