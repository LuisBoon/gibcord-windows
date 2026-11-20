#!/bin/bash
# Sprawdzenie, czy aplikacja się kompiluje (tylko C#, na Linuksie). Pełne budowanie: GitHub Actions na Windowsie.
# Wypisuje tylko błędy i ostrzeżenia CS; kod wyjścia ≠ 0, gdy kompilacja się nie udała.
export DOTNET_ROOT=${DOTNET_ROOT:-/opt/dotnet}
export PATH=${DOTNET_ROOT}:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$(dirname "$0")/.."
command -v dotnet >/dev/null || { echo "Brak dotnet (DOTNET_ROOT=$DOTNET_ROOT)"; exit 2; }
out=$(dotnet msbuild src/GibCord.App -t:Compile -p:Configuration=Debug -p:WindowsAppSDKSelfContained=false -nologo -v:q 2>&1)
code=$?
echo "$out" | grep -E "error|warning CS" | grep -v "/errorreport" | sed -E 's#^ *(/opt/dc/windows/)?(src/)?##; s# \[/opt[^]]*\]##' | sort -u
[ $code -eq 0 ] && echo "Kompilacja OK" || { echo "Kompilacja NIEUDANA (kod $code)"; exit 1; }
