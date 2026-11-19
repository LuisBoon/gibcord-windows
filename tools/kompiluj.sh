#!/bin/bash
# Sprawdzenie, czy aplikacja się kompiluje (tylko C#, na Linuksie). Pełne budowanie: GitHub Actions na Windowsie.
export DOTNET_ROOT=${DOTNET_ROOT:-/opt/dotnet} PATH=${DOTNET_ROOT}:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$(dirname "$0")/.."
dotnet msbuild src/GibCord.App -t:Compile -p:Configuration=Debug -p:WindowsAppSDKSelfContained=false -nologo -v:q 2>&1 \
  | grep -E "error|warning CS" | sed -E 's#^/opt/dc/windows/(src/)?##; s# \[/opt[^]]*\]##' | sort -u
