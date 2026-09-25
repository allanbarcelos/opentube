#!/usr/bin/env bash
# Gera .env com usuário, senha e chaves aleatórios. Se o arquivo já existe, não mexe:
# o PostgreSQL só aceita a senha da primeira inicialização do volume.
set -euo pipefail

raiz="$(cd "$(dirname "$0")/.." && pwd)"
destino="${raiz}/.env"

if [[ -f "$destino" ]]; then
  echo "  .env já existe — credenciais mantidas."
  exit 0
fi

command -v openssl >/dev/null 2>&1 || { echo "openssl é necessário para gerar as credenciais." >&2; exit 1; }

segredo() {
  openssl rand -base64 64 | tr -d '/+=\n' | head -c "$1"
}

umask 077
cat > "$destino" <<EOF
# Gerado por scripts/gen-env.sh. Não versionar.
POSTGRES_DB=db_$(openssl rand -hex 5)
POSTGRES_USER=user_$(openssl rand -hex 4)
POSTGRES_PASSWORD=$(segredo 32)
MINIO_ROOT_USER=minio_$(openssl rand -hex 4)
MINIO_ROOT_PASSWORD=$(segredo 32)
TOKEN_PEPPER=$(segredo 48)
IP_HASH_PEPPER=$(segredo 48)
OPENTUBE_HOST=localhost
OPENTUBE_ADMIN_EMAIL=${OPENTUBE_ADMIN_EMAIL:-admin@localhost}
EOF
chmod 600 "$destino"

echo "  .env criado com usuário, senha e chaves aleatórios."
echo "  O arquivo fica só nesta máquina (modo 600). Não é reimpresso."
