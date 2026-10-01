# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

# Load .env and export the variables the application reads on the host.
# Usage: source scripts/dev-env.sh   (from the repository root)
if [[ ! -f .env ]]; then
  echo "Missing .env. Run make." >&2
  return 1 2>/dev/null || exit 1
fi

set -a
# shellcheck disable=SC1091
. ./.env
set +a

export ConnectionStrings__Default="Host=localhost;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
export OPENTUBE_DB="${ConnectionStrings__Default}"
export Storage__Endpoint="http://localhost:9000"
export Storage__AccessKey="${MINIO_ROOT_USER}"
export Storage__SecretKey="${MINIO_ROOT_PASSWORD}"
export Security__TokenPepper="${TOKEN_PEPPER}"
export Security__IpHashPepper="${IP_HASH_PEPPER}"
export Security__PublicUrl="http://localhost:5080"
# O administrador do desenvolvimento vem do .env de cada máquina, e não de um arquivo do
# repositório: o appsettings.Development.json vai dentro da imagem publicada.
export Security__AdminEmails__0="${OPENTUBE_ADMIN_EMAIL:-admin@localhost}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Development}"

# Transcrição automática de legendas: ligada quando o whisper-cli e o modelo baixado por
# 'make whisper' existem. Uma configuração já definida no ambiente prevalece.
if [[ -z "${Transcription__Executable:-}" ]] && command -v whisper-cli >/dev/null 2>&1 && [[ -f .whisper/modelo.bin ]]; then
  export Transcription__Executable="$(command -v whisper-cli)"
  export Transcription__ModelPath="$PWD/.whisper/modelo.bin"
fi
