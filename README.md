# NexusForever Bots

A WildStar server emulator (a fork of [NexusForever](https://github.com/NexusForever/NexusForever), client build 16042, .NET 5) packaged for Docker, with an automated bot QA harness built into the world server. Bots are ordinary characters on socketless connections that go through the same message handlers as a game client. They quest, fight, craft, run public events and clear dungeons and raids in groups, and everything they cannot do is counted in a failure ledger. The longer-term aim is bots that can be played with as companions.

## State

It is functional, not finished. Questing, combat, crafting, dungeons and raids, the economy systems and character persistence work as far as bots can exercise them. The reference run is 60 bots for 36 hours ([CHANGELOG.md](CHANGELOG.md) has the numbers). No game client has been used to verify the server-side systems added here. Everything was exercised by bots only, so anything that depends on real client behaviour (groups, companions, UI flows) is unverified. The prioritized list of gaps is in [CHANGELOG.md](CHANGELOG.md#known-limitations). The per-system status is in [docs/SYSTEMS.md](docs/SYSTEMS.md).

The repository contains no game files. You need your own installed WildStar client (build 16042); the installer extracts the game tables and terrain from it on your machine.

## Quick start

```bash
git clone https://github.com/voidwatch/NexusForeverBots.git
cd NexusForeverBots
./install.sh --client-dir /path/to/WildStar      # the folder with Client64/WildStar64.exe and Patch/
```

Docker Engine with the compose v2 plugin is the only host requirement. `install.sh` validates the client, writes `.env`, generates `data/tbl` and `data/map`, builds and starts the stack, checks the firewall, loads spawns and creates your first account. [docs/QUICKSTART.md](docs/QUICKSTART.md) covers every step, the options, connecting the client and troubleshooting.

| service | port | role |
|---|---|---|
| `db` | not published | MariaDB 10.11: auth, character and world databases |
| `world` | 24000 | the game world; applies the database schema on start and registers the realm |
| `auth` | 23115 | account check, hands the client the realm address |
| `sts` | 6600 | login handshake |

## Client data

`data/tbl` (game tables) and `data/map` (terrain) are generated from your client's `Patch` folder by `install.sh --client-dir` (the bundled map generator, `docker compose run --rm mapgen`), or by hand on Windows. Both are git-ignored and never distributed. Details: [docs/QUICKSTART.md#client-data](docs/QUICKSTART.md#client-data); what else is derived from the client and what comes from community research: [docs/DATA-PROVENANCE.md](docs/DATA-PROVENANCE.md).

## Connecting the game client

Point NCSOFT's login hostnames at your server in the Windows hosts file (`SERVER cligate.ncsoft.com`, `SERVER auth.eu.wildstar-online.com`, plus any host the client's error message lists), then log in with an account from `scripts/create-account.sh`. Step by step, and the launch-argument alternative: [docs/QUICKSTART.md#2-connect-the-game-client](docs/QUICKSTART.md#2-connect-the-game-client).

## Configuration

`.env` (written by `install.sh` from `.env.example`) holds every setting: database passwords, realm address, bot counts and roles, combat switches, memory limit, regeneration rates. Each key is explained in [docs/CONFIGURATION.md](docs/CONFIGURATION.md). A variable exported in your shell overrides `.env`; changes take effect with `docker compose up -d world`.

## Troubleshooting

`./install.sh --check` tests a running stack, including whether the auth container can reach the world port. The usual problems ("No realms are available" from a host firewall, the client hash check, install errors) are in [docs/QUICKSTART.md#troubleshooting](docs/QUICKSTART.md#troubleshooting); runtime symptoms are in [docs/OPERATIONS.md](docs/OPERATIONS.md#common-failures-and-fixes).

## Resetting

`docker compose down -v` deletes the database and cache volumes (all accounts and characters); `data/` and `.env` stay. Do this if you changed `DB_PASSWORD` or `DB_ROOT_PASSWORD` after the first start: MariaDB reads them only when it creates a new database volume. Then run `./install.sh` again to reload spawns and create an account. `./uninstall.sh` removes the stack and asks before deleting volumes, images and `.env`. Take a backup first ([docs/OPERATIONS.md](docs/OPERATIONS.md#database-backup-and-restore)).

## Bots

```bash
scripts/bots.sh enable 30       # 30 bots, combat on
scripts/bots.sh status
scripts/bot-feed.sh -f          # live feed of what they do
scripts/report.sh failures      # the failure ledger
scripts/bots.sh disable
```

Roles (quest, clean verification, dungeon and raid teams, events, paths, system probes, companions) and how to read their numbers: [docs/BOTS.md](docs/BOTS.md). The test loop (10-minute gate, soaks, clean-bot quest verification): [docs/TESTING.md](docs/TESTING.md).

## Documentation

| file | contents |
|---|---|
| [docs/QUICKSTART.md](docs/QUICKSTART.md) | install, connect, troubleshoot |
| [docs/CONFIGURATION.md](docs/CONFIGURATION.md) | every `.env` key and run recipe |
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | running it long-term: start/stop, backups, logs, reports, memory diagnostics, disk |
| [CHANGELOG.md](CHANGELOG.md) | what works, measured numbers, known limitations |
| [docs/SYSTEMS.md](docs/SYSTEMS.md) | status of every game system and the bot driver that tests it |
| [docs/BOTS.md](docs/BOTS.md), [docs/BOT-FEED.md](docs/BOT-FEED.md) | what bots do; the event feed |
| [docs/TESTING.md](docs/TESTING.md) | gate, soaks, clean-bot verification, focused checks |
| [docs/QUEST-COVERAGE.md](docs/QUEST-COVERAGE.md) | which quests are open and why |
| [docs/INSTANCES.md](docs/INSTANCES.md), [docs/DUNGEONS.md](docs/DUNGEONS.md) | dungeon and raid runs by bot teams |
| [docs/COMBAT-MATH.md](docs/COMBAT-MATH.md) | damage, healing, mitigation, stats pipeline |
| [docs/PERFORMANCE.md](docs/PERFORMANCE.md) | CPU and memory measurements, the out-of-memory analysis |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | code layout and how to extend it |
| [docs/PROTOCOL.md](docs/PROTOCOL.md), [docs/OPCODE-COVERAGE.md](docs/OPCODE-COVERAGE.md) | client messages: what is known and handled |
| [docs/ASSUMPTIONS.md](docs/ASSUMPTIONS.md) | every decoded fact and every guess, with confidence and source |
| [docs/DATA-PROVENANCE.md](docs/DATA-PROVENANCE.md), [docs/SOURCES.md](docs/SOURCES.md) | where the data comes from |

## Security

Only ports 6600, 23115 and 24000 are published; the database is not. Docker's port rules bypass ufw/firewalld, so restrict access with `BIND_IP` or the `DOCKER-USER` chain. It is a hobby emulator on an end-of-life runtime with no login rate limiting: keep it on a LAN or a VPN. `.env` holds the database passwords.

## License

[GNU AGPL v3.0](LICENSE). If you run a modified version as a network service, the AGPL requires you to offer its source to your users. The emulator is the work of the NexusForever project and its contributors; community spawn data comes from [NexusForever.WorldDatabase](https://github.com/NexusForever/NexusForever.WorldDatabase).

WildStar, NCSOFT and Carbine are trademarks of NCSOFT Corporation. This project is not affiliated with or endorsed by them and contains none of their assets.

---

Maintained by [voidwatch](https://github.com/voidwatch).
