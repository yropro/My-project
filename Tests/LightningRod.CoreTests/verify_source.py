#!/usr/bin/env python3
"""Static source checks, not a C#/Unity compilation or gameplay test."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

BASE = "42fd79733de00151bac0788eb8c531cfae83fbf9"
AUDIO = "Assets/Leviathan/Content/Scripts/CoreAudioRuntime.cs"
EXPECTED_AUDIO_BLOB = "a650db3f3b21ea358f43a98804f2c204f20e8a00"
ROOT = Path(__file__).resolve().parents[2]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-file", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    before = (args.baseline_file.read_bytes() if args.baseline_file else
              subprocess.check_output(["git", "show", f"{BASE}:{AUDIO}"], cwd=ROOT))
    after = (ROOT / AUDIO).read_text()
    checks: list[str] = []

    def check(condition: bool, name: str) -> None:
        if not condition:
            raise AssertionError(name)
        checks.append(name)

    actual = hashlib.sha1(b"blob " + str(len(before)).encode() + b"\0" + before).hexdigest()
    check(actual == EXPECTED_AUDIO_BLOB, "Baseline audio source matches GitHub blob SHA")
    old = before.decode()
    start = "    private static AudioClip ResolveClip"
    end = "    public static void Reset()"
    check(old[old.index(start):old.index(end)] == after[after.index(start):after.index(end)],
          "Existing asset lookup and cache discovery implementation preserved byte-for-byte")
    for constant, value in [("DefaultSpatialBlend", "0.75f"), ("UseNativeDistance", "-1f"),
                            ("PositionalAudioCleanupPaddingSeconds", "0.50f")]:
        check(f"public const float {constant} = {value};" in after, f"Preserved {constant}")
    check("Mathf.Max(1f," in after, "Preserved one-second minimum object cleanup lifetime")
    check("float playbackDurationSeconds = -1f, float? fadeOutStartSeconds = null)" in after,
          "New per-playback arguments are optional trailing parameters")
    check("effect.minPitch = effect.maxPitch = 1f;" in after and
          "SoundEffectPlayer.Category.Effects" in after, "Native base pitch and Effects mixer category retained")
    check(after.count("FadeOutStartSeconds.TryGetValue") == 1,
          "Clip default read only during playback setup, not during voice updates")
    check("CoreAudioDelayedFadeStop" not in after and after.count("class CoreAudioVoiceLifetime") == 1,
          "Single replacement playback-lifetime component, no duplicate old timer")

    prod = ROOT / "Assets/Leviathan/Content/Scripts"
    pure_paths = [prod / "CoreAudioPlaybackTiming.cs", prod / "Orrery/OrreryLightningRodState.cs"]
    for p in pure_paths:
        text = p.read_text()
        check("using UnityEngine;" not in text and "[HarmonyPatch" not in text,
              f"{p.name}: no Unity or Harmony dependency in portable state")
    state = pure_paths[1].read_text()
    check("private readonly TTarget target;" in state and
          "public TTarget Target { get { return target; } }" in state,
          "Stored target reference is immutable")
    check("nextStrikeAt = nowScaledSeconds + settings.StrikeIntervalSeconds;" in state,
          "Late strikes do not shorten the next cadence interval")

    test_dir = Path(__file__).parent
    project = ET.parse(test_dir / "LightningRod.CoreTests.csproj")
    for node in project.findall(".//Compile"):
        check((test_dir / node.attrib["Include"]).resolve().is_file(),
              "Test compile input exists: " + node.attrib["Include"])

    # Lexical delimiter check only: deliberately does not claim to parse/typecheck C#.
    token = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"')
    for p in [prod / "CoreAudioRuntime.cs", *pure_paths, test_dir / "Program.cs"]:
        text = token.sub("", p.read_text())
        stack: list[str] = []
        pairs = {"}": "{", ")": "(", "]": "["}
        for c in text:
            if c in "{([":
                stack.append(c)
            elif c in pairs:
                if not stack or stack.pop() != pairs[c]:
                    raise AssertionError("Mismatched delimiter: " + str(p))
        check(not stack, f"{p.name}: lexical delimiters balanced (not compilation)")

    report = {"validation": "static source checks only", "base_commit": BASE,
              "passed_checks": checks, "csharp_tests_executed": False,
              "unity_compiled": False, "two_peer_playtest": False}
    output = json.dumps(report, indent=2) + "\n"
    print(output, end="")
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(output)


if __name__ == "__main__":
    main()
