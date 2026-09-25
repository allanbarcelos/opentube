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
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Development}"
