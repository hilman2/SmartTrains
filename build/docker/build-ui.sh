#!/bin/sh
# Copies the panel sources to /tmp, installs the pinned dependencies,
# type-checks and bundles. See compose.yml.
set -eu

rm -rf /tmp/ui
mkdir -p /tmp/ui
cd /src/src/SmartTrains.UI
tar --exclude=node_modules --exclude=build -cf - . | tar -xf - -C /tmp/ui
cd /tmp/ui

if [ -f package-lock.json ]; then
    npm ci
else
    npm install
fi
cp package-lock.json /out/package-lock.json

npm run typecheck
rm -rf /out/dist
SMARTTRAINS_UI_OUT=/out/dist npm run build
ls -l /out/dist

# Render the bundle against the game's real module exports before anything
# ships (tests/ui/smoke.mjs). A failure here would otherwise surface in the
# game as a missing interface.
node /src/tests/ui/smoke.mjs /out/dist/SmartTrains.mjs /out/preview.html
