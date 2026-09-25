#!/bin/sh
# Espera o banco e o storage antes de entregar o processo. O Swarm não tem
# depends_on; sem esta espera a aplicação cai e reinicia até a rede publicar o nome.
echo "[opentube] à espera de postgres:5432" >&2
espera=0
until nc -z postgres 5432 2>/dev/null; do
  espera=$((espera + 2))
  if [ "$espera" -ge 90 ]; then
    echo "[opentube] postgres:5432 inacessível" >&2
    exit 1
  fi
  sleep 2
done

echo "[opentube] à espera de minio:9000" >&2
espera=0
until nc -z minio 9000 2>/dev/null; do
  espera=$((espera + 2))
  if [ "$espera" -ge 90 ]; then
    echo "[opentube] minio:9000 inacessível" >&2
    exit 1
  fi
  sleep 2
done

exec "$@"
