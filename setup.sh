#!/usr/bin/env bash
# setup.sh is now install.sh (same options plus --client-dir). Kept so old instructions keep working: an existing setup
# with complete data/tbl and data/map does not need the client, so the client check is skipped unless --client-dir is given.
cd "$(dirname "${BASH_SOURCE[0]}")" || exit 1
case " $* " in *" --client-dir "*) exec ./install.sh "$@" ;; esac
exec ./install.sh --skip-client-check "$@"
