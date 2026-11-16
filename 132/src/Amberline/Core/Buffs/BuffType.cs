namespace Amberline.Core.Buffs;

/// <summary>
/// Patch-132 buff type byte values: the numbers the stock 1.0.0.132 client registers for the scripts' <c>BUFF_*</c> constants (its
/// constant registration, lol132.exe the client: <c>BUFF_SpellShield</c> 4 is new and every type from Stun up is one higher than in 126,
/// up to <c>BUFF_AmmoStack</c> 0x1A; <c>BUFF_Shred</c> 0x1B is new). The packet byte of the buff add is this value unchanged, and the
/// client's buff bar reads it. The 126 line (branch 126) keeps the 1.0.0.126 numbering (Stun 4... AmmoStack 0x19), which the A4 audit
/// (18 October 2026) restored there after the table had carried this later numbering from the Python server's packets_spell.py.
/// </summary>
public enum BuffType : byte
{
    Internal = 0,
    Aura = 1,
    CombatEnhancer = 2,
    CombatDehancer = 3,
    /// <summary>The scripts' <c>BUFF_SpellShield</c> (new in 1.0.0.132; no shipped script adds one).</summary>
    SpellShield = 4,
    Stun = 5,
    Invisibility = 6,
    Silence = 7,
    Taunt = 8,
    Polymorph = 9,
    Slow = 10,
    Snare = 11,
    /// <summary>The scripts' <c>BUFF_Damage</c>.</summary>
    DamageOverTime = 12,
    Heal = 13,
    Haste = 14,
    SpellImmunity = 15,
    PhysicalImmunity = 16,
    Invulnerability = 17,
    Sleep = 18,
    NearSight = 19,
    Frenzy = 20,
    Fear = 21,
    Net = 22,
    Poison = 23,
    Suppression = 24,
    Blind = 25,
    AmmoStack = 26,
    /// <summary>The scripts' <c>BUFF_Shred</c> (new in 1.0.0.132: the armor and magic-resist reductions, 42 shipped adds).</summary>
    Shred = 27,
}
