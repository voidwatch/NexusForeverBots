#!/bin/bash
# Fetch protocol sources for client-message research into research/protocol/ (gitignored raw cache).
# Sources: NexusForever game_rework message models, MaxtorCoder/WildstarPacketParser (sniff parser), 1 req/s.
set -e
cd "$(dirname "$0")/../../research/protocol"
UA="NexusForeverBots-research/1.0 (protocol study)"
mkdir -p upstream wpp
grep -oE '"path": "Source/NexusForever.Network.World/Message/Model/[^"]*/?(Client|Server)(P2PTrad|Loot|Auction|Commodity|Instance|SetInstance|ClosedInstance|ShowInstance|HideInstance|ResetSingle|ResetInst|GroupLoot|Marketplace|RequestOwned|RequestCommod|Chat|RandomRoll)[^"]*\.cs"' tree-game_rework.json \
 | sed 's/"path": "//;s/"$//' | while read p; do f=upstream/$(basename "$p"); [ -s "$f" ] || { curl -s -A "$UA" -o "$f" "https://raw.githubusercontent.com/NexusForever/NexusForever/game_rework/$p"; sleep 1; }; done
grep -oE '"path": "WildstarPacketParser/[^"]*\.cs"' tree-wpp.json | sed 's/"path": "//;s/"$//' | while read p; do f=wpp/$(basename "$p"); [ -s "$f" ] || { curl -s -A "$UA" -o "$f" "https://raw.githubusercontent.com/MaxtorCoder/WildstarPacketParser/master/$p"; sleep 1; }; done
echo upstream $(ls upstream | wc -l) wpp $(ls wpp | wc -l)
