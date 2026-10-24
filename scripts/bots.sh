#!/usr/bin/env bash
# Turn the experimental player bots on or off, resize them, or toggle combat, without rebuilding anything.
# Settings live in .env (BOTS_TARGET, COMBAT, COMBAT_AGGRO); the world container is recreated to pick them up.
#
#   scripts/bots.sh status
#   scripts/bots.sh enable [COUNT]      keep COUNT bots online (default 50) and turn combat on
#   scripts/bots.sh disable             no bots (they are logged out; their characters stay in the database)
#   scripts/bots.sh count COUNT         change the number of bots
#   scripts/bots.sh combat on|off       combat, death and respawn for the whole world
#   scripts/bots.sh aggro on|off        hostile creatures also attack anyone who walks close (needs combat)
#   scripts/bots.sh focus TYPES|off     bots take quests with these objective ids first (e.g. 22,32) and their unfinished quests are dropped, to test one kind of objective
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
[ -f .env ] || { echo "No .env here. Run ./install.sh first." >&2; exit 1; }

get_env() { grep -E "^$1=" .env | tail -n1 | cut -d= -f2- || true; }

set_env() {
  local key="$1" value="$2"
  if grep -qE "^$key=" .env; then
    sed -i "s|^$key=.*|$key=$value|" .env
  else
    printf '%s=%s\n' "$key" "$value" >> .env
  fi
}

apply() {
  echo "Applying (recreating the world container, players are disconnected for a moment)..."
  docker compose up -d world
}

status() {
  echo "Bots (BOTS_TARGET) : $(get_env BOTS_TARGET)"
  echo "Combat (COMBAT)    : $(get_env COMBAT)"
  echo "Aggro (COMBAT_AGGRO): $(get_env COMBAT_AGGRO)"
  echo "Max companions     : $(get_env BOTS_MAX)"
  echo "Immortal bots      : $(get_env BOTS_IMMORTAL)"
}

number() { case "${1:-}" in ''|*[!0-9]*) echo "Need a number, got '${1:-}'." >&2; exit 1 ;; esac; }
onoff() { case "${1:-}" in on|1|yes) echo 1 ;; off|0|no) echo 0 ;; *) echo "Use on or off." >&2; exit 1 ;; esac; }

focus() {
  local pw; pw="$(get_env DB_ROOT_PASSWORD)"
  docker compose stop world
  docker compose exec -T db mysql -uroot -p"$pw" -e "use nexus_forever_character; delete o from character_quest_objective o join character_quest cq on cq.id=o.id and cq.questId=o.questId join \`character\` c on c.id=cq.id join nexus_forever_auth.account a on a.id=c.accountId where a.email like 'bot.%' and cq.state=1; delete cq from character_quest cq join \`character\` c on c.id=cq.id join nexus_forever_auth.account a on a.id=c.accountId where a.email like 'bot.%' and cq.state=1;" </dev/null
}

case "${1:-status}" in
  status) status ;;
  enable)  n="${2:-50}"; number "$n"; set_env BOTS_TARGET "$n"; set_env COMBAT 1; status; apply ;;
  disable) set_env BOTS_TARGET 0; status; apply ;;
  count)   number "${2:-}"; set_env BOTS_TARGET "$2"; [ "$2" -gt 0 ] && set_env COMBAT 1; status; apply ;;
  combat)  v="$(onoff "${2:-}")"; set_env COMBAT "$v"; [ "$v" = 0 ] && { set_env COMBAT_AGGRO 0; set_env BOTS_TARGET 0; echo "(bots disabled too, they need combat)"; }; status; apply ;;
  immortal) v="$(onoff "${2:-}")"; set_env BOTS_IMMORTAL "$v"; status; apply ;;
  aggro)   v="$(onoff "${2:-}")"; set_env COMBAT_AGGRO "$v"; [ "$v" = 1 ] && set_env COMBAT 1; status; apply ;;
  focus)   t="${2:-off}"; [ "$t" = off ] && t=''; set_env BOTS_FOCUS "$t"; focus; apply ;;
  *) sed -n '2,13p' "$0"; exit 1 ;;
esac
