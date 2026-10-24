#!/usr/bin/env bash
# Row counts of every account and character that is NOT a clean bot (accounts bot.clean-N@bots.invalid), one line per table.
# Clean-bot runs must leave these numbers unchanged: run it before and after and diff the two files.
#   scripts/db-counts.sh > /tmp/before.txt; ...run...; scripts/db-counts.sh > /tmp/after.txt; diff /tmp/before.txt /tmp/after.txt
# The last lines count the clean bots themselves (accounts, characters, quest rows by state), for information.
cd "$(dirname "$0")/.."
pw="$(grep -E '^DB_ROOT_PASSWORD=' .env | cut -d= -f2-)"
clean="a.email like 'bot.clean-%'"
own="(select c.id from nexus_forever_character.\`character\` c join nexus_forever_auth.account a on a.id=c.accountId where not ($clean))"
q="select 'account', count(*) from nexus_forever_auth.account a where not ($clean)"
q="$q union all select 'account_crc', coalesce(sum(crc32(concat_ws('|',a.id,a.email,a.s,a.v,a.createTime))),0) from nexus_forever_auth.account a where not ($clean)"
q="$q union all select 'character', count(*) from nexus_forever_character.\`character\` c where c.id in $own"
q="$q union all select 'character_crc', coalesce(sum(crc32(concat_ws('|',c.id,c.accountId,c.name,c.level,c.worldId,c.locationX,c.locationY,c.locationZ,c.lastOnline))),0) from nexus_forever_character.\`character\` c where c.id in $own"
for t in $(docker compose exec -T db mariadb -uroot -p"$pw" -N -e "select table_name from information_schema.tables where table_schema='nexus_forever_character' and table_name like 'character\\_%' and table_name not in ('character_mail','character_mail_attachment','character_create') order by table_name" </dev/null 2>/dev/null); do
  q="$q union all select '$t', count(*) from nexus_forever_character.$t x where x.id in $own"
done
q="$q union all select 'item', count(*) from nexus_forever_character.item x where x.ownerId in $own"
q="$q union all select 'character_mail', count(*) from nexus_forever_character.character_mail x where x.recipientId in $own"
q="$q union all select 'residence', count(*) from nexus_forever_character.residence x where x.ownerId in $own"
q="$q union all select 'guild_member', count(*) from nexus_forever_character.guild_member x where x.characterId in $own"
docker compose exec -T db mariadb -uroot -p"$pw" -N -e "$q" </dev/null 2>&1 | tr '\t' ' '
docker compose exec -T db mariadb -uroot -p"$pw" -N -e "select '# clean accounts', count(*) from nexus_forever_auth.account a where $clean
 union all select '# clean characters', count(*) from nexus_forever_character.\`character\` c join nexus_forever_auth.account a on a.id=c.accountId where $clean
 union all select concat('# clean quest rows state ', cq.state), count(*) from nexus_forever_character.character_quest cq join nexus_forever_character.\`character\` c on c.id=cq.id join nexus_forever_auth.account a on a.id=c.accountId where $clean group by cq.state" </dev/null 2>&1 | tr '\t' ' '
