param(
    [string]$UnityEditorRoot = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor',
    [string]$GameManaged = 'D:\SteamLibrary\steamapps\common\Star Vortex\Star Vortex_Data\Managed',
    [string]$OutputDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'leviathan-network-tests')
)
$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path $PSScriptRoot -Parent
$projectRoot = [IO.Path]::GetFullPath((Join-Path $scriptsRoot '..\..\..\..'))
$projectFile = Join-Path $projectRoot 'LeviathanMod.csproj'
if (!(Test-Path -LiteralPath $projectFile)) { throw 'Generate Unity project files before running this test.' }
[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$references = @($project.Project.ItemGroup.Reference.HintPath | Where-Object { $_ } | ForEach-Object {
    $referencePath = $_
    if (![IO.Path]::IsPathRooted($referencePath)) { $referencePath = Join-Path $projectRoot $referencePath }
    '/reference:"' + $referencePath + '"'
})
$urp = Join-Path $projectRoot 'Library\ScriptAssemblies\Unity.RenderPipelines.Universal.Runtime.dll'
$references = @($references + ('/reference:"' + $urp + '"') | Select-Object -Unique)
$framework = @($references | Where-Object { $_ -match '[\\/]NetStandard[\\/]' })
$sdkLine = @(dotnet --list-sdks)[-1]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'Cannot locate the .NET SDK compiler.' }
$compiler = Join-Path $Matches[2] ($Matches[1] + '\Roslyn\bincore\csc.dll')
$mono = Join-Path $UnityEditorRoot 'Data\MonoBleedingEdge\bin\mono.exe'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath $scriptsRoot -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/]\.claude[\\/]' } |
    ForEach-Object { '"' + $_.FullName + '"' })
function Compile-NetworkCheck($name, $options, $refs, $files) {
    $response = Join-Path $OutputDirectory ($name + '.rsp')
    $argsForCompiler = @('/nologo','/nostdlib+','/langversion:9.0',
        ('/out:"' + (Join-Path $OutputDirectory $name) + '"')) + $options + $refs + $files
    Set-Content -LiteralPath $response -Value $argsForCompiler -Encoding utf8
    & dotnet $compiler ('@' + $response)
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $name" }
}
Compile-NetworkCheck 'LeviathanMod.dll' @('/target:library') $references $sources
Compile-NetworkCheck 'NetworkIntegrationTests.exe' @('/target:exe','/define:CORE_NETWORK_TESTS','/main:NetworkIntegrationTests') $references $sources
$env:CORE_NETWORK_TEST_ASSEMBLIES = $GameManaged + ';' + (Join-Path $projectRoot 'Library\ScriptAssemblies') + ';' + (Join-Path $projectRoot 'Packages\Star Vortex')
& $mono (Join-Path $OutputDirectory 'NetworkIntegrationTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Network integration tests failed.' }
Compile-NetworkCheck 'CrossOwnerNetworkTests.exe' @('/target:exe','/define:CROSS_OWNER_NETWORK_TESTS') $framework @(
    ('"' + (Join-Path $scriptsRoot 'CoreCrossOwnerEffects.cs') + '"'),
    ('"' + (Join-Path $PSScriptRoot 'CrossOwnerNetworkTests.cs') + '"'))
& $mono (Join-Path $OutputDirectory 'CrossOwnerNetworkTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Cross-owner tests failed.' }
Compile-NetworkCheck 'WireSelfTest.exe' @('/target:exe','/define:UNITY_EDITOR,CORE_WIRE_TEST_HOST') $framework @(
    ('"' + (Join-Path $scriptsRoot 'CoreWire.cs') + '"'),
    ('"' + (Join-Path $scriptsRoot 'CoreWireSelfTest.cs') + '"'),
    ('"' + (Join-Path $PSScriptRoot 'WireSelfTestHost.cs') + '"'))
& $mono (Join-Path $OutputDirectory 'WireSelfTest.exe')
if ($LASTEXITCODE -ne 0) { throw 'CoreWire self-tests failed.' }
$timedSource = Get-Content -LiteralPath (Join-Path $scriptsRoot 'CoreTimedShipEffects.cs') -Raw
$timedSource = $timedSource.Substring(0, $timedSource.IndexOf('[HarmonyPatch')).Replace('using HarmonyLib;', '')
$timedUnderTest = Join-Path $OutputDirectory 'CoreTimedShipEffectsUnderTest.cs'
Set-Content -LiteralPath $timedUnderTest -Value $timedSource -Encoding utf8
Compile-NetworkCheck 'TimedEffectNetworkTests.exe' @('/target:exe','/define:CORE_TIMED_NETWORK_TESTS') $framework @(
    ('"' + $timedUnderTest + '"'),
    ('"' + (Join-Path $PSScriptRoot 'TimedEffectNetworkTests.cs') + '"'))
& $mono (Join-Path $OutputDirectory 'TimedEffectNetworkTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Timed-effect network tests failed.' }
Write-Output "Validated build and tests are in $OutputDirectory. Nothing was deployed."
