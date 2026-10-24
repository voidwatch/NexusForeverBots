#!/usr/bin/env python3
"""Expected combat numbers for scripts/combat-check.sh, computed from the game tables and the sources (independent of the C# code).
Prints lines: <key> <expected> <tolerance-relative> <source>. Keys match the 'value' lines of 'gt combat'."""
import struct, tbl
L={r['ID']:r for r in tbl.load('CreatureLevel')}
def lv(level,k): return L[max(1,min(50,level))][f'unitPropertyValue{k:02d}']
def mult(t,i,k):
    r=tbl.index(t).get(i); return r[f'unitPropertyMultiplier{k:03d}'] if r else 1.0
eff=tbl.load('Spell4Effects')
GF={r['ID']:r for r in tbl.load('GameFormula')}
import math
def curve(fid,rating,level,base=0.0):
    # client rating curve (docs/ASSUMPTIONS.md "Rating curves")
    g=GF[fid]; vals=[v for k,v in sorted(g.items()) if k!='ID']
    i0,i1=[v for k,v in g.items() if k.lower().startswith('dataint')][:2]
    f0,f1,f2,f3=[v for k,v in g.items() if k.lower().startswith('datafloat')][:4]
    s=f0/level*f1; knee=(f2-i0)/s
    v=s*rating+i0 if rating<knee else f2+s*(rating-knee)*math.cos(math.pi/2*(rating-knee)/((rating-knee)+f3))
    return min(v*0.01+base,i1*0.01)
AP,SP,STR,HEALTH,SHIELD,ARMOR=35,36,0,7,41,42
def stat(t,level,power):
    if t in (1,2,3,4): return lv(level,t-1)
    if t==10: return level
    # assault/support power = rating x 0.25 (client GetAssaultPower: property 35 x GameFormula 1266 Datafloat0)
    if t==12: return (power or lv(level,AP))*GF[1266]['datafloat0' if 'datafloat0' in GF[1266] else 'Datafloat0']
    if t==13: return (power or lv(level,SP))*GF[1266]['datafloat01' if 'datafloat01' in GF[1266] else 'Datafloat01']
    return None
def spell(sid,level,power=0,src=''):
    for e in eff:
        if e['spellId']!=sid: continue
        terms=[(e[f'parameterType0{k}'],e[f'parameterValue0{k}']) for k in range(4) if e[f'parameterType0{k}']]
        known=[(t,v) for t,v in terms if stat(t,level,power) is not None]
        tag = f"L{level}" + (f"P{power:g}" if power else "")
        if not known: continue
        # retail client amount (client code, docs/ASSUMPTIONS.md "client spell amount"): the stat part (types 1-5, 11-13)
        # is rounded half away from zero to an int (+-0.5 from .rdata, cvttss2si); the flat part (type 10 here)
        # is (DataBits01 + flat) x float(DataBits00) for Damage/Heal/DistanceDependentDamage/DistributedDamage/HealShields/DamageShields
        st=sum(v*stat(t,level,power) for t,v in known if t in (1,2,3,4,5,11,12,13))
        st=math.floor(st+0.5) if st>=0 else math.ceil(st-0.5)
        fl=sum(v*stat(t,level,power) for t,v in known if t not in (1,2,3,4,5,11,12,13))
        if e['effectType'] in (8,10,12,33,118,138): fl=(e['dataBits01']+fl)*struct.unpack('<f',struct.pack('<I',e['dataBits00']))[0]
        print(f"{tag}.spell.{sid}.effect.{e['ID']} {st+fl:.4f} 0.002 {src}")
for level in (1,10,30,50):
    print(f"player.L{level}.health {lv(level,HEALTH):.4f} 0.001 CreatureLevel row (players use it until gear exists)")
    print(f"player.L{level}.assault {lv(level,AP):.4f} 0.001 CreatureLevel row")
    a=lv(level,ARMOR); print(f"player.L{level}.resist {curve(1234,a,level):.4f} 0.002 client mitigation curve GameFormula 1234")
    spell(43498,level,0,'Quick Draw T1: round(AP*0.1029) + 5.5*level (client formula; greenyneko form without the rounding)')
print("crit.severity 1.5 0.0001 UnitProperty2 127 CriticalHitSeverityMultiplier default 1.5; greenyneko 150%")
print(f"crit.sampled-multiplier {1+0.05*(1.5-1):.4f} 0.004 greenyneko 1+chance*(severity-1) with the assumed 5% chance")
spell(41457,50,17765,'Mortar Strike T1 at 17765 rating = 4441.25 AP (greenyneko); blog tooltip 5664.16 = this x1.182 (later patch)')
spell(49679,50,0,'Vitality Burst T5 heal: SP*0.413 + 14.41*level')
# geared player: level row assault rating + an item level 50 quality 3 weapon (Item2 supportPowerPercentage -0.5 -> 75% assault:
# floor(111.375 x 50 x 0.75) = 4176, the Jabbithole tooltip of such weapons shows 4176 Assault Rating)
spell(43498,50,lv(50,AP)+4176,'Quick Draw T1 at L50 with an ilvl 50 weapon (level row AR + 4176 weapon AR)')
spell(6962,50,0,'Snap (creature attack): 0.8*Strength + 0.8*AP of the level row')
for cid,level,src in ((59617,50,'Adimar, Jabbithole live 24300 (x1.126, older patch curve)'),(33504,10,'Armorer Rogall, Jabbithole 3670'),(22169,50,'Agent Razios Prime Group, Jabbithole 895000')):
    c=tbl.index('Creature2')[cid]
    f=lambda k: lv(level,k)*mult('Creature2ArcheType',c['creature2ArcheTypeId'],k)*mult('Creature2Difficulty',c['creature2DifficultyId'],k)*mult('Creature2Tier',c['creature2TierId'],k)
    print(f"creature.{cid}.L{level}.health {f(HEALTH):.4f} 0.001 table formula; {src}")
    print(f"creature.{cid}.L{level}.assault {f(AP):.4f} 0.001 table formula")
    a=f(ARMOR); la=lv(level,ARMOR); print(f"creature.{cid}.L{level}.resist {curve(1234,a,level):.4f} 0.002 client mitigation curve GameFormula 1234")
