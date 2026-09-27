#!/bin/sh
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

# Wait for the database and storage before handing off the process. Swarm has
# no depends_on; without this wait the app exits and restarts until the
# network publishes the name.
echo "[opentube] waiting for postgres:5432" >&2
waited=0
until nc -z postgres 5432 2>/dev/null; do
  waited=$((waited + 2))
  if [ "$waited" -ge 90 ]; then
    echo "[opentube] postgres:5432 unreachable" >&2
    exit 1
  fi
  sleep 2
done

echo "[opentube] waiting for minio:9000" >&2
waited=0
until nc -z minio 9000 2>/dev/null; do
  waited=$((waited + 2))
  if [ "$waited" -ge 90 ]; then
    echo "[opentube] minio:9000 unreachable" >&2
    exit 1
  fi
  sleep 2
done

exec "$@"
