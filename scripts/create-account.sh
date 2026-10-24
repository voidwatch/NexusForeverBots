#!/usr/bin/env bash
# Create a game account without attaching to the server console.
#
#   scripts/create-account.sh you@example.com            # prompts for the password
#   NF_ACCOUNT_PASSWORD=secret scripts/create-account.sh you@example.com
#
# Starts a throw-away world container that writes the account to the auth database and exits (the running server is not
# touched). The password is passed through the environment of that one container only, never on a command line.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

email="${1:-}"
[ -n "$email" ] || { echo "usage: $0 <email> (password via prompt or NF_ACCOUNT_PASSWORD)" >&2; exit 1; }
[ -f .env ] || { echo "No .env found. Run ./install.sh first." >&2; exit 1; }

password="${NF_ACCOUNT_PASSWORD:-}"
if [ -z "$password" ]; then
  read -rsp "Password for $email: " password; echo
  read -rsp "Repeat password: " password2; echo
  [ "$password" = "$password2" ] || { echo "Passwords do not match." >&2; exit 1; }
fi
[ -n "$password" ] || { echo "Empty password." >&2; exit 1; }

export NF_CREATE_ACCOUNT_EMAIL="$email"
export NF_CREATE_ACCOUNT_PASSWORD="$password"
set +e
docker compose run --rm -T -e NF_CREATE_ACCOUNT_EMAIL -e NF_CREATE_ACCOUNT_PASSWORD world </dev/null
rc=$?
set -e
unset NF_CREATE_ACCOUNT_EMAIL NF_CREATE_ACCOUNT_PASSWORD

case "$rc" in
  0) echo "Account created: $email" ;;
  2) echo "An account with that email already exists." >&2; exit 2 ;;
  *) echo "Account creation failed (exit code $rc). Is the database up? Try: docker compose logs db" >&2; exit "$rc" ;;
esac
