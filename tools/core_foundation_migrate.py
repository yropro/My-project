from pathlib import Path
import re, subprocess

# Runs only on the disposable Core migration branch; generated output is audited
# before the branch is allowed to replace skill-trees.
ROOT = Path.cwd()
S = ROOT / 'Assets/Leviathan/Content/Scripts'

def fail(msg): raise RuntimeError('[CoreMigration] ' + msg)
def read(p): return p.read_text(encoding='utf-8-sig')
def write(p,text):
    bom = p.exists() and p.read_bytes().startswith(b'\xef\xbb\xbf')
    p.write_text(text, encoding='utf-8-sig' if bom else 'utf-8', newline='')
def replace_once(text, old, new, label):
    if old not in text: fail('missing anchor: ' + label)
    return text.replace(old,new,1)
def git(*args): subprocess.run(['git',*args], cwd=ROOT, check=True)

def extract_class(text, marker, next_marker=None):
    start=text.find(marker)
    if start<0: fail('missing block '+marker)
    if next_marker:
        end=text.find(next_marker,start)
        if end<0: fail('missing end marker '+next_marker)
    else:
        end=len(text)
    return text[:start]+text[end:], text[start:end].rstrip()+"\n"

promoted={
 'LeviathanCombat.cs':'CoreCombat.cs',
 'LeviathanCombatHistory.cs':'CoreCombatHistory.cs',
 'LeviathanCombatState.cs':'CoreCombatState.cs',
 'LeviathanNetwork.cs':'CoreNetwork.cs',
 'LeviathanSpecializationFramework.cs':'CoreSpecializationFramework.cs',
}
for old,new in promoted.items():
    if not (S/old).exists(): fail('missing '+old)
    if (S/new).exists(): fail('target exists '+new)
if not (S/'CoreClassRuntime.cs').exists(): fail('CoreClassRuntime.cs must exist before migration')

# Extract class-owned policy from the generic specialization framework BEFORE
# declaration discovery, so those classes intentionally retain Leviathan names.
framework_path=S/'LeviathanSpecializationFramework.cs'
fw=read(framework_path)
fw, pointbank = extract_class(fw,
    'public sealed class LeviathanEvolutionPointBank',
    'public static class LeviathanSpecializationPersistence')
fw, persistence = extract_class(fw,
    'public static class LeviathanSpecializationPersistence',
    'internal struct LeviathanSpecializationAggregateCacheValue')
fw, catalog = extract_class(fw,
    'public static class LeviathanSpecializationCatalog')
write(framework_path, fw.rstrip()+"\n")
policy_path=S/'LeviathanSpecializationPolicy.cs'
policy='''using StarVortex;\nusing System;\nusing System.Collections.Generic;\nusing System.IO;\nusing UnityEngine;\n\n/// <summary>\n/// Leviathan-owned policy adapters for the shared specialization engine.\n/// Currency, persistence namespace and default tree catalog intentionally live\n/// outside Core so another standalone class can supply different policy.\n/// </summary>\n'''+pointbank+'\n'+persistence+'\n'+catalog
policy_path.write_text(policy,encoding='utf-8',newline='')
(S/'LeviathanSpecializationPolicy.cs.meta').write_text('''fileFormatVersion: 2\nguid: 4860d43b26ea42a293bcd0621136692c\nMonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n''',encoding='utf-8')

# Discover ONLY types still declared in promoted files.
type_re=re.compile(r'^\s*(?:(?:public|internal|private|protected)\s+)?(?:(?:sealed|abstract|static|partial|readonly)\s+)*(?:class|struct|interface|enum)\s+(I?Leviathan[A-Za-z0-9_]+)\b',re.M)
rename={}
for fn in promoted:
    for m in type_re.finditer(read(S/fn)):
        old=m.group(1)
        new=('ICore'+old[len('ILeviathan'):] if old.startswith('ILeviathan') else 'Core'+old[len('Leviathan'):])
        rename.setdefault(old,new)
for old,new in {'LeviathanCombat':'CoreCombat','LeviathanCombatHistory':'CoreCombatHistory','LeviathanCombatState':'CoreCombatState','LeviathanNetwork':'CoreNetwork','LeviathanSpecializationRuntime':'CoreSpecializationRuntime','LeviathanSpecializationRegistry':'CoreSpecializationRegistry'}.items():
    if rename.get(old)!=new: fail('required mapping absent '+old)
print('Promoted declarations:',len(rename))
ordered=sorted(rename.items(),key=lambda x:len(x[0]),reverse=True)
excluded={'Library','Temp','obj','bin','Logs','PackagesCache'}
for p in ROOT.rglob('*.cs'):
    if any(x in excluded for x in p.parts): continue
    before=read(p); after=before
    for old,new in ordered:
        after=re.sub(r'(?<![A-Za-z0-9_])'+re.escape(old)+r'(?![A-Za-z0-9_])',new,after)
    if after!=before: write(p,after)

# Truthful naming in Core files while preserving class-owned policy names.
cp=S/'LeviathanCombat.cs'; t=read(cp)
t=t.replace('Shared Leviathan combat provenance/correlation foundation.','Shared project combat provenance/correlation foundation.')
t=replace_once(t,'        public const byte DronesTurretsBeacons = 7;','        public const byte DronesTurretsBeacons = 7;\n        public const byte Orrery = 8;','Orrery skill id')
t=replace_once(t,'        WeaponSlot = 8,','        WeaponSlot = 8,\n        Satellite = 9,','Satellite contributor kind')
write(cp,t)
sp=S/'LeviathanCombatState.cs'; write(sp,read(sp).replace('Bounded local registry for temporary Leviathan combat truth.','Bounded local registry for temporary semantic combat truth.'))
sfp=S/'LeviathanSpecializationFramework.cs'; write(sfp,read(sfp).replace('// LEVIATHAN SPECIALIZATION FRAMEWORK','// CORE SPECIALIZATION FRAMEWORK'))

# Register Leviathan class predicate; native lifecycle observation is Core-owned.
mp=S/'LeviathanMod.cs'; t=read(mp)
anchor='        LeviathanSkillSystem.Register();'
insert='''        LeviathanSkillSystem.Register();\n\n        CoreClassRuntime.RegisterLocalClass(\n            CoreClassId.Leviathan,\n            delegate(Pilot pilot)\n            {\n                return pilot != null &&\n                    pilot.GetUpgradeLevel(\n                        LeviathanSpecializationCurrency.UpgradeKey) >= 1;\n            });'''
t=replace_once(t,anchor,insert,'Leviathan class registration'); write(mp,t)

# Generic transport gate and stable Orrery slot. Wire format/version unchanged.
np=S/'LeviathanNetwork.cs'; t=read(np)
t=replace_once(t,'    public const byte SlotBehemoth = 5;','    public const byte SlotBehemoth = 5;\n    public const byte SlotOrrery = 6;','Orrery slot')
t=replace_once(t,'    private static int specBurstRemaining;','    private static int specBurstRemaining;\n    private static int classClearBurstRemaining;\n    private static int lastSeenClassTransitionRevision = int.MinValue;','class fields')
old='''            // Nothing to say if this player is not a Leviathan.\n            if (!LeviathanMod.PlayerHasLeviathan())\n                return;\n\n            bool wantSpec = ShouldSendSpecBlock();'''
new='''            bool hasActiveClass = CoreClassRuntime.HasActiveLocalClass;\n            int classTransitionRevision = CoreClassRuntime.TransitionRevision;\n\n            if (lastSeenClassTransitionRevision == int.MinValue)\n            {\n                // Establish a vanilla/initial baseline without manufacturing a\n                // class-exit clear burst before any class transition occurred.\n                lastSeenClassTransitionRevision = classTransitionRevision;\n            }\n            else if (classTransitionRevision != lastSeenClassTransitionRevision)\n            {\n                lastSeenClassTransitionRevision = classTransitionRevision;\n                classClearBurstRemaining = hasActiveClass ? 0 : SpecBurstPackets;\n            }\n\n            bool publishingClassClear =\n                !hasActiveClass && classClearBurstRemaining > 0;\n\n            if (!hasActiveClass && !publishingClassClear)\n                return;\n\n            if (publishingClassClear)\n                ClearLocalSlots();\n\n            bool wantSpec = hasActiveClass && ShouldSendSpecBlock();'''
t=replace_once(t,old,new,'active class gate')
old='''            if ((blockFlags & BlockFlagSpec) != 0 && specBurstRemaining > 0)\n                specBurstRemaining--;'''
new='''            if ((blockFlags & BlockFlagSpec) != 0 && specBurstRemaining > 0)\n                specBurstRemaining--;\n\n            if (publishingClassClear &&\n                (blockFlags & BlockFlagDynamic) != 0 &&\n                classClearBurstRemaining > 0)\n            {\n                classClearBurstRemaining--;\n            }'''
t=replace_once(t,old,new,'clear decrement')
t=replace_once(t,'        ResetCombatTransport();','        CoreClassRuntime.Reset();\n        ResetCombatTransport();','class reset')
write(np,t)

# Orrery consumes canonical shared ids instead of local magic numbers.
op=S/'OrreryCombat.cs'; t=read(op)
t=replace_once(t,'    public const byte SkillId = 8;','    public const byte SkillId = CoreCombat.SkillIds.Orrery;','Orrery combat id')
t=replace_once(t,'    public const byte SatelliteContributorKindId = 9;','    public const byte SatelliteContributorKindId = (byte)CoreCombat.ContributorKind.Satellite;','Orrery contributor id')
t=t.replace('(CoreCombat.ContributorKind)SatelliteContributorKindId,','CoreCombat.ContributorKind.Satellite,')
write(op,t)
on=S/'OrreryNetwork.cs'; t=read(on); t=replace_once(t,'    public const byte SharedSlotId = 6;','    public const byte SharedSlotId = CoreNetwork.SlotOrrery;','Orrery network id'); write(on,t)

# Rename source+meta preserving existing GUID files.
for old,new in promoted.items():
    git('mv',str((S/old).relative_to(ROOT)),str((S/new).relative_to(ROOT)))
    om=S/(old+'.meta')
    if om.exists(): git('mv',str(om.relative_to(ROOT)),str((S/(new+'.meta')).relative_to(ROOT)))

# Static migration invariants.
net=read(S/'CoreNetwork.cs'); combat=read(S/'CoreCombat.cs')
for f in ['private const byte Magic0 = 0x4C','private const byte Magic1 = 0x56','public const byte ProtocolVersion = 2','public const byte SlotStellarConverter = 1','public const byte SlotStarfire = 2','public const byte SlotPredator = 3','public const byte SlotConstrictor = 4','public const byte SlotBehemoth = 5','public const byte SlotOrrery = 6','CoreClassRuntime.HasActiveLocalClass','classClearBurstRemaining']:
    if f not in net: fail('network invariant '+f)
for f in ['public const byte Growth = 1','public const byte Predator = 2','public const byte Constrictor = 3','public const byte Behemoth = 4','public const byte Starfire = 5','public const byte StellarConverter = 6','public const byte DronesTurretsBeacons = 7','public const byte Orrery = 8','Satellite = 9']:
    if f not in combat: fail('combat invariant '+f)
if 'if (!LeviathanMod.PlayerHasLeviathan())' in net: fail('old network gate remains')
if 'CoreCombat.SkillIds.Orrery' not in read(S/'OrreryCombat.cs'): fail('Orrery core skill id missing')
if 'CoreCombat.ContributorKind.Satellite' not in read(S/'OrreryCombat.cs'): fail('Orrery contributor id missing')
if 'CoreNetwork.SlotOrrery' not in read(S/'OrreryNetwork.cs'): fail('Orrery slot missing')

# Policy declarations must remain Leviathan-owned; all promoted declarations must disappear.
policy=read(policy_path)
for name in ['LeviathanEvolutionPointBank','LeviathanSpecializationPersistence','LeviathanSpecializationCatalog']:
    if name not in policy: fail('extracted policy missing '+name)
stale=[]
for p in ROOT.rglob('*.cs'):
    if any(x in excluded for x in p.parts): continue
    text=read(p)
    for old,_ in ordered:
        if re.search(r'(?<![A-Za-z0-9_])'+re.escape(old)+r'(?![A-Za-z0-9_])',text): stale.append(str(p.relative_to(ROOT))+': '+old)
if stale: fail('stale promoted identifiers:\n'+'\n'.join(sorted(set(stale))))
subprocess.run(['git','diff','--check'],cwd=ROOT,check=True)
print(subprocess.run(['git','diff','--stat'],cwd=ROOT,text=True,capture_output=True,check=True).stdout)
