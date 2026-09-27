#!/bin/sh
# Copies the read-only sources to /tmp and runs the tests there.
# $1 is the console verbosity: "minimal" or "detailed".
set -eu

rm -rf /tmp/work
mkdir -p /tmp/work
cp -r /src/Directory.Build.props /src/src /src/tests /tmp/work/
cd /tmp/work

# A hanging test fails after 60 s and names itself, instead of stalling the run.
dotnet test tests/SmartTrains.Core.Tests/SmartTrains.Core.Tests.csproj \
    --blame-hang-timeout 60s \
    --logger "console;verbosity=${1:-minimal}"
