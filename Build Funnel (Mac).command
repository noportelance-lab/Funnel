#!/bin/bash
# Double-click to build a fresh standalone Funnel.app (macOS).
# The finished app is written to  "ios version/Funnel.app"
cd "$(dirname "$0")" && ./build-mac.sh
echo ""
echo "Done. Your app is in the \"ios version\" folder next to this file."
read -n 1 -s -r -p "Press any key to close."
