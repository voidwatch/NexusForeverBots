# Combat math: from spell effect to HP change

Every step the server takes when a spell effect or an auto-attack changes a unit's health or shield, with its status:
**implemented** (sourced: tables, the retail client binary, or a documented formula), **guessed** (implemented, no source;
listed in docs/ASSUMPTIONS.md) or **missing** (not implemented, what is needed to do it). Code paths are under
Source/NexusForever.WorldServer/Game. Checks: `scripts/combat-check.sh` (server numbers vs. tables/tooltips),
`scripts/research/creature_spell_dmg.py` (creature spells vs. a level-matched player, research/decode/combat/creature-spell-dmg.tsv).

| # | step | status | how / source | code |
|---|------|--------|--------------|------|
| 1 | Effect reaches a target | implemented (partly guessed) | Telegraphs from the tables for creatures; player Damage effects always reach the tab target (telegraph shapes unreliable, guess). | Spell/Spell.cs ExecuteEffects |
| 2 | Who can hurt whom | implemented | No PvP, no creature vs creature, no self damage; friendly quest targets damageable by their quest holder (likely). | SpellEffectHandler.cs HandleEffectDamage |
| 3 | Base amount | implemented (the client's formula, certain for the client) | client code: stat part = round(sum of value x stat) over types 1-5 (attributes), 11 (Grit), 12/13 (assault/support power); flat part = sum of value x quantity over 7 target max health, 8 caster max health, 10 level, 14-17 shields, 18 item budget, 19/23 health, 21/22/25/26 missing health/shields. For Damage, Heal, DistanceDependentDamage, DistributedDamage, HealShields, DamageShields: flat = (DataBits01 + flat) x DataBits00 (float; Transference DataBits03/02). Creature casters: soft cap (step 3e). Names of every type from the client's text client table. | Combat/SpellFormula.cs |
| 3a | Assault/support power | implemented (certain) | power = rating x GameFormula 1266 Datafloat0/01 (0.25): client Unit:GetAssaultPower and GetSupportPower read property 35/36 and multiply by that row; greenyneko "17765 rating / 4 = 4441.25 AP". Before 2026-10-11 the rating itself was used (every AP term 4x too high). | SpellFormula.AssaultPowerPerRating |
| 3b | Weapon damage (types 6 "Weapon", 9 "Weapon DPS") | implemented (certain for the client) | The client's amount formula has no case for them: they add 0. Build 16042 items carry no weapon damage either (Item2Category weaponPowerBase 0 / modifier 1). | SpellFormula.Amount |
| 3c | Types 18, 20, 24 | 20/24 implemented, 18 missing | 20/24 (second "Target/Caster Shield Capacity" names) add nothing in the client formula. 18 "Item Budget" multiplies a value the client's caller passes (an item budget, source of that value not found); seen on spell 82315 only. Ledger `spell-param-unknown`. | SpellFormula |
| 3d | Rows without parameters | partial | 16874 of 33502 damage/heal rows have no parameter. 3521 carry a designer amount in DataBits01 (Transference DataBits03): DataBits01 x DataBits00 as in the client formula (Ice Blizzard 1500, Firebloom 1200; certain for the client). The other 13353 have no amount (client 0): the server keeps DataBits00 (float) x assault power (guess). 40 creature names (37 spawned, 364 spawn rows) cast such a spell, 16 abilities are fully on the guess: scripts/research/noparam.py | Combat/SpellFormula.cs, Spell/SpellEffectHandler.cs |
| 3f | Normal/veteran effect copies | implemented (likely) | Prerequisite type 143 = instance veteran mode (0/1). Dungeon spells hold both copies of a row (14409 Equal 0, 14893 Equal 1; veteran median 1.54x). Effect prerequisites were never read, so both landed. Now only the copy of the map's mode applies. | Spell/Spell.cs DifficultyAllows |
| 3g | Innate resource costs | implemented for Resource1 and Spell Power, Suit Power/Focus counted only | Spell4.InnateCostType = Vital id; Class.vitalEnumResourceNN maps vitals to resource slots. Charged by default (NF_INNATE_COSTS): Esper Psi Points, Engineer Volatility, Medic Actuators, Spellslinger Spell Power (Surge casts); Warrior Kinetic Energy thresholds via casterInnateRequirement eval 2. Pools and regeneration in "Resource economy". Suit Power and Focus have no sourced base regeneration: counted, not charged | Spell/InnateCost.cs, Spell/SpellConditions.cs, Spell/TooltipGenerators.cs |
| 3e | Creature power soft cap | implemented (certain for the client) | Casters that are not characters (Unit:IsACharacter = unit type 20) cap the stat part at (GameFormula 712: 70 x level + 100) x the mean stat coefficient; above it the stat part is cap x (1 + 1 x (1 - e^-(excess/cap))), then / 1. Players are never capped (the Lua GetAssaultAndSupportPowerSoftcap shows the same 70 x level + 100). | SpellFormula.Amount |
| 4 | Player stats | implemented, base guessed | CreatureLevel row of the level + equipped items (Combat/ItemStats.cs: health, shield, armor, assault/support, secondary ratings, sourced from 610 Jabbithole tooltips). Using the creature level row as the naked player base is a guess (no class/level stat table). | Player.ApplyLevelStats |
| 4a | AMPs, runes, set bonuses | partial | Set bonuses and imbuements apply. AMP and talent effects are spells: their UnitPropertyModifier rows apply (step 6). Class innate stances: Class.Spell4IdInnateAbilityPassive[InnateIndex] (Warrior Onslaught/Juggernaut, Engineer Eradicate/Provoke, Stalker Nano Skin) is applied as a lasting passive like an AMP (likely). Runes: ItemStat type 4 "random attribute" slots add nothing (no budget). | Player.ApplySetBonuses, ItemStats |
| 5 | Creature stats | implemented (certain) | CreatureLevel row x Creature2ArcheType x Creature2Difficulty x Creature2Tier UnitPropertyMultiplier (Jabbithole ratios equal table ratios). | Combat/UnitStats.cs |
| 5a | Creature auto-attack | implemented, interval guessed | First Damage row of the creature's researched "[AUTO-ATTACK]"/"Basic Attack" spell through the spell formula and its damage type (7261 of 8890 creature pages have one). Without one: 0.8 x (Strength + assault power), the most common auto-attack row (187 of 647, guess). Swing every 2.5 s (guess: auto-attack rows have no cast time or cooldown). Before 2026-10-11: the full assault rating per swing. | NonPlayer.Update |
| 5b | Creature abilities | implemented, timing guessed | Researched (Jabbithole) abilities cast off table cooldowns (10 s when 0), at least 3 s apart; encounter files for bosses. | NonPlayer.TryCastAbility, Encounters |
| 5c | CC conditions, spell modifications | implemented (certain / likely) | Spell4CCConditions: a caster or target under a masked CC state cannot cast (client evaluator, bit n = CCStates row n). ModifySpell + Spell4Modification follow the client's evaluator (certain): per priority the changes are summed, type 0 = (data00 - 1) x base + data01 and type 1 = (data00 - 1) x current, each with a stop value (data02 / data01) and direction (data03 / data02); type 2 clamps the summed change, 3 sets, 4 sets or clears bits. Applied: cast time, cooldown, innate costs 0/1, caster innate requirement amounts, caster/target Spell4Conditions. Range, channel timing and threat are counted (docs/ASSUMPTIONS.md "Client spell modification evaluator"). | Spell/SpellCCConditions.cs, Spell/SpellModifications.cs |
| 6 | Property modifiers | implemented (order guessed) | UnitPropertyModifier types 1-4 all value x A + B, applied in ascending type order (form certain, order guess). Applied to every property read through GetModifiedProperty (power, armor, ratings, multipliers). | UnitEntity.GetModifiedProperty |
| 7 | Damage dealt/taken multipliers | implemented | DamageDealtMultiplier{Physical,Tech,Magic} of the caster, DamageTakenMultiplier of the target (properties 141-143 and 159-161), PersonalDmgHealMod of either side. | HandleEffectDamage, UnitEntity.TakeDamage |
| 8 | Deflect / strikethrough | implemented (certain curves) | Client rating curve with GameFormula 1235/1230; deflect chance minus strikethrough; excess strikethrough becomes armor pierce (tooltip). | Combat/DamageMath.RollHit |
| 9 | Mitigation (armor + resist) | implemented (certain) | Client: DamageMitigationPctOffset + type offset + curve 1234 of (armor + resist rating of the type), 0..0.75; times (1 - armor pierce). | DamageMath.Mitigation |
| 10 | Crit chance / severity | implemented, player base guessed | Curves 1231/1232, crit deflect 1236, crit mitigation 1242. Player base chance 5% (guess: UnitProperty2 102 default is 0), severity default 1.5 (UnitProperty2 127, certain). Creatures crit only from their table properties (0). | DamageMath |
| 11 | Glance, multi-hit, vigor, tenacity, toughness, lifesteal, reflect | implemented (curves certain, composition likely) | Client curves; what each does from the character sheet tooltips; order from greenyneko (base, crit, multi-hit, vigor, strikethrough, resist, pierce, tenacity). | DamageMath.RollHit, AfterHit |
| 12 | Random spread | guessed | x 0.85-1.15 per hit. No source for WildStar's damage variance. | CombatConfig.DamageRoll |
| 13 | Periodic damage / heals | implemented | Rows with TickTime apply their amount every tick for DurationTime ("Deals X damage every T seconds for D seconds", certain per tick). 2143 Damage + 715 Heal rows. First tick at the cast (guess). Before 2026-10-11: applied once. | Spell.SchedulePeriodic |
| 14 | Level difference | missing | No table or source found for hit/damage scaling by level difference (creatures already scale through their level row). | - |
| 15 | Shields | implemented | Players: ShieldMitigationMax share of each hit to the shield (0.5 when unset, wiki "Shield"), regen ShieldRegenPct per ShieldTickTime after ShieldRebootTime (UnitProperty2 defaults or the shield item). Creature shields: table value exists, not absorbed (missing: no creature shield hit rule found). | Player.AbsorbWithShield |
| 16 | Absorption, damage immunity, invulnerable | implemented | Absorption effect, SpellEffectImmunity kinds 8/32, scripted invulnerability. | UnitEntity |
| 17 | Heals | implemented | Same formula with support power, RollHeal (crit, intensity, hope, multi-hit), heal crit procs. | HandleEffectHeal, DamageMath.RollHeal |
| 18 | Health regeneration | missing (no source) | HealthRegenMultiplier (property 8, default 1) exists but no base rate in any table, client function or source: the client has Lua GetShieldRegenPct and three focus regen functions, nothing for health. The resource regeneration multipliers (16-22, 39, 51, 53, 55) and the Kinetic Energy decay multiplier (117/118) are only read by the server; no client code uses them for a vital. Players regain health only through heals, resurrection and level-up. | - |
| 19 | Threat | missing | The first attacker stays the target until it dies or leaves; Spell4Effects.threatMultiplier and property 132 ThreatMultiplier exist but no threat formula is sourced. ThreatModification mode 2 (drop) is applied. | NonPlayer |
| 20 | Bot damage boost | test only | NF_BOTS_DAMAGE (default 1) multiplies bot damage; immortal bots (NF_BOTS_IMMORTAL) take no health loss. | BotManager |

## What changed numbers on 2026-10-11
- Assault/support power = rating x 0.25 (step 3a). A naked level 50 player's Quick Draw T1: 0.1029 x 3522.8 + 5.5 x 50 = 637 before,
  0.1029 x 880.7 + 275 = 366 now; geared (level row + 4176 weapon rating) 1066 -> 474.
- Creature spells with the usual (attribute, a) + (12, a) rows lose 60% (attribute x a stays, the assault term drops to a quarter).
- Creature auto-attacks: full assault rating per 2.5 s swing (3523 at level 50 before mitigation) -> the creature's own auto-attack row,
  e.g. 0.8 x (Strength + AP) = 1409 for a level 50 standard creature.
- DoTs and HoTs tick for their whole duration.

## Combat-stats counters (ledger group `combat-stats`)
Statistics, not failures: `damage effect applied by bot|human|creature` counts Damage handler calls that reached HP (before 2026-10-11
creatures were counted as "human": the label only told bots apart), `heal effect applied by ...` the same for heals, `creature killed by
bot|human|other`, `creature respawned`, `player died/revived (bot|human)`, `hit deflected`, `hit glanced`, `multi-hit`, `damage fully
resisted`, `periodic Damage|Heal tick`, `auto-attack without a researched spell (default row)` / `auto-attack spell row without
decoded parameters`.

## A0: the 25 most-cast creature spells against a level-matched player (soak 20261010-1119)
`scripts/research/creature_spell_dmg.py <failures.txt> 25` (offline, same formula as the server). Caster = the creature named in the spell
description, else the first researched (Jabbithole) page with the spell; stats = level row x archetype/difficulty/tier. Player = naked
level row of the caster's level (health + shield, armor mitigation by curve 1234). Damage = sum of every Damage row reached through
proxies (each row is usually a separate telegraph, so one cast rarely lands all of them), before spread, crit and deflect.
How it is computed: every row is (attribute, a) + (12, a) [creature form, 7374 of 7391 rows]: a x attribute + a x assault power.
Before 2026-10-11 the second term used the assault rating (4x the power), so 'before' is 2.5x 'now' for these rows.

| casts | spell | name | caster (level) | rows | before | now | % of player hp+shield now | note |
|---|---|---|---|---|---|---|---|---|
| 2223 | 48766 | [TELEGRAPH](ct) Humanoid 2H Sword (Base - Tank) - Triple Thr | Attacking Sunderstone Fighter (24) | 3 | 1734 | 694 | 1.7% |  |
| 2213 | 49411 | [TELEGRAPH](ct) Humanoid 2H Sword (Base - Tank) - Demolition | Attacking Sunderstone Fighter (24) | 1 | 4288 | 1715 | 4.3% |  |
| 2153 | 44714 | [TELEGRAPH](ct) Buzzbing (Stinger) - Stinger Blast - Complex | Buzzbing (5) | 1 | 855 | 342 | 15.5% | 2 Creature2 rows share the name |
| 1223 | 41339 | [SK] e328 - Lava Tube - Collapse - Base - Tier 1 | Cave In (20) | 1 | 9331 | 3732 | 12.5% |  |
| 1182 | 35556 | [TELEGRAPH](ct) Girrok (Rockpaw) - Rolling Rampage - Complex | Girrok (5) | 2 | 1630 | 652 | 29.5% |  |
| 1170 | 59759 | [PROXY](ct) Buzzbing (Stinger) - Honey Bomb - Tier 1 | Buzzbing (5) | 1 | 219 | 87 | 4.0% | 2 Creature2 rows share the name |
| 1097 | 44650 | [TELEGRAPH](ct) Buzzbing (Stinger) - Stinger Torrent - Prog  | Buzzbing (5) | 3 | 880 | 352 | 15.9% | 2 Creature2 rows share the name |
| 1021 | 63697 | [SK] e328 - Lava Tube - Heat Vent - Base - Tier 2 | Heat Vent (20) | 1 | 18661 | 7464 | 25.0% | 3 Creature2 rows share the name |
| 1017 | 52940 | [SK] e329 - Molten Chasm - Melted Armor - Tier 1 | Magma Wave (20) | 1 | 1353 | 2705 | 9.1% | 2 Creature2 rows share the name; periodic 10000/2000 ms: once before 2026-10-11, 5  |
| 986 | 52347 | [SK] e329 - Molten Chasm - Molten Wave - Tier 1 | Molten Wave (1) | 2 | 209 | 84 | 20.9% |  |
| 867 | 44638 | [TELEGRAPH](ct) Buzzbing (Drone) - Ground Slam - PBAE - Tier | Buzzbing (5) | 1 | 1068 | 427 | 19.3% | 2 Creature2 rows share the name |
| 867 | 57287 | [TELEGRAPH](ct) Spikehorde (Base) - Careful Coating - Prog - | Spikehorde (34) | 2 | 2550 | 638 | 1.1% |  |
| 864 | 57311 | [PROXY](ct) Spikehorde (Base) - Repugnant Flow (dmg) - Tier  | Spikehorde (34) | 1 | 4681 | 1170 | 2.0% |  |
| 856 | 51991 | [KV] e449 - Forgemaster Trogun - Inferno - Base - Tier 1 | Forgemaster Trogun (25) | 4 | 107537 | 43015 | 102.1% | 4 Creature2 rows share the name |
| 803 | 35963 | [TELEGRAPH](ct) Buzzbing (Shared) - Flurry of the Buzzbing - | Buzzbing (5) | 4 | 774 | 310 | 14.0% | 2 Creature2 rows share the name |
| 764 | 43759 | [PROXY](ct)(sh) Bleed - DoT - Default - Dex/AP - 8s - Tier 1 | Abyss (47) | 1 | 782 | 939 | 1.0% | periodic 6000/2000 ms: once before 2026-10-11, 3 ticks now |
| 761 | 53110 | [KV] e449 - Forgemaster Trogun - Fireball - AE Proxy - Tier  | Forgemaster Trogun (25) | 2 | 60094 | 24038 | 57.1% | 4 Creature2 rows share the name |
| 749 | 52014 | [KV] e449 - Forgemaster Trogun - Inferno - Channel - Tier 1 | Forgemaster Trogun (25) | 2 | 50605 | 20242 | 48.1% | 4 Creature2 rows share the name |
| 674 | 34262 | [TELEGRAPH](ct) Girrok (Shared) - Ground Pound - Prog - Tier | Girrok (5) | 4 | 511 | 204 | 9.3% |  |
| 634 | 49928 | [TELEGRAPH](ct) Humanoid 2H Sword (Fire - DPS) - Brush Fire  | Bonerazer Bloodspiller (8) | 1 | 2971 | 1189 | 24.4% |  |
| 615 | 58752 | [PROXY](ct) Girrok (Rockpaw) - Hurl Boulder - Tier 1 | Girrok (5) | 1 | 869 | 348 | 15.7% |  |
| 612 | 49792 | [TELEGRAPH](ct) Humanoid 2H Sword (Shared) Searing Strikes - | Beastmaster Xix (8) | 2 | 665 | 266 | 5.5% |  |
| 599 | 34552 | [TELEGRAPH](ct) Humanoid Unarmed (Shared) - Flourishing Comb | Aberrant Blaze (50) | 2 | 6222 | 2489 | 2.2% |  |
| 576 | 52448 | [TELEGRAPH](ct) Humanoid Unarmed (Shared) - Haymaker - PBAE  | Aberrant Blaze (50) | 1 | 17386 | 6954 | 6.1% |  |
| 525 | 34131 | [TELEGRAPH](ct) Humanoid 2H Sword (Base - DPS) Hack and Slas | Acacia (13) | 2 | 734 | 294 | 2.3% |  |

Flags: no spell deals zero damage. Implausibly low: none (lowest 43759 Bleed DoT, 0.209 coefficients, 1% per cast at level 47, a
filler DoT). High: Forgemaster Trogun (Ruins of Kel Voreth boss, 51991 Inferno: 4 rows, 102% of a naked level 25 player when all land;
each row is a separate avoidable telegraph). Before 2026-10-11 ordinary mobs took 40-75% of a player per ability (Girrok Rolling Rampage 74%)
and every auto-attack hit for the full assault rating. Nothing in the tables gives a per-hit damage figure to compare against, so the
absolute level stays unverified; the formula and inputs are sourced (power = rating x 0.25, creature stats from the tables).

## Stage D: time to kill per boss and elite family
`scripts/research/ttk.py [bot-stats.tsv] [spawned.tsv]` (offline, the server's formulas via scripts/research/combatlib.py) writes
research/decode/combat/ttk-families.tsv, copied to docs/ttk/ (232 families = Creature2Difficulty rank x archetype x tier of every spawned Superior/Prime/
group/raid creature), ttk-bosses.tsv (60 encounter bosses), ttk-by-rank.txt and ttk-summary.txt. `gt botstats` writes the live bots'
stats (/cache/bot-stats.tsv) for comparison. Player = level row + full item set of item level = level (ItemStats formulas), DPS =
median of the six classes' tier-1 abilities: builders only / rotation with one spender per 4 s (guess) / spenders every global cooldown
(what the server allowed before 2026-10-15: Spell4 innate costs were not charged; since 2026-10-16 Resource1 and Spell Power costs are charged by default, NF_INNATE_COSTS). Creature damage = researched auto-attack + abilities as NonPlayer casts them.

| player level | effective health | assault rating | mitigation | DPS builder / rotation / server |
|---|---|---|---|---|
| 10 | 10655 | 1480 | 0.720 | 244 / 333 / 347 |
| 25 | 40254 | 4045 | 0.672 | 1135 / 1458 / 2105 |
| 50 | 104912 | 8914 | 0.506 | 2395 / 3443 / 4420 |

| rank | families | solo kill s (median [min-max]) | group of rank size | one player survives s (median [min]) |
|---|---|---|---|---|
| Superior | 47 | 21 [10-888] | 21 | 185 [78] |
| Prime | 29 | 70 [39-137] | 70 | 95 [44] |
| Group Challenger / Superior / Prime | 25 / 27 / 36 | 132 / 221 / 581 | 26 / 44 / 116 (5 players) | 49 / 56 / 67 [20] |
| Raid 20 Challenger / Superior / Prime | 15 / 12 / 21 | 899 / 4850 / 8871 | 45 / 242 / 444 (20 players) | 20 / 21 / 37 [6] |
| Raid 40 Superior / Prime | 9 / 11 | 6556 / 26759 | 164 / 669 (40 players) | 90 / 90 [22] |

Dungeon bosses (5 players): Stormtalon/Blade-Wind/Aethros 89 s, Stew-Shaman Tugga and Mordechai Redmoon 91 s, Grond 102 s, Spiritmother
Selene and Rayna Darkspeaker 153 s; a lone player survives 28-108 s. Datascape bosses with 20 players: 565-630 s (Avatus 630 s).
Flags (stage D criteria): no boss dies to a single player in under 30 s with a resource-limited rotation. With spenders on every
global cooldown (the server today) two solo Prime families do: Snowshadow Investigator family (rank 6, archetype 11, 9 creatures, L10-49)
28 s and Sabotaged Enforcerbot family (archetype 13, 7 creatures, L23-50) 27 s. Kills a level-appropriate player in under 5 s when every
telegraph of its two hardest abilities lands: 13 raid bosses (Avatus 30505 382677 vs 104912 effective health, Golgox 52970, Megalith
30499, Hydroflux 30500, Pyrobane 30503, Aileron 30502, Null/Binary System Daemon 30495/30496, Dreadphage Ohmna 49395, Experiment X-89 49198,
Prime Evolutionary Operant 50472, Metal Maw 24740 / Metal Maw Prime 26190) and 2 raid trash families (Phageborn Brute 54792, Phagetouched
Drillbot 69130) plus Blisterbone Dreglord 19818 (Group Superior L47). These are raid mechanics a raid avoids, not outliers by themselves.
Removed outliers: three rank-0 flavor copies of Avatus / Metal Maw Prime died in 13-18 s solo because they had boss encounters (fixed).

## Stage E: 30 minutes with mortal bots
NF_BOTS_IMMORTAL=0, NF_BOTS_DAMAGE=1 (real bot damage), 30 bots, 2026-10-11 10:22-10:52, image 11bbaa6. 0 map-update / spell-update
exceptions, every map ticking (~28k ticks per 10 min), CPU 110-127%, memory 2.70-2.87 GiB (flat). Ledger (combat-stats): 313 bot deaths,
313 resurrections, 86 creatures killed by bots, 8778 bot damage effects vs 4153 creature damage effects, 36 bot heals.
Deaths per killer (player-death ledger, top): Bosun Octog 24486 (Group Prime L35, Skullcano 1263) 57; Bloodspawn Flea 56575 (veteran add
L50 in a normal Kel Voreth 1336 run) 51; Toric Antevian 47082 (Group Prime L21, 1336) 51; no killer 39 (the death/resurrection system
probes call Kill(null) on purpose); Soulrot Canister 38434 (rank 0 L38-39 quest object with "Q7907 - Damage Aura", level 11 bots) 31;
Blood Pit Gladiator 51068 10; Slavemaster Drokk 32536 9; Grond 32534 8; Dreadwatcher 55019 (Raid Prime) 7; Dreamspore 30921 7; KE-27
Sentinel 56464 7 (level 1 bot in a level 50 zone); Metal Maw Prime 26190 5; 21 more killers with 1-3 deaths, mostly level 50 creatures
killing level 1-11 bots.
Dungeon runs: both teams of 5 (Kel Voreth level 25, Skullcano level 35, normal mode) wiped repeatedly and timed out after 30 minutes
(bosses killed 1 and 0; Bosun Octog reset 26 times and enraged once at 600 s). Bot teams have no healer role (36 heals in 30 min) and do
not dodge telegraphs, so this is the expected result for an unhealed group, consistent with stage D (a lone player lasts 33-41 s against
these bosses; 5 players need ~91 s).
Fixed from this run: the Bloodsucker Flea add's generated encounter cast "[KV] e444 - Grond - Bloodburst - Veteran" (63099) in a normal
run and summoned level 50 veteran fleas: DesignerTag.FitsMode now reads " - Veteran - " / " - Normal - " segments (22 spells).
Harness findings (not server bugs): level rotation leaves a bot in its old zone after dropping it to level 1-11, so mortal bots die to
level 50 creatures until they travel; bots never heal others.


## Resource economy
Pools (set at login, Game/Spell/InnateCost.cs; sources in docs/ASSUMPTIONS.md "Combat resource economy"):

| class | resource (slot, vital) | max / start | regeneration | cost charged | builders that land in the server |
|---|---|---|---|---|---|
| Warrior | Kinetic Energy (Resource1, vital 4) | 1000 / 0 (certain) | decay unknown, not modelled | no costs; casterInnateRequirement eval 2 thresholds (250/500/750) checked | Add Cell rows 53865, 54606, 46712, 46867 |
| Engineer | Volatility (Resource1, vital 26) | 100 / 0 (certain) | out-of-combat decay unknown, not modelled | yes | tooltip generator 41341 -> 35967, 76143; stance change empties it (46297) |
| Esper | Psi Points (Resource1, vital 6) | 5 / 0 (likely) | none | yes | 30900 "+1 Psi Point" via telegraph proxy, 32822 |
| Medic | Actuators (Resource1, vital 18) | 4 / 4 (certain) | refill to 4 out of combat (certain), 1/s (guess) | yes | out-of-combat refill; tooltip generators (Triage, Discharge 42568) |
| Stalker | Suit Power (Resource3, vital 12) | 100 / 100 (guess) | base rate unknown | no (counted) | 60868; stance change empties it (45057, share -1.0) |
| Spellslinger | Spell Power (Resource4, vital 19) | 100 / 100 (likely) | 25 per 12 s in and out of combat (likely) | yes (Surge variants only, 25) | none needed |
| healers | Focus (vital 15) | property 5 BaseFocusPool (gear, 1.5 per point; client unit+0x5ec) | out of combat 0.011 x pool per second (property 108); in combat the rating curve of property 107 + RatingFocusRecovery, GameFormula 1237, every 500 ms (GameFormula 88). Certain: client code, wiki Stat page | no (counted) | - |

Bot evidence, two 10-minute mortal gates with 30 bots and charging on (ledger resource-sample every 30 s per bot, innate-cost, resource-flow):

| class | samples empty / partial / full | charged casts | resource gains (spell x count) | verdict |
|---|---|---|---|---|
| Warrior | 64 / 17+ / 133 | requirement only | 53865 x384, 54606 x66, 46712 x8 | moves |
| Engineer | 75 / 41 / 0 | 298 | 35967 x53 (tooltip generator), 76143 x1 | moves (spends whenever it can, rarely full) |
| Esper | 36 / 14 / 0 | 61 | 30900 x35, 32822 x29 | moves (spends at 1 point) |
| Medic | 1 / 2 / 35 | 8 | out-of-combat refill | moves |
| Stalker | 0 / 468 / 0 | 0 (94 counted) | 60868 x24, loss 45057 x24 | moves through gains and stance losses only |
| Spellslinger | 0 / 0 / 194 | 0 (bots never cast Surge variants) | - | full, not exercised |

Before 2026-10-16 the same check showed Resource1 at 0 on every Esper, Engineer and Medic bot (455 waits per gate): bots cast class auto attacks
(spellClass 0, no cooldown) every global cooldown and only saw the first 8 Ability-bag slots, and no builder generator landed.

DPS model with charging off and on (scripts/research/dps_charge.py, 60 s greedy rotation, level-appropriate gear, research/decode/combat/dps-charge.tsv):

| class | L10 off / on | L25 off / on | L50 off / on |
|---|---|---|---|
| Warrior | 319 / 319 | 1172 / 1181 | 3179 / 3179 |
| Engineer | 297 / 297 | 1151 / 930 | 2533 / 2098 |
| Esper | 938 / 613 | 2435 / 1601 | 5084 / 3343 |
| Medic | 347 / 296 | 1673 / 1673 | 3540 / 3540 |
| Stalker | 1311 / 1311 | 3406 / 3406 | 7131 / 7131 |
| Spellslinger | 330 / 330 | 4850 / 4850 | 10127 / 10127 |

Charging lowers Esper DPS by about a third and Engineer by about a fifth (spenders went from every global cooldown to what the builders
pay for). The model does not include the tooltip generators, so its Engineer "on" value is a lower bound.
