#!/usr/bin/env python3
"""Source/preservation audit only. NOT a C# compile, Unity or co-op test.
Run with --repo ROOT --baseline DIR. Baselines must be exact aeb1021 source files.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct

EXPECTED = {
    'OrrerySpellRegistry.cs': '8d6bfc1a10ae540f52db3511864f5d3efc4af979',
    'OrreryNetwork.cs': 'f2699150cc2cbb8b4d60e52d29ed121f032bfa19',
    'OrreryPresentationNetwork.cs': '9d5add8c8c8620a632e29738efb397b8fb551c07',
    'OrrerySpellCompendium.cs': 'f91744ccda2fb0c2a1d951f808cfff82cca671ad',
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    source = args.repo / 'Assets/Leviathan/Content/Scripts/Orrery'
    checks = []

    def check(condition, label):
        if not condition:
            raise AssertionError(label)
        checks.append(label)

    for name, expected in EXPECTED.items():
        data = (args.baseline / name).read_bytes()
        actual = hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
        check(actual == expected, 'Pinned baseline blob: ' + name)

    compendium = (source / 'OrrerySpellCompendium.cs').read_text()
    old = (args.baseline / 'OrrerySpellCompendium.cs').read_text()
    check(compendium.startswith(old.rstrip()[:-1].rstrip()), 'All prior Compendium definitions unchanged')
    block = compendium.split('public struct Profile')[1].split('public static readonly Profile LL')[0]
    fields = [field.strip() for _, group in re.findall(r'public (float|int) ([^;]+);', block)
              for field in group.split(',')]
    check(len(fields) == 40, '40 Arc profile tuning fields')
    runtime = '\n'.join((source / name).read_text() for name in (
        'OrreryArcResonance.cs', 'OrreryArcResonancePresentation.cs', 'OrreryArcResonanceVisual.cs'))
    for field in fields:
        check(re.search(r'\.' + field + r'\b', runtime) is not None, 'Consumed tuning: ' + field)

    network = (source / 'OrreryNetwork.cs').read_text()
    registration = '''        CoreNetworkPresentation.Register("Orrery/ArcResonance",
            render: OrreryArcResonancePresentation.Render,
            update: OrreryArcResonancePresentation.Update,
            forget: OrreryArcResonancePresentation.ForgetShip,
            died: OrreryArcResonancePresentation.ForgetShip,
            reset: OrreryArcResonancePresentation.Reset);
'''
    check(network.count(registration) == 1 and network.replace(registration, '') ==
          (args.baseline / 'OrreryNetwork.cs').read_text(), 'Only Arc registration added to module')
    bank = (source / 'OrreryPresentationNetwork.cs').read_text()
    additions = ['    // 7 is reserved by the concurrent Accretion Disk branch. Do not reuse it.\n'
                 '    public const byte CodecArcResonance = 8;\n',
                 '                OrreryArcResonancePresentation.Publish(owner);\n']
    stripped = bank
    for addition in additions:
        check(stripped.count(addition) == 1, 'Unique network addition: ' + addition.strip().splitlines()[-1])
        stripped = stripped.replace(addition, '')
    check(stripped == (args.baseline / 'OrreryPresentationNetwork.cs').read_text(),
          'Packet framing, budgets and existing publishers unchanged')

    registry = (source / 'OrrerySpellRegistry.cs').read_text()
    check('OrreryArcResonance.Execute' in registry and 'OrrerySpellRuntime.ExecuteTesla' not in registry,
          'LL registry routed to Arc only')
    check('OrrerySpellCompendium.ArcResonance.Id' in registry and
          'public const ushort Id = 2;' in compendium, 'Stable LL spell identity 2')
    check('Pure(OrreryElement.Lightning, 3)' not in registry, 'No speculative LLL unlock')
    check('[HarmonyPatch' not in runtime, 'Arc files introduce no engine Harmony hooks')
    owner = (source / 'OrreryArcResonance.cs').read_text()
    presentation = (source / 'OrreryArcResonancePresentation.cs').read_text()
    check('OrreryDamageRouter.Route' not in presentation and '.Clock.Tick' not in presentation,
          'No damage or gameplay schedule in presentation adapter')
    check('TargetKey' in owner and 'key.Equals(state.TargetKey)' in owner, 'Target incarnation check present')
    check('state.CommitInProgress' in owner and 'state.RoutingDamage' in owner,
          'Commit/damage reentrancy boundaries present')
    check('OrreryNetwork.Channel<WireState>' in presentation and 'channel.Publish' in presentation,
          'Uses shared typed channel')
    wire = presentation.split('internal static void Wire(')[1].split('public static void Publish(')[0]
    wire_calls = re.findall(r'wire\.(Byte|Flags|UInt32|Position|Float)\(ref state\.(\w+)\)', wire)
    check(wire_calls == [('Byte', 'RecipeSize'), ('Byte', 'StrikeIndex'), ('Flags', 'Active'),
                         ('UInt32', 'TargetNetId'), ('Position', 'TargetPosition'), ('Float', 'StrikeAgeSeconds')],
          'One explicit 19-byte wire field order')
    # Independent layout arithmetic fixture only; this does not execute CoreWire.
    vector = struct.pack('<BBBIfff', 2, 1, 1, 0x04030201, 1, -2, .25)
    check(vector.hex() == '020101010203040000803f000000c00000803e', 'Independent golden layout arithmetic')
    check(len(vector) == 19 and len(vector) + 6 <= 32, 'Single-record payload budget')
    check('if (s.StrikeIndex == v.StrikeIndex) return;' in presentation,
          'Repeated strike snapshots deduplicated before playback')
    check('v.LeaseUntil = Time.time +' in presentation, 'Bounded target-reference lease')
    check('OrrerySpellCompendium.ArcResonance.ThunderClipName' in presentation and
          'v.Profile.ThunderPlaybackDurationSeconds, v.Profile.ThunderFadeOutStartSeconds' in presentation,
          'Explicit independent audio duration and fade')

    # Lexical checks only, not parsing/type resolution.
    tokens = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"')
    audited = [source / name for name in (
        'OrreryArcResonance.cs', 'OrreryArcResonancePresentation.cs', 'OrreryArcResonanceVisual.cs',
        'OrreryCombat.cs', 'OrreryNetwork.cs', 'OrreryPresentationNetwork.cs',
        'OrrerySpellCompendium.cs', 'OrrerySpellLifetime.cs', 'OrrerySpellRegistry.cs')]
    audited.append(args.repo / 'Assets/Editor/OrreryArcResonanceAssetBuilder.cs')
    for path in sorted(audited):
        text = tokens.sub('', path.read_text())
        stack = []
        pairs = {'}': '{', ')': '(', ']': '['}
        for char in text:
            if char in '{([':
                stack.append(char)
            elif char in pairs:
                check_ok = bool(stack) and stack.pop() == pairs[char]
                if not check_ok:
                    raise AssertionError('Unbalanced delimiters: ' + str(path))
        check(not stack, 'Lexical delimiters: ' + path.name)

    report = {'validation': 'source audit only', 'base': 'aeb1021', 'checks': checks,
              'golden_payload_hex': vector.hex(), 'payload_bytes': len(vector),
              'production_csharp_executed': False, 'unity_rendered': False, 'peer_tested': False,
              'asset_preparation_executed': False}
    text = json.dumps(report, indent=2) + '\n'
    print(text)
    if args.report:
        args.report.write_text(text)


if __name__ == '__main__':
    main()
