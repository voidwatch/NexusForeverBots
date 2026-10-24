#!/bin/bash
# Runs once, on first init of an empty data volume (mariadb image /docker-entrypoint-initdb.d).
# Creates the three NexusForever databases and grants the app user access. Schema comes from EF migrations at WorldServer start.
set -euo pipefail
mariadb -uroot -p"${MARIADB_ROOT_PASSWORD}" <<SQL
CREATE DATABASE IF NOT EXISTS nexus_forever_auth      CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
CREATE DATABASE IF NOT EXISTS nexus_forever_character CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
CREATE DATABASE IF NOT EXISTS nexus_forever_world     CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
GRANT ALL PRIVILEGES ON nexus_forever_auth.*      TO '${MARIADB_USER}'@'%';
GRANT ALL PRIVILEGES ON nexus_forever_character.* TO '${MARIADB_USER}'@'%';
GRANT ALL PRIVILEGES ON nexus_forever_world.*     TO '${MARIADB_USER}'@'%';
FLUSH PRIVILEGES;
SQL
