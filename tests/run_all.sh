#!/bin/sh
# Headless tests for Pedal Master N (Linux or WSL: .NET 10 SDK,
# Python 3 with numpy and scipy). No ReBuzz needed: the harness drives the engine
# (MasterEngine.cs) directly, with the same command line as the native machine's
# C++ harness, so the original Buzz 1503 test scripts run unchanged.
#
# Optional: pass the path of a native C++ harness (built from the Buzz 1503 source,
# pedal-master-n-1503 tests/harness.cpp) to also run the side-by-side check.
set -e
cd "$(dirname "$0")"
dotnet build harness-src/Harness.csproj -c Release -o build/harness >/dev/null
dotnet build loudness-src/LoudnessTest.csproj -c Release -o build/lt >/dev/null
cp build/harness/* .
for t in t1 t2 t3 t4 t5 t5b t6 t7 t8 t10 t12; do echo "── $t"; python3 $t.py; done
echo "── t9 (GUI loudness maths)"; python3 t9_loudness.py build/lt/lt
echo "── sleep on silent input"; python3 sleep_check.py ./harness
if [ -n "$1" ]; then echo "── against the native C++ build"; python3 equivalence.py "$1" ./harness; fi
