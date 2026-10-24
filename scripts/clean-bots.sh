#!/usr/bin/env bash
# List the clean bots (accounts bot.clean-N@bots.invalid, NF_BOTS_CLEAN=1): account, character id and name, world, quests completed / in the
# log, account creation time, and "retired" for characters that are no longer fresh (dev-zone-xp or level-set in /cache/truth/quest-truth-state.json;
# clean runs do not log them in again). Read only: clean accounts are never deleted, other runs never log them in.
#   scripts/clean-bots.sh [lines]
cd "$(dirname "$0")/.."
pw="$(grep -E '^DB_ROOT_PASSWORD=' .env | cut -d= -f2-)"
retired="$(docker compose exec -T world sh -c 'cat /cache/truth/quest-truth-state.json 2>/dev/null' </dev/null | python3 -c "
import json, sys
try: s = json.load(sys.stdin)
except Exception: s = {'Characters': {}}
print(' '.join(k for k, v in s.get('Characters', {}).items() if set(v.get('Lifetime', [])) & {'dev-zone-xp', 'level-set'}))")"
docker compose exec -T db mariadb -uroot -p"$pw" -e "select a.email, c.id as character_id, c.name, c.worldId as world,
  sum(cq.state=3) as completed, sum(cq.state in (1,2)) as in_log, a.createTime as account_created
  from nexus_forever_auth.account a left join nexus_forever_character.\`character\` c on c.accountId=a.id
  left join nexus_forever_character.character_quest cq on cq.id=c.id
  where a.email like 'bot.clean-%' group by a.id, c.id order by cast(substring_index(substring(a.email, 11), '@', 1) as unsigned)" </dev/null 2>&1 \
  | awk -v r=" $retired " 'NR==1 {print $0 "\tstatus"; next} {s = index(r, " " $2 " ") ? "retired" : ($2 == "NULL" ? "no character yet" : "active"); print $0 "\t" s}' \
  | head -"${1:-80}"
