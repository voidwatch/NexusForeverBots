#!/bin/bash
# Runs the Jabbithole scrapers one after the other (one connection, one request per second overall).
cd "$(dirname "$0")/../.."
python3 scripts/research/jabbithole_pages.py zones
python3 scripts/research/jabbithole_pages.py public-events
python3 scripts/research/jabbithole_fetch.py --all
