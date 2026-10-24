# Quick start

From nothing to a running server you can log in to. Everything runs in Docker; the host needs no .NET.

## Requirements

- A Linux host (tested on Debian/Ubuntu-family). The world container is limited to `WORLD_MEM_LIMIT` (default 14g); with 30-60 bots it used 2.5-4.5 GiB in the measured runs ([PERFORMANCE.md](PERFORMANCE.md)). A few GB of disk for images, plus the client data.
- Docker Engine 24+ with the `docker compose` v2 plugin. Plain `docker run` is not needed; every step uses `docker compose`. Podman is untested.
- `git`, `bash`, `timeout` and `sha1sum` (coreutils), and internet access for Docker images, NuGet packages and the upstream world data on GitHub.
- Your own WildStar client at build 16042, installed (the folder with `Client64/WildStar64.exe` and `Patch/`). It is never distributed with this repository. See [Client data](#client-data).

## 1. Clone and install

```bash
git clone https://github.com/voidwatch/NexusForeverBots.git
cd NexusForeverBots
./install.sh --client-dir /path/to/WildStar
```

`install.sh` is safe to re-run. In order it:

1. checks Docker, Compose, `timeout` and `sha1sum`;
2. validates the client: `Client64/WildStar64.exe` (or `WildStar64.exe` directly in the folder) must exist and should have SHA-1 `a5974e9628897fc1965b7a740bce68df5b621175` (build 16042); see [Hash mismatch](#client-hash-mismatch);
3. writes `.env` from `.env.example` with random database passwords (mode 600), the address clients will use (your detected LAN IP) and `PATCH_DIR`. An existing `.env` is kept; keys it lacks are listed;
4. generates `data/tbl` (game tables, about 385 files plus `en-US.bin`) and `data/map` (`.nfmap` terrain) from the client's `Patch` folder with the bundled map generator (`docker compose run --rm mapgen`) when they are missing;
5. runs `docker compose build` and `docker compose up -d` and waits until the world server is healthy (the first build downloads images and packages and takes several minutes; the world needs up to about 3 minutes to load tables and maps);
6. checks that the auth container can reach the world server at the address it advertises ([Firewall](#no-realms-are-available-firewall));
7. offers to load creature spawns: the upstream open-world and instance data, then this repository's researched spawns (`data/spawns`);
8. offers to create your first game account (the password is prompted, never put on a command line).

Options (`./install.sh --help` prints them all):

| option | effect |
|---|---|
| `--yes` | no prompts, safe defaults; a client hash mismatch is accepted with a warning |
| `--realm-host HOST` | IP or DNS name clients use to reach this machine (default: detected LAN IP) |
| `--account EMAIL` | create this account (password from the prompt or `NF_ACCOUNT_PASSWORD`) |
| `--bots N` | keep N bots online (0, the default, is no bots); `--combat` turns on combat |
| `--project NAME`, `--world-port`, `--auth-port`, `--sts-port` | a second, fully separate stack (own containers, database, images) next to an existing one. Clients only connect to the stack on port 24000 |
| `--skip-client-check`, `--skip-world-data`, `--skip-account`, `--skip-firewall` | skip a step |
| `--check` | only check a running stack (container state plus the auth-to-world reachability test) |

Unattended example:

```bash
NF_ACCOUNT_PASSWORD='...' ./install.sh --client-dir /path/to/WildStar --yes --realm-host 192.168.1.10 --account you@example.com
```

`./setup.sh` is the old name; it runs `install.sh` without the client check, for a checkout whose `data/` is already complete.

To remove the stack: `./uninstall.sh` stops and removes this checkout's containers and network, then asks before deleting the database and cache volumes (all accounts and characters), the images and `.env` (`--yes [--volumes] [--images] [--env]` for unattended use). It never touches the client folder, `data/` or anything outside the checkout.

## Client data

The server needs two folders generated from the client:

| folder | contents | used for |
|---|---|---|
| `data/tbl/` | about 385 game tables (`*.tbl`) and the text table `en-US.bin` (`fr-FR.bin`, `de-DE.bin` optional) | all game rules: items, spells, creatures, quests |
| `data/map/` | `*.nfmap` terrain files, one per world; at least `Western`, `Eastern` and `NewCentral` | terrain and collision |

`install.sh --client-dir` generates both. Without Docker access to the client you can generate them on the Windows PC that has the client, with the .NET 5 SDK and `Source/NexusForever.MapGenerator` from this repository:

```powershell
cd NexusForeverBots\Source\NexusForever.MapGenerator
dotnet run -c Release -- -i "C:\Program Files (x86)\NCSOFT\WildStar\Patch" -e -g
```

`-e` extracts tables and text, `-g` generates maps. Copy the resulting `tbl` and `map` folders so the files sit directly in `data/tbl/` and `data/map/` (not `data/tbl/tbl/`), then run `./install.sh --skip-client-check`. Instance worlds get extra floor-height files from `scripts/gen-floors.sh` ([DATA-PROVENANCE.md](DATA-PROVENANCE.md)).

Both folders are git-ignored. Getting a client is your responsibility: the game was shut down in 2018 and the client is NCSOFT's software.

## 2. Connect the game client

On the Windows PC with the client, point NCSOFT's login hostnames at your server (replace `SERVER` with `REALM_HOST` from `.env`). The client already uses the emulator's default ports (6600, 23115, 24000).

1. Open the hosts file as administrator: `Start-Process notepad "C:\Windows\System32\drivers\etc\hosts" -Verb RunAs`
2. Add and save:
   ```
   SERVER cligate.ncsoft.com
   SERVER auth.eu.wildstar-online.com
   ```
   If the client says "Cannot connect to NCSoft login services -- <host> -- <host>", add every host it lists.
3. `ipconfig /flushdns`, start the game (launcher PLAY, or `Client64\WildStar64.exe`) and log in with the account you created.

Remove the lines to undo it. Launch arguments (`WildStar64.exe /auth SERVER /authNc SERVER /lang en /patcher SERVER /SettingsKey WildStar /realmDataCenterId 9`, also wrapped by `Source/NexusForever.ClientConnector`) are the upstream method; on the retail-launcher install tested here the client ignored them, so use the hosts file.

More accounts: `scripts/create-account.sh you@example.com`. Emails are stored lower-case; there is no self-service signup.

## 3. Optional: bots

```bash
scripts/bots.sh enable 30      # 30 bots with combat on
scripts/bots.sh status
scripts/bots.sh disable        # log them out; their characters stay
```

Each call edits `.env` and recreates the world container (connected players are dropped for a moment). Roles, clean-bot mode and dungeon teams: [BOTS.md](BOTS.md) and [CONFIGURATION.md](CONFIGURATION.md).

## Troubleshooting

Start with `./install.sh --check` and `docker compose logs --tail=80 world`. More symptoms: [OPERATIONS.md](OPERATIONS.md#common-failures-and-fixes).

### "No realms are available" (firewall)

Login works but the realm list is empty: the auth server checks the world server by opening `REALM_HOST:24000` from inside its container. A host firewall that drops container-to-host traffic (ufw is the usual one) makes the world look offline. `install.sh` tests this and prints the fix for your Docker subnet:

```bash
sudo ufw allow from <docker subnet> to any port 24000 proto tcp
```

If sudo needs a password, `install.sh` skips the change and prints the command. If `REALM_HOST` is a DNS name it must resolve to the host from inside the container. Right after a restart the realm can take about 15 seconds to show. With `--project` and another `--world-port`, the check reports that port, but clients only connect to 24000.

Docker publishes ports with its own firewall rules ahead of ufw/firewalld, so `ufw deny` does not protect ports 6600, 23115 and 24000 from outside traffic. Restrict them with `BIND_IP` in `.env` or the `DOCKER-USER` iptables chain.

### Client hash mismatch

`WildStar64.exe has sha1 ..., expected a5974e96... (build 16042)`: the tables and packet code here match build 16042 only. Interactively, `install.sh` asks whether to continue (default no). With `--yes` it continues with a warning. Without `--yes` and without a terminal it stops with "Client build mismatch. Re-run with --yes to continue anyway."

### Other install.sh errors

| message | fix |
|---|---|
| `'docker compose' does not work` | Install the compose v2 plugin, start the Docker daemon, or add your user to the `docker` group (log out and in). |
| `No client folder given` | Unattended runs need `--client-dir PATH`, or `--skip-client-check` when `data/` is already complete. |
| `WildStar64.exe not found under ...` | Point `--client-dir` at the install root (it looks in `Client64/` and the folder itself). |
| `Cannot generate them without the client's Patch folder` | `data/tbl` or `data/map` is incomplete and no `Patch/` with `ClientData.index` was found. Fix `--client-dir`, or copy `tbl/` and `map/` into `data/` yourself. |
| `could not clear old output in data/map-gen/work` | Files from an older generator run are owned by root; remove `data/map-gen/work` with sudo. |
| `--project ... differs from COMPOSE_PROJECT_NAME` | The existing `.env` belongs to another project. Edit `.env` or use another checkout. |
| `world did not become healthy` / `dependency failed to start` | The world crashed on start; the last exception in the printed log says why (often a missing map file or wrong database password). |
| `made it readable (644)` warning | Compose runs as another user on this host and could not read `.env`; it holds the database passwords, so keep the checkout private. |

### After install

| symptom | fix |
|---|---|
| "Cannot connect to NCSoft login services" | The hosts file redirect is missing or incomplete; add every host the message lists, as administrator. |
| Wrong-credentials error | No such account or wrong password: `scripts/create-account.sh`. |
| `Realm id 1 doesn't exist in the database` | `REALM_HOST` is empty. Set it in `.env`, then `docker compose up -d`. |
| `Access denied` for the database user | A database password in `.env` was changed after the first start. MariaDB reads it only when the volume is created; see [Resetting](../README.md#resetting). |
| Empty world, no creatures | Spawns not loaded: `scripts/load-world-data.sh && scripts/load-instance-data.sh && scripts/import-research-spawns.sh && docker compose restart world`. |
| A `.env` change has no effect | A shell variable with the same name overrides `.env`: `unset $(grep -E '^[A-Za-z_]+=' .env \| cut -d= -f1)`, then `docker compose up -d world`. |
