#!/usr/bin/env bash
# NexusForever Bots installer (Docker only): checks Docker, validates your WildStar client, writes .env from
# .env.example, generates the game tables and maps from the client when data/ lacks them, builds and starts the stack,
# verifies it, and optionally loads world data and creates your first account. Safe to re-run.
#
# You must provide the WildStar client yourself (build 16042). This script never downloads game files.
#
#   ./install.sh                                         interactive
#   ./install.sh --client-dir /games/WildStar --yes      unattended (accepts the defaults below)
#   NF_ACCOUNT_PASSWORD=... ./install.sh --client-dir DIR --yes --realm-host 192.168.1.10 --account me@example.com
#   ./install.sh --check                                 only check that a running stack is reachable
#   ./uninstall.sh                                       remove what this installer created
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

# SHA-1 of WildStar64.exe from the final live client, build 16042 (the build this server's tables and code match)
KNOWN_CLIENT_SHA1="a5974e9628897fc1965b7a740bce68df5b621175"
DEFAULT_PROJECT="nexusforeverbots"

YES=0
CHECK_ONLY=0
SKIP_CLIENT_CHECK=0
SKIP_WORLD_DATA=0
SKIP_ACCOUNT=0
SKIP_FIREWALL=0
CLIENT_DIR_ARG="${CLIENT_DIR:-}"
PROJECT_ARG=""
WORLD_PORT_ARG=""
AUTH_PORT_ARG=""
STS_PORT_ARG=""
REALM_HOST_ARG=""
ACCOUNT_ARG=""
BOTS_ARG=""
COMBAT_ARG=""
CLIENT_DIR=""
PATCH=""

usage() {
  cat <<'EOF'
Usage: ./install.sh [options]
      --client-dir PATH   WildStar client install folder (holds Client64/WildStar64.exe and Patch/)
  -y, --yes               do not prompt, accept safe defaults (a client hash mismatch is accepted with a warning)
      --realm-host HOST   IP or DNS name game clients use to reach this machine (default: detected LAN IP)
      --account EMAIL     create this game account (password from prompt or NF_ACCOUNT_PASSWORD)
      --bots N            keep N ambient player bots online (0 = no bots, the default). Change later with scripts/bots.sh
      --combat            turn on the combat and death layer (needed for bots to fight and quest). Off by default
      --project NAME      compose project name for a second, separate stack (own containers, database, images)
      --world-port N      host ports for that second stack (defaults 24000, 23115, 6600; clients expect 24000)
      --auth-port N
      --sts-port N
      --skip-client-check do not ask for or validate the client (only when data/tbl and data/map are already complete)
      --skip-world-data   do not load creature/spawn data
      --skip-account      do not offer to create an account
      --skip-firewall     do not offer to change ufw
      --check             only check an already running stack
  -h, --help              this text
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --client-dir) CLIENT_DIR_ARG="${2:-}"; shift ;;
    -y|--yes) YES=1 ;;
    --realm-host) REALM_HOST_ARG="${2:-}"; shift ;;
    --account) ACCOUNT_ARG="${2:-}"; shift ;;
    --bots) BOTS_ARG="${2:-}"; shift ;;
    --combat) COMBAT_ARG=1 ;;
    --project) PROJECT_ARG="${2:-}"; shift ;;
    --world-port) WORLD_PORT_ARG="${2:-}"; shift ;;
    --auth-port) AUTH_PORT_ARG="${2:-}"; shift ;;
    --sts-port) STS_PORT_ARG="${2:-}"; shift ;;
    --skip-client-check) SKIP_CLIENT_CHECK=1 ;;
    --skip-world-data) SKIP_WORLD_DATA=1 ;;
    --skip-account) SKIP_ACCOUNT=1 ;;
    --skip-firewall) SKIP_FIREWALL=1 ;;
    --check) CHECK_ONLY=1 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 1 ;;
  esac
  shift
done

if [ -t 1 ]; then B=$'\033[1m'; G=$'\033[32m'; Y=$'\033[33m'; R=$'\033[31m'; N=$'\033[0m'; else B=""; G=""; Y=""; R=""; N=""; fi
step() { echo; echo "${B}== $*${N}"; }
ok()   { echo "${G}ok${N}   $*"; }
warn() { echo "${Y}warn${N} $*"; }
die()  { echo "${R}error${N} $*" >&2; exit 1; }

# Ctrl-C always ends the script (the stack keeps running, it is not stopped).
trap 'echo; warn "Interrupted. Containers that were already started keep running (docker compose ps)."; exit 130' INT TERM

# bounded SECONDS command...   Run a command that must not be allowed to hang: it is killed after SECONDS, stdin is /dev/null
# (a command that reads the terminal from a background process group is stopped by SIGTTIN and can never be killed or
# interrupted), and --foreground keeps it in the terminal's foreground group so Ctrl-C reaches it.
bounded() {
  local secs="$1"; shift
  timeout --foreground -s KILL "$secs" "$@" </dev/null
}

# ask "question" default(y|n) -> 0 for yes
ask() {
  local q="$1" def="${2:-n}" reply
  if [ "$YES" -eq 1 ] || [ ! -t 0 ]; then [ "$def" = "y" ]; return; fi
  if [ "$def" = "y" ]; then read -rp "$q [Y/n] " reply; reply="${reply:-y}"; else read -rp "$q [y/N] " reply; reply="${reply:-n}"; fi
  [[ "$reply" =~ ^[Yy] ]]
}

rand() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "${1:-24}" || true; }

detect_ip() {
  ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "src") { print $(i + 1); exit }}' \
    || true
}

# env_get KEY -> value of KEY in .env (empty when missing)
env_get() { sed -n "s/^$1=//p" .env 2>/dev/null | tail -n 1; }

# env_set FILE KEY VALUE: replace KEY=... in FILE, or append it. VALUE must not contain newlines.
env_set() {
  local file="$1" key="$2" val="$3" tmp
  tmp="$(mktemp "$file.XXXXXX")"
  awk -v k="$key" -v v="$val" 'BEGIN { done = 0 } index($0, k "=") == 1 { if (!done) print k "=" v; done = 1; next } { print } END { if (!done) print k "=" v }' "$file" > "$tmp"
  chmod --reference="$file" "$tmp" 2>/dev/null || true
  mv "$tmp" "$file"
}

load_env() {
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
  PROJECT="${COMPOSE_PROJECT_NAME:-$DEFAULT_PROJECT}"
  WORLD_PORT="${WORLD_PORT:-24000}"
}

# docker compose ps reports health in its own format, so no "docker inspect" is needed
container_health() {
  local line state health
  line="$(bounded 20 docker compose ps -a --format '{{.State}} {{.Health}}' "$1" 2>/dev/null | head -n 1 || true)"
  [ -n "$line" ] || { echo "missing"; return; }
  state="${line%% *}"; health="${line#* }"
  [ "$health" != "$line" ] && [ -n "$health" ] && { echo "$health"; return; }
  echo "$state"
}

wait_healthy() {
  local svc="$1" timeout="${2:-600}" waited=0 status
  while [ "$waited" -lt "$timeout" ]; do
    status="$(container_health "$svc")"
    case "$status" in
      healthy) [ "$waited" -eq 0 ] || echo; return 0 ;;
      exited|dead) echo; return 1 ;;
    esac
    printf '.'
    sleep 5; waited=$((waited + 5))
  done
  echo
  return 1
}

# ---------------------------------------------------------------------------------------------------------------------
preflight() {
  step "Checking prerequisites"
  command -v docker >/dev/null 2>&1 || die "Docker is not installed. Install Docker Engine: https://docs.docker.com/engine/install/"
  command -v timeout >/dev/null 2>&1 || die "'timeout' (coreutils) is missing."
  command -v sha1sum >/dev/null 2>&1 || die "'sha1sum' (coreutils) is missing."
  local out
  # a .env problem is not a Docker problem: the configuration step below writes or fixes .env
  if ! out="$(bounded 60 docker compose ps 2>&1)" && ! echo "$out" | grep -q '\.env'; then
    echo "$out" | tail -n 5 >&2
    die "'docker compose' does not work: the compose v2 plugin is missing, the daemon is not running, or your user is not in the 'docker' group."
  fi
  ok "docker compose works$(v="$(docker compose version --short 2>/dev/null || true)"; [ -n "$v" ] && echo " (compose $v)")"
}

check_client() {
  step "WildStar client"
  if [ "$SKIP_CLIENT_CHECK" -eq 1 ]; then warn "client check skipped (--skip-client-check)"; return 0; fi
  local dir="$CLIENT_DIR_ARG" exe="" sum reply
  if [ -z "$dir" ]; then
    if [ "$YES" -eq 1 ] || [ ! -t 0 ]; then die "No client folder given. Re-run with --client-dir PATH (the folder that holds Client64/WildStar64.exe and Patch/)."; fi
    read -rp "Path to your WildStar client install (the folder with Client64/ and Patch/): " dir
  fi
  dir="${dir/#\~/$HOME}"
  [ -d "$dir" ] || die "Client folder not found: $dir"
  CLIENT_DIR="$(cd "$dir" && pwd)"
  # the launcher install has Client64/WildStar64.exe; accept the Client64 folder itself (or a flat copy) too
  for f in "$CLIENT_DIR/Client64/WildStar64.exe" "$CLIENT_DIR/WildStar64.exe"; do
    [ -f "$f" ] && { exe="$f"; break; }
  done
  [ -n "$exe" ] || die "WildStar64.exe not found under $CLIENT_DIR (looked in Client64/ and the folder itself)."
  for f in "$CLIENT_DIR/Patch" "$CLIENT_DIR/../Patch"; do
    [ -f "$f/ClientData.index" ] && { PATCH="$(cd "$f" && pwd)"; break; }
  done
  sum="$(sha1sum "$exe" | awk '{print tolower($1)}')"
  if [ "$sum" = "$KNOWN_CLIENT_SHA1" ]; then
    ok "$exe is the known-good build 16042 (sha1 $sum)"
  else
    warn "$exe has sha1 $sum, expected $KNOWN_CLIENT_SHA1 (build 16042)."
    warn "This server's game tables and packet code match build 16042 only. Another build or region may work partly or not at all."
    if [ "$YES" -eq 1 ]; then
      warn "continuing because of --yes"
    elif [ -t 0 ]; then
      read -rp "Continue with this client anyway? [y/N] " reply
      [[ "${reply:-n}" =~ ^[Yy] ]] || die "Stopped. Point --client-dir at a build 16042 client."
    else
      die "Client build mismatch. Re-run with --yes to continue anyway."
    fi
  fi
  if [ -n "$PATCH" ]; then ok "Patch folder: $PATCH"; else warn "no Patch/ folder with ClientData.index next to the client (only needed to generate data/tbl and data/map)"; fi
}

write_env() {
  step "Configuration (.env)"
  [ -f .env.example ] || die ".env.example is missing; it is part of the repository (git checkout .env.example)."
  if [ -f .env ]; then
    ok ".env already exists, keeping it"
    local missing
    missing="$(grep -oE '^[A-Z_]+=' .env.example | tr -d = | while read -r k; do grep -q "^$k=" .env || printf '%s ' "$k"; done)"
    [ -z "$missing" ] || echo "     not set in .env, using the defaults from .env.example: $missing"
    local cur; cur="$(env_get COMPOSE_PROJECT_NAME)"
    if [ -n "$PROJECT_ARG" ] && [ "$PROJECT_ARG" != "${cur:-$DEFAULT_PROJECT}" ]; then
      die "--project $PROJECT_ARG differs from COMPOSE_PROJECT_NAME=${cur:-$DEFAULT_PROJECT} in the existing .env. Edit .env or use another checkout."
    fi
    if [ -n "$PATCH" ] && [ "$(env_get PATCH_DIR)" != "$PATCH" ]; then
      env_set .env PATCH_DIR "$PATCH"; ok "PATCH_DIR set to $PATCH"
    fi
  else
    local host="${REALM_HOST_ARG:-${REALM_HOST:-}}" reply
    [ -n "$host" ] || host="$(detect_ip)"
    if [ "$YES" -ne 1 ] && [ -t 0 ]; then
      read -rp "Address game clients use to reach this machine [${host:-none}]: " reply
      host="${reply:-$host}"
    fi
    [ -n "$host" ] || die "Could not detect an address. Re-run with --realm-host <ip-or-name>."

    # optional player bots (experimental). Everything is switched by environment variables, so it can be changed later with scripts/bots.sh
    local bots="${BOTS_ARG:-0}" combat="${COMBAT_ARG:-0}" project="${PROJECT_ARG:-$DEFAULT_PROJECT}" p
    case "$bots" in ''|*[!0-9]*) die "--bots needs a number" ;; esac
    [[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || die "--project must be lowercase letters, digits, - and _"
    for p in "$WORLD_PORT_ARG" "$AUTH_PORT_ARG" "$STS_PORT_ARG"; do case "$p" in ''|[0-9]*) ;; *) die "ports must be numbers" ;; esac; done
    if [ -z "$BOTS_ARG" ] && [ "$YES" -ne 1 ] && [ -t 0 ]; then
      echo
      echo "Player bots are experimental: logged-in characters that wander, fight and take quests so the world feels populated."
      echo "You can turn them on, off or resize them any time later with scripts/bots.sh, without rebuilding."
      if ask "Enable player bots now?" n; then
        read -rp "How many bots to keep online (each is a full player, start small) [50]: " reply
        bots="${reply:-50}"
        case "$bots" in ''|*[!0-9]*) die "That is not a number." ;; esac
        if ask "Also enable combat, death and respawn (bots need it to fight and quest; it changes how the whole world behaves)?" y; then combat=1; fi
      fi
    fi
    [ "$bots" -gt 0 ] && combat=1
    local tmp; tmp="$(umask 077; mktemp .env.XXXXXX)"
    { echo "# Generated by install.sh from .env.example on $(date +%F). Passwords are alphanumeric on purpose (connection string)."
      echo "# Changing DB_PASSWORD or DB_ROOT_PASSWORD later does NOT change the existing database, see README \"Resetting\"."
      cat .env.example; } > "$tmp"
    env_set "$tmp" COMPOSE_PROJECT_NAME "$project"
    env_set "$tmp" IMAGE_PREFIX "$project"
    env_set "$tmp" DB_ROOT_PASSWORD "$(rand 24)"
    env_set "$tmp" DB_PASSWORD "$(rand 24)"
    env_set "$tmp" REALM_HOST "$host"
    env_set "$tmp" BOTS_TARGET "$bots"
    env_set "$tmp" COMBAT "$combat"
    [ -z "$PATCH" ] || env_set "$tmp" PATCH_DIR "$PATCH"
    [ -z "$WORLD_PORT_ARG" ] || env_set "$tmp" WORLD_PORT "$WORLD_PORT_ARG"
    [ -z "$AUTH_PORT_ARG" ] || env_set "$tmp" AUTH_PORT "$AUTH_PORT_ARG"
    [ -z "$STS_PORT_ARG" ] || env_set "$tmp" STS_PORT "$STS_PORT_ARG"
    chmod 600 "$tmp"
    mv "$tmp" .env
    ok "wrote .env (project $project, realm address $host, random database passwords, mode 600)"
  fi
  # docker run through a wrapper or sudo as another user cannot read a mode 600 .env
  local psout; psout="$(bounded 60 docker compose ps 2>&1 || true)"
  if echo "$psout" | grep -q '\.env: permission denied'; then
    chmod 644 .env
    warn "docker compose runs as another user here and could not read .env; made it readable (644). It holds the database passwords."
  fi
  load_env
  [ -n "${REALM_HOST:-}" ] || die "REALM_HOST is empty in .env"
}

client_data_status() {
  local tbl f
  TBL_OK=1; MAP_OK=1
  tbl="$(find data/tbl -maxdepth 1 -name '*.tbl' 2>/dev/null | wc -l)"
  { [ "$tbl" -ge 100 ] && [ -f data/tbl/en-US.bin ]; } || TBL_OK=0
  for f in Western Eastern NewCentral; do [ -f "data/map/$f.nfmap" ] || MAP_OK=0; done
  TBL_COUNT="$tbl"; MAP_COUNT="$(find data/map -maxdepth 1 -name '*.nfmap' 2>/dev/null | wc -l)"
}

# Runs the bundled NexusForever.MapGenerator in the one-shot "mapgen" service against PATCH_DIR (read-only) and moves
# the output from data/map-gen/work into data/tbl and data/map.
generate_client_data() {
  local args=""
  [ "$TBL_OK" -eq 1 ] || args="-e"
  [ "$MAP_OK" -eq 1 ] || args="$args -g"
  echo "Running the map generator ($args) on $PATCH. Tables take a few minutes, maps considerably longer."
  rm -rf data/map-gen/work/tbl data/map-gen/work/map 2>/dev/null || warn "could not clear old output in data/map-gen/work (owned by root from an older run?)"
  docker compose run --rm -T -e MAPGEN_ARGS="$args" mapgen </dev/null || die "the map generator failed (see the output above)"
  mkdir -p data/tbl data/map
  if [ "$TBL_OK" -eq 0 ]; then
    [ -d data/map-gen/work/tbl ] || die "the map generator wrote no tables (data/map-gen/work/tbl)"
    cp -a data/map-gen/work/tbl/. data/tbl/ && rm -rf data/map-gen/work/tbl
  fi
  if [ "$MAP_OK" -eq 0 ]; then
    [ -d data/map-gen/work/map ] || die "the map generator wrote no maps (data/map-gen/work/map)"
    cp -a data/map-gen/work/map/. data/map/ && rm -rf data/map-gen/work/map
  fi
}

check_client_data() {
  step "Client data (data/tbl, data/map)"
  client_data_status
  if [ "$TBL_OK" -eq 0 ] || [ "$MAP_OK" -eq 0 ]; then
    [ "$TBL_OK" -eq 1 ] || warn "data/tbl is incomplete ($TBL_COUNT tables, en-US.bin $( [ -f data/tbl/en-US.bin ] && echo present || echo missing))"
    [ "$MAP_OK" -eq 1 ] || warn "data/map lacks Western/Eastern/NewCentral.nfmap ($MAP_COUNT maps)"
    [ -n "$PATCH" ] || die "Cannot generate them without the client's Patch folder. Re-run with --client-dir, or copy tbl/ and map/ into data/ (README, Client data)."
    ask "Generate the missing data from $PATCH now?" y || die "Stopped. See README, Client data."
    generate_client_data
    client_data_status
    [ "$TBL_OK" -eq 1 ] && [ "$MAP_OK" -eq 1 ] || die "data/tbl or data/map is still incomplete after generation"
  fi
  ok "$TBL_COUNT tables, $MAP_COUNT maps"
}

start_stack() {
  step "Building (the first build downloads .NET images and packages, this takes a few minutes)"
  docker compose build || die "docker compose build failed. Fix the error above and re-run ./install.sh (it is safe to re-run)."
  step "Starting"
  if ! docker compose up -d; then
    warn "docker compose up reported a failure. Last world log lines:"
    docker compose logs --tail=40 world || true
    die "Stack did not start. Fix the error above and re-run ./install.sh (it is safe to re-run)."
  fi
  echo "waiting for the world server (loading tables and maps takes a few minutes)"
  wait_healthy world 900 || { docker compose logs --tail=60 world || true; die "world did not become healthy"; }
  ok "world is healthy"
  local svc state
  for svc in db auth sts; do
    state="$(container_health "$svc")"
    case "$state" in healthy|running) ;; *) die "$svc is not running (state: $state). See: docker compose logs $svc" ;; esac
  done
  ok "db, auth and sts are running"
}

realm_reachable() {
  # </dev/null and --foreground matter: plain `timeout` moves the command into a background process group, so when
  # docker reads the terminal it is stopped by SIGTTIN and neither the timeout nor Ctrl-C can end it (the script hangs).
  bounded 8 docker compose exec -T auth bash -c "exec 3<>/dev/tcp/${REALM_HOST}/${WORLD_PORT}" >/dev/null 2>&1
}

check_realm_reachable() {
  step "Checking that the auth server can reach the world server at ${REALM_HOST}:${WORLD_PORT}"
  if realm_reachable; then ok "reachable"; return 0; fi

  warn "The auth container cannot connect to ${REALM_HOST}:${WORLD_PORT}. Clients would see \"No realms are available\"."
  warn "Usual cause: a host firewall (ufw) dropping container -> host traffic. Docker-published ports bypass ufw for outside"
  warn "clients, but not for containers talking to the host's own address."

  local sudo_cmd="" status subnet
  [ "$(id -u)" -eq 0 ] || sudo_cmd="sudo"
  subnet="$(bounded 20 docker network inspect "${PROJECT}_default" -f '{{range .IPAM.Config}}{{.Subnet}}{{end}}' 2>/dev/null || true)"
  [[ "$subnet" =~ ^[0-9./]+$ ]] || subnet="172.16.0.0/12"
  if [ "$SKIP_FIREWALL" -eq 0 ] && command -v ufw >/dev/null 2>&1; then
    if [ "$YES" -eq 1 ] || [ ! -t 0 ]; then
      warn "If you use ufw, allow the stack's containers to reach the world port:"
      warn "  sudo ufw allow from $subnet to any port ${WORLD_PORT} proto tcp"
    else
      status=""
      if [ -z "$sudo_cmd" ] || sudo -n true 2>/dev/null; then
        status="$(${sudo_cmd:+$sudo_cmd -n} ufw status 2>/dev/null | head -1 || true)"
      else
        warn "sudo needs a password to inspect ufw, so this step is skipped. If clients cannot log in, run:"
        warn "  sudo ufw allow from $subnet to any port ${WORLD_PORT} proto tcp"
      fi
      if echo "$status" | grep -qi "status: active"; then
        if ask "ufw is active. Add 'ufw allow from $subnet to any port ${WORLD_PORT} proto tcp' (this stack's containers only)?" y; then
          $sudo_cmd ufw allow from "$subnet" to any port "${WORLD_PORT}" proto tcp comment 'nexusforever containers to world' >/dev/null
          sleep 2
          if realm_reachable; then ok "reachable now"; return 0; fi
          warn "still not reachable after adding the ufw rule"
        fi
      fi
    fi
  fi
  warn "Fix the firewall or address, then run: ./install.sh --check   (see README, Troubleshooting)"
  return 1
}

world_data_loaded() {
  local n
  n="$(bounded 30 docker compose exec -T -e MYSQL_PWD="$DB_PASSWORD" db mariadb -u"$DB_USER" -N -e 'SELECT COUNT(*) FROM nexus_forever_world.entity' 2>/dev/null | tr -d '[:space:]' || true)"
  [ -n "$n" ] && [ "$n" -gt 0 ] 2>/dev/null
}

load_world_data() {
  [ "$SKIP_WORLD_DATA" -eq 0 ] || return 0
  step "World data (creature spawns)"
  if world_data_loaded; then ok "already loaded, skipping"; return 0; fi
  command -v git >/dev/null 2>&1 || { warn "git is not installed, skipping. Install git and run scripts/load-world-data.sh"; return 0; }
  if ask "Load the creature/spawn data (upstream NexusForever.WorldDatabase, ~4 MB download, plus this repo's data/spawns)? Takes a few minutes." y; then
    scripts/load-world-data.sh </dev/null
    scripts/load-instance-data.sh </dev/null || warn "instance spawn data was not loaded (see above); later: scripts/load-instance-data.sh"
    if compgen -G "data/spawns/*.tsv" >/dev/null; then
      scripts/import-research-spawns.sh </dev/null || warn "research spawns were not imported; later: scripts/import-research-spawns.sh"
    fi
    docker compose restart world >/dev/null
    wait_healthy world 900 || die "world did not come back healthy after loading world data"
    ok "world data loaded, world restarted"
  else
    echo "Skipped. Later: scripts/load-world-data.sh; scripts/load-instance-data.sh; scripts/import-research-spawns.sh; docker compose restart world"
  fi
}

create_account() {
  [ "$SKIP_ACCOUNT" -eq 0 ] || return 0
  step "Game account"
  local email="$ACCOUNT_ARG"
  if [ -z "$email" ]; then
    if [ "$YES" -eq 1 ] || [ ! -t 0 ]; then echo "No --account given, skipping. Later: scripts/create-account.sh you@example.com"; return 0; fi
    if ! ask "Create a game account now?" y; then echo "Skipped. Later: scripts/create-account.sh you@example.com"; return 0; fi
    read -rp "Email (used as the login name): " email
  fi
  [ -n "$email" ] || return 0
  scripts/create-account.sh "$email" || warn "account was not created (see message above)"
}

summary() {
  step "Done"
  cat <<EOF
The server is running (compose project ${PROJECT}).

Connect
  Point your WildStar client at ${REALM_HOST} (README.md, "Connecting the game client": hosts file or launch arguments).
  Ports: ${STS_PORT:-6600} STS, ${AUTH_PORT:-23115} auth, ${WORLD_PORT} world. Accounts: scripts/create-account.sh you@example.com

Logs and console
  docker compose logs -f world          (also auth, sts, db)
  docker compose ps                     container state and health
  docker attach --sig-proxy=false --detach-keys=ctrl-x ${PROJECT}-world-1   server console, Ctrl-X detaches (never Ctrl-C)

Bots
  scripts/bots.sh status                enable, disable, resize; no rebuild needed

Stop, start, remove
  docker compose stop / docker compose start
  ./uninstall.sh                        removes the containers (and, after asking, the database volumes)
EOF
}

# ---------------------------------------------------------------------------------------------------------------------
preflight
if [ "$CHECK_ONLY" -eq 1 ]; then
  [ -f .env ] || die "No .env found. Run ./install.sh first."
  load_env
  step "Container status"
  docker compose ps
  check_realm_reachable
  exit 0
fi

check_client
write_env
check_client_data
start_stack
check_realm_reachable || true
load_world_data
create_account
summary
