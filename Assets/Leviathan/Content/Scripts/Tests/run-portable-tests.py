#!/usr/bin/env python3
"""Production-code tests with explicit engine/transport doubles. Requires .NET 8 SDK.
Does not download game DLLs, install the mod, or claim Unity/co-op validation.
"""
import argparse
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    dotnet = shutil.which('dotnet')
    if not dotnet:
        parser.error('The .NET 8 SDK is required; dotnet was not found on PATH.')
    root = Path(__file__).resolve().parent.parent
    output = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix='leviathan-portable-'))
    output.mkdir(parents=True, exist_ok=True)
    suites = [
        ('CrossOwnerNetworkTests', 'CROSS_OWNER_NETWORK_TESTS', [root/'CoreCrossOwnerEffects.cs', root/'Tests/CrossOwnerNetworkTests.cs']),
        ('WireSelfTest', 'UNITY_EDITOR;CORE_WIRE_TEST_HOST', [root/'CoreWire.cs', root/'CoreWireSelfTest.cs', root/'Tests/WireSelfTestHost.cs']),
        ('AccretionProtocolTests', 'ACCRETION_PROTOCOL_TESTS', [root/'CoreProjectileCapture.cs', root/'CoreProjectileSpawnGuard.cs',
            root/'CoreDamageApplicationObservation.cs', root/'Orrery/OrreryAccretionReservoir.cs', root/'Tests/AccretionProtocolTests.cs']),
    ]
    # Match the existing Windows runner's intentionally isolated timed-effect test.
    timed = (root/'CoreTimedShipEffects.cs').read_text(encoding='utf-8-sig')
    timed = timed[:timed.index('[HarmonyPatch')].replace('using HarmonyLib;', '')
    timed_file = output/'CoreTimedShipEffectsUnderTest.cs'
    timed_file.write_text(timed, encoding='utf-8')
    suites.append(('TimedEffectNetworkTests', 'CORE_TIMED_NETWORK_TESTS', [timed_file, root/'Tests/TimedEffectNetworkTests.cs']))
    failures = []
    for name, symbols, files in suites:
        folder = output/name
        folder.mkdir(exist_ok=True)
        project = folder/(name+'.csproj')
        items = ''.join('<Compile Include="'+escape(str(p), {'"': '&quot;'})+'" />' for p in files)
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
            '<DefineConstants>'+symbols+'</DefineConstants><LangVersion>9.0</LangVersion><Nullable>disable</Nullable>'
            '</PropertyGroup><ItemGroup>'+items+'</ItemGroup></Project>', encoding='utf-8')
        print('\n=== '+name+' ===', flush=True)
        result = subprocess.run([dotnet, 'run', '--project', str(project), '-c', 'Release'],
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        (output/(name+'.log')).write_text(result.stdout, encoding='utf-8')
        print(result.stdout, flush=True)
        if result.returncode:
            failures.append(name)
    print('Results: '+str(output))
    if failures:
        print('Failed suites: '+', '.join(failures), file=sys.stderr)
        return 1
    print('Portable checks passed. Not a full Unity build, native physics/socket execution, or live co-op result.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
