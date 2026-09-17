#!/bin/bash
set -e
cd /home/claude/BigWalkVRExtras
R=/home/claude/ref
REFS=""
for f in $R/net6/*.dll; do REFS="$REFS -r:$f"; done
for f in BepInEx.Core BepInEx.Unity.IL2CPP BepInEx.Unity.Common 0Harmony Il2CppInterop.Runtime Il2CppInterop.Common \
         Assembly-CSharp Il2Cppmscorlib Il2CppSystem UnityEngine UnityEngine.CoreModule UnityEngine.UI UnityEngine.UIModule \
         UnityEngine.PhysicsModule UnityEngine.InputLegacyModule UnityEngine.TextRenderingModule Unity.TextMeshPro \
         Rewired_Core Mirror UniTask AudioSystem; do REFS="$REFS -r:$R/$f.dll"; done
REFS="$REFS -r:$R/pub/BigWalkVR.dll"
mkdir -p out
dotnet /usr/lib/dotnet/sdk/8.0.131/Roslyn/bincore/csc.dll -noconfig -nostdlib+ -nologo -target:library -optimize+ -deterministic \
  -langversion:latest -nullable:disable -unsafe- -out:out/BigWalkVRExtras.dll $REFS Plugin.cs
echo BUILD OK
