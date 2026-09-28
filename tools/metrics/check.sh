#!/bin/sh
# Lints, type-checks and tests the report. Runs in the metrics image with the
# sources mounted read-only at /app; caches go to /tmp.
set -eu
cd /app
ruff check --no-cache .
mypy --cache-dir /tmp/mypy report.py tests
python -m pytest -p no:cacheprovider tests "$@"
