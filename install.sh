#!/usr/bin/env bash
# ==============================================================================
#  install.sh — instalador de produção do OpenTube (Docker Swarm, um nó)
#
#  Uso: sudo bash install.sh
#
#  O script segue o mesmo desenho do instalador do Archeo, no que esta
#  aplicação precisa:
#    1. Configuração interativa (nome, email do administrador, acesso, SMTP, disco)
#    2. Pacotes (docker, openssl, ufw)
#    3. Swarm de um nó
#    4. Usuário, senha e chaves gerados aqui — nada disso existe no repositório
#    5. Diretório /opt/<nome>
#    6. Segredos do Swarm (não vão para variável de ambiente nem para o disco)
#    7. Build das imagens a partir deste repositório
#    8. Stack file + Caddyfile
#    9. docker stack deploy
#   10. UFW (22, e 80/443 conforme o modo)
#   11. scripts/update.sh para reconstruir e republicar
#   12. Espera dos serviços
#   13. Resumo. A senha só aparece nesta primeira vez: o Swarm não a devolve.
#
#  Rodar de novo não troca segredo que já existe e não troca o usuário do banco.
# ==============================================================================
set -euo pipefail
IFS=$'\n\t'

export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:${PATH:-}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[[ -f "${ROOT}/src/OpenTube.Web/Dockerfile" ]] \
  || { echo "Rode a partir do repositório do OpenTube (Dockerfile não encontrado)." >&2; exit 1; }

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
BLUE='\033[0;34m'; CYAN='\033[0;36m'; BOLD='\033[1m'; DIM='\033[2m'; NC='\033[0m'

info()    { echo -e "${BLUE}[INFO]${NC}  $*"; }
ok()      { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC}  $*"; }
die()     { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }
phase()   { echo -e "\n${BOLD}${CYAN}━━━  $*  ━━━${NC}"; }
sep()     { echo -e "${DIM}──────────────────────────────────────────────────────${NC}"; }

ask() {
  local prompt="$1" default="${2:-}" var_name="$3" value
  if [[ -n "$default" ]]; then
    read -rp "$(echo -e "  ${BOLD}${prompt}${NC} ${DIM}[${default}]${NC}: ")" value </dev/tty
    value="${value:-$default}"
  else
    while true; do
      read -rp "$(echo -e "  ${BOLD}${prompt}${NC}: ")" value </dev/tty
      [[ -n "$value" ]] && break
      echo -e "  ${RED}Obrigatório.${NC}"
    done
  fi
  printf -v "$var_name" '%s' "$value"
}

ask_secret() {
  local prompt="$1" var_name="$2" value
  while true; do
    read -rsp "$(echo -e "  ${BOLD}${prompt}${NC}: ")" value </dev/tty; echo
    [[ -n "$value" ]] && break
    echo -e "  ${RED}Obrigatório.${NC}"
  done
  printf -v "$var_name" '%s' "$value"
}

ask_optional() {
  local prompt="$1" default="${2:-}" var_name="$3" value
  if [[ -n "$default" ]]; then
    read -rp "$(echo -e "  ${BOLD}${prompt}${NC} ${DIM}[${default}]${NC}: ")" value </dev/tty
  else
    read -rp "$(echo -e "  ${BOLD}${prompt}${NC}: ")" value </dev/tty
  fi
  value="${value:-$default}"
  printf -v "$var_name" '%s' "$value"
}

ask_yn() {
  local prompt="$1" var_name="$2" default="${3:-y}" value hint
  [[ "$default" == "y" ]] && hint="Y/n" || hint="y/N"
  read -rp "$(echo -e "  ${BOLD}${prompt}${NC} ${DIM}[${hint}]${NC}: ")" value </dev/tty
  value="${value:-$default}"
  [[ "$value" =~ ^[Yy]$ ]] && printf -v "$var_name" 'y' || printf -v "$var_name" 'n'
}

gen_pass() {
  openssl rand -base64 64 | tr -d '/+=\n' | head -c "${1:-32}"
}

slugify() {
  echo "$1" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/-/g' | sed 's/-\+/-/g' | sed 's/^-\|-$//g'
}

read_conf() {
  local file="$1" key="$2"
  [[ -f "$file" ]] || return 0
  grep -m1 "^${key}=" "$file" | cut -d= -f2- | sed "s/^'//;s/'\$//" || true
}

require_root() { [[ $EUID -eq 0 ]] || die "Rode como root: sudo bash install.sh"; }

swarm_secret_exists() { docker secret inspect "$1" &>/dev/null; }

create_swarm_secret() {
  local name="$1" value="$2"
  if swarm_secret_exists "$name"; then
    warn "Segredo ${name} já existe — mantido"
  else
    printf '%s' "$value" | docker secret create "$name" - >/dev/null
    ok "Segredo criado: ${name}"
  fi
}

require_root
command -v openssl >/dev/null 2>&1 || die "Instale o openssl antes de continuar."

clear
echo -e "${BOLD}${CYAN}OpenTube${NC}  ·  instalador de produção  ·  Docker Swarm"
sep

# ==============================================================================
phase "FASE 1 — Configuração"
# ==============================================================================

echo ""
ask "Nome da aplicação" "opentube" APP_NAME_RAW
APP_NAME="$(slugify "$APP_NAME_RAW")"
[[ -n "$APP_NAME" ]] || die "Nome inválido."
APP_DIR="/opt/${APP_NAME}"
STACK_NAME="${APP_NAME//-/_}"
INSTALL_CONF="${APP_DIR}/etc/install.conf"
echo -e "  ${DIM}Diretório: ${APP_DIR}  |  Stack: ${STACK_NAME}${NC}"
echo ""

_admin_default="$(read_conf "$INSTALL_CONF" ADMIN_EMAIL)"
ask "Email do administrador (entra com código, sem senha)" "$_admin_default" ADMIN_EMAIL
[[ "$ADMIN_EMAIL" == *@*.* ]] || die "Email inválido: ${ADMIN_EMAIL}"
echo ""

_mode_default="$(read_conf "$INSTALL_CONF" INSTALL_MODE)"
_access_default="1"
[[ "$_mode_default" == "local" ]] && _access_default="2"
echo -e "  ${BOLD}Acesso${NC}"
echo -e "  ${BOLD}1)${NC} Hostname público — o Caddy pede certificado (portas 80 e 443)"
echo -e "  ${BOLD}2)${NC} Rede local — certificado interno, UFW só para redes privadas"
echo ""
while true; do
  read -rp "$(echo -e "  ${BOLD}Modo${NC} ${DIM}[${_access_default}]${NC}: ")" ACCESS_CHOICE </dev/tty
  ACCESS_CHOICE="${ACCESS_CHOICE:-$_access_default}"
  [[ "$ACCESS_CHOICE" == "1" || "$ACCESS_CHOICE" == "2" ]] && break
  echo -e "  ${RED}Escolha 1 ou 2.${NC}"
done

CERTBOT_EMAIL=""
if [[ "$ACCESS_CHOICE" == "1" ]]; then
  INSTALL_MODE="letsencrypt"
  _host_default="$(read_conf "$INSTALL_CONF" PUBLIC_HOST)"
  ask "Domínio (tem de apontar para esta máquina)" "$_host_default" PUBLIC_HOST
  PUBLIC_HOST="${PUBLIC_HOST#http://}"; PUBLIC_HOST="${PUBLIC_HOST#https://}"
  PUBLIC_HOST="${PUBLIC_HOST%%/*}"; PUBLIC_HOST="${PUBLIC_HOST%%:*}"
  [[ "$PUBLIC_HOST" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?\.[A-Za-z]{2,}$ ]] \
    || die "Domínio inválido: ${PUBLIC_HOST}"
  _mail_default="$(read_conf "$INSTALL_CONF" CERTBOT_EMAIL)"
  [[ -z "$_mail_default" ]] && _mail_default="$ADMIN_EMAIL"
  ask "Email para o Let's Encrypt" "$_mail_default" CERTBOT_EMAIL
  PUBLIC_URL="https://${PUBLIC_HOST}"
else
  INSTALL_MODE="local"
  _host_default="$(read_conf "$INSTALL_CONF" PUBLIC_HOST)"
  [[ -z "$_host_default" ]] && _host_default="opentube.local"
  ask "Nome nesta rede" "$_host_default" PUBLIC_HOST
  PUBLIC_HOST="${PUBLIC_HOST#http://}"; PUBLIC_HOST="${PUBLIC_HOST#https://}"
  PUBLIC_HOST="${PUBLIC_HOST%%/*}"; PUBLIC_HOST="${PUBLIC_HOST%%:*}"
  [[ "$PUBLIC_HOST" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] \
    || die "Nome inválido: ${PUBLIC_HOST}"
  PUBLIC_URL="https://${PUBLIC_HOST}"
fi
ALLOWED_HOSTS="${PUBLIC_HOST};localhost"
echo ""

_smtp_default="$(read_conf "$INSTALL_CONF" SMTP_HOST)"
_smtp_yn="n"
[[ -n "$_smtp_default" ]] && _smtp_yn="y"
ask_yn "Configurar SMTP agora? Sem isso o código de acesso não sai." CONFIGURAR_SMTP "$_smtp_yn"
SMTP_HOST=""; SMTP_PORT="587"; SMTP_USER=""; SMTP_FROM=""; SMTP_PASSWORD=""
if [[ "$CONFIGURAR_SMTP" == "y" ]]; then
  ask "Servidor SMTP" "${_smtp_default}" SMTP_HOST
  _smtp_port="$(read_conf "$INSTALL_CONF" SMTP_PORT)"
  [[ -z "$_smtp_port" ]] && _smtp_port="587"
  ask "Porta" "$_smtp_port" SMTP_PORT
  [[ "$SMTP_PORT" =~ ^[0-9]+$ ]] || die "Porta SMTP inválida."
  ask_optional "Usuário SMTP (vazio se o servidor não exige)" "$(read_conf "$INSTALL_CONF" SMTP_USER)" SMTP_USER
  _from_default="$(read_conf "$INSTALL_CONF" SMTP_FROM)"
  [[ -z "$_from_default" ]] && _from_default="nao-responda@${PUBLIC_HOST}"
  ask "Remetente" "$_from_default" SMTP_FROM
fi
echo ""

_EXISTING_MINIO="$(read_conf "$INSTALL_CONF" MINIO_DATA_DIR)"
echo -e "  ${BOLD}Disco do MinIO${NC}"
df -h -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null || true
echo ""
ask "Caminho absoluto dos objetos" "${_EXISTING_MINIO:-${APP_DIR}/data/minio}" MINIO_DATA_DIR
[[ "$MINIO_DATA_DIR" == /* ]] || die "O caminho do MinIO tem de ser absoluto."
MINIO_DATA_DIR="${MINIO_DATA_DIR%/}"
case "$MINIO_DATA_DIR" in
  /|/boot|/etc|/usr|/bin|/sbin|/lib|/root|/dev|/proc|/sys|/run)
    die "Caminho do MinIO recusado: ${MINIO_DATA_DIR}" ;;
esac
if [[ -n "$_EXISTING_MINIO" && "$_EXISTING_MINIO" != "$MINIO_DATA_DIR" ]]; then
  die "O MinIO já está em ${_EXISTING_MINIO}. Mantenha esse caminho ou mova os dados à mão antes."
fi

echo ""
sep
echo -e "  Aplicação : ${CYAN}${APP_NAME}${NC}  →  ${CYAN}${APP_DIR}${NC}"
echo -e "  Admin     : ${CYAN}${ADMIN_EMAIL}${NC}"
echo -e "  Acesso    : ${CYAN}${INSTALL_MODE}${NC}  ${PUBLIC_URL}"
echo -e "  MinIO     : ${CYAN}${MINIO_DATA_DIR}${NC}"
if [[ -n "$SMTP_HOST" ]]; then
  echo -e "  SMTP      : ${CYAN}${SMTP_HOST}:${SMTP_PORT}${NC}"
else
  echo -e "  SMTP      : ${YELLOW}não configurado${NC}"
fi
sep
echo ""
read -rp "$(echo -e "  ${BOLD}Instalar?${NC} ${DIM}[Y/n]${NC}: ")" _CONFIRM </dev/tty
[[ "${_CONFIRM:-y}" =~ ^[Yy]$ ]] || { echo "Cancelado."; exit 0; }

# ==============================================================================
phase "FASE 2 — Pacotes"
# ==============================================================================

if command -v apt-get >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq curl ca-certificates openssl ufw >/dev/null
  ok "Pacotes de base"
else
  warn "Sem apt-get — docker, curl e ufw precisam já estar instalados."
fi

if ! command -v docker >/dev/null 2>&1; then
  info "Instalando Docker..."
  curl -fsSL https://get.docker.com | sh
fi
docker info >/dev/null 2>&1 || die "O Docker não está acessível."
ok "Docker"

# ==============================================================================
phase "FASE 3 — Docker Swarm"
# ==============================================================================

HOST_IP="$(hostname -I 2>/dev/null | awk '{print $1}' || true)"
[[ -n "$HOST_IP" ]] || die "Não achei um IP desta máquina para anunciar o Swarm."

state="$(docker info --format '{{.Swarm.LocalNodeState}}' 2>/dev/null || echo inactive)"
if [[ "$state" == "active" ]]; then
  ok "Swarm já está ativo"
else
  docker swarm init --advertise-addr "$HOST_IP" >/dev/null
  ok "Swarm iniciado em ${HOST_IP}"
fi

# ==============================================================================
phase "FASE 4 — Credenciais"
# ==============================================================================

# O nome do banco e o usuário não são segredo (entram no stack file), mas não
# podem mudar numa reinstalação: o volume já foi inicializado com eles.
EXISTING_DB="$(read_conf "$INSTALL_CONF" POSTGRES_DB)"
EXISTING_USER="$(read_conf "$INSTALL_CONF" POSTGRES_USER)"
if [[ -n "$EXISTING_DB" && -n "$EXISTING_USER" ]]; then
  POSTGRES_DB="$EXISTING_DB"
  POSTGRES_USER="$EXISTING_USER"
  info "Reusando banco ${POSTGRES_DB} e usuário ${POSTGRES_USER}"
else
  POSTGRES_DB="db_$(openssl rand -hex 5)"
  POSTGRES_USER="user_$(openssl rand -hex 4)"
fi

CORE_SECRETS=(
  db_user db_password minio_root_user minio_root_password
  connection_string storage_access_key storage_secret_key
  token_pepper ip_hash_pepper
)
found=0
for nome in "${CORE_SECRETS[@]}"; do
  swarm_secret_exists "${STACK_NAME}_${nome}" && found=$((found + 1))
done
if [[ "$found" -ne 0 && "$found" -ne ${#CORE_SECRETS[@]} ]]; then
  die "Segredos do Swarm incompletos (${found}/${#CORE_SECRETS[@]}). Apague os ${STACK_NAME}_* só se for descartar os dados, e rode de novo."
fi
if [[ "$found" -eq ${#CORE_SECRETS[@]} ]]; then
  SECRETS_EXIST="y"
  info "Segredos já existem — senha e chaves não serão trocadas nem reimpressas."
else
  SECRETS_EXIST="n"
  if [[ -d "${APP_DIR}/data/postgres" ]] && find "${APP_DIR}/data/postgres" -mindepth 1 -print -quit 2>/dev/null | grep -q .; then
    die "Há dados em ${APP_DIR}/data/postgres sem os segredos do Swarm. A senha não pode ser recriada."
  fi
  POSTGRES_PASSWORD="$(gen_pass 32)"
  MINIO_ROOT_USER="minio_$(openssl rand -hex 4)"
  MINIO_ROOT_PASSWORD="$(gen_pass 32)"
  TOKEN_PEPPER="$(gen_pass 48)"
  IP_HASH_PEPPER="$(gen_pass 48)"
  ok "Usuário, senha e chaves gerados"
fi

if [[ -n "$SMTP_HOST" ]] && ! swarm_secret_exists "${STACK_NAME}_smtp_password"; then
  ask_secret "Senha SMTP" SMTP_PASSWORD
fi

# ==============================================================================
phase "FASE 5 — Diretórios"
# ==============================================================================

umask 077
mkdir -p \
  "${APP_DIR}/data/postgres" \
  "${APP_DIR}/data/caddy" \
  "${APP_DIR}/etc" \
  "${APP_DIR}/scripts" \
  "${APP_DIR}/logs" \
  "${MINIO_DATA_DIR}"
chmod 700 "${APP_DIR}" "${APP_DIR}/data" "${APP_DIR}/etc" "${MINIO_DATA_DIR}" || true

info "Baixando imagens de base para acertar o dono dos volumes..."
docker pull postgres:17-alpine >/dev/null
docker pull bitnamilegacy/minio:latest >/dev/null
docker pull caddy:2-alpine >/dev/null

MINIO_UID="$(docker run --rm --entrypoint id bitnamilegacy/minio:latest -u 2>/dev/null || echo 1001)"
CADDY_UID="$(docker run --rm --entrypoint id caddy:2-alpine -u 2>/dev/null || echo 1000)"
chown -R "${MINIO_UID}:${MINIO_UID}" "${MINIO_DATA_DIR}" || true
chown -R "${CADDY_UID}:${CADDY_UID}" "${APP_DIR}/data/caddy" || true
ok "Diretórios prontos"

# ==============================================================================
phase "FASE 6 — Segredos do Swarm"
# ==============================================================================

if [[ "$SECRETS_EXIST" == "n" ]]; then
  CONN="Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
  create_swarm_secret "${STACK_NAME}_db_user"            "$POSTGRES_USER"
  create_swarm_secret "${STACK_NAME}_db_password"        "$POSTGRES_PASSWORD"
  create_swarm_secret "${STACK_NAME}_minio_root_user"    "$MINIO_ROOT_USER"
  create_swarm_secret "${STACK_NAME}_minio_root_password" "$MINIO_ROOT_PASSWORD"
  create_swarm_secret "${STACK_NAME}_connection_string"  "$CONN"
  create_swarm_secret "${STACK_NAME}_storage_access_key" "$MINIO_ROOT_USER"
  create_swarm_secret "${STACK_NAME}_storage_secret_key" "$MINIO_ROOT_PASSWORD"
  create_swarm_secret "${STACK_NAME}_token_pepper"       "$TOKEN_PEPPER"
  create_swarm_secret "${STACK_NAME}_ip_hash_pepper"     "$IP_HASH_PEPPER"
fi

if [[ -n "${SMTP_PASSWORD}" ]]; then
  create_swarm_secret "${STACK_NAME}_smtp_password" "$SMTP_PASSWORD"
fi

# ==============================================================================
phase "FASE 7 — Imagens"
# ==============================================================================

IMAGE_TAG="initial"
info "Construindo a aplicação e o worker (a primeira vez demora)..."
docker build -t "${STACK_NAME}_app:${IMAGE_TAG}" -f "${ROOT}/src/OpenTube.Web/Dockerfile" "$ROOT"
docker build -t "${STACK_NAME}_worker:${IMAGE_TAG}" -f "${ROOT}/src/OpenTube.Worker/Dockerfile" "$ROOT"
ok "Imagens ${STACK_NAME}_{app,worker}:${IMAGE_TAG}"

install -m 0755 "${ROOT}/scripts/swarm-entrypoint.sh" "${APP_DIR}/scripts/entrypoint.sh"

# ==============================================================================
phase "FASE 8 — Stack e Caddy"
# ==============================================================================

TLS_LINE=""
ACME_BLOCK=""
if [[ "$INSTALL_MODE" == "local" ]]; then
  TLS_LINE=$'\n\ttls internal'
else
  ACME_BLOCK="{
	email ${CERTBOT_EMAIL}
}
"
fi

SMTP_ENV=""
SMTP_MOUNT=""
SMTP_TOP=""
if [[ -n "$SMTP_HOST" ]]; then
  SMTP_ENV="      Smtp__Host: \"${SMTP_HOST}\"
      Smtp__Port: \"${SMTP_PORT}\"
      Smtp__Username: \"${SMTP_USER}\"
      Smtp__From: \"${SMTP_FROM}\"
      Smtp__FromName: OpenTube
      Smtp__UseStartTls: \"true\""
  if swarm_secret_exists "${STACK_NAME}_smtp_password"; then
    SMTP_MOUNT="      - source: ${STACK_NAME}_smtp_password
        target: Smtp__Password
        uid: \"1001\"
        gid: \"1001\"
        mode: 0400"
    SMTP_TOP="  ${STACK_NAME}_smtp_password:
    external: true"
  fi
fi

cat > "${APP_DIR}/etc/Caddyfile" <<EOF
${ACME_BLOCK}${PUBLIC_HOST} {${TLS_LINE}
	encode zstd gzip

	handle_path /vod/* {
		forward_auth app:8080 {
			uri /_authz
			copy_headers Cookie
			header_up X-Forwarded-Uri {http.request.orig_uri}
		}
		rewrite * /vod{uri}
		reverse_proxy minio:9000
	}

	handle /originals/* {
		reverse_proxy minio:9000 {
			header_up Host {http.request.host}
		}
	}

	handle {
		reverse_proxy app:8080
	}

	header {
		Referrer-Policy "same-origin"
		X-Content-Type-Options "nosniff"
		X-Frame-Options "SAMEORIGIN"
		X-Robots-Tag "noindex, nofollow"
	}
}
EOF

# Segredos montados com o nome que o KeyPerFile transforma em chave de configuração
# (o __ vira ':'). O arquivo fica legível só pelo uid 1001, o usuário do processo.
APP_MOUNTS="      - source: ${STACK_NAME}_connection_string
        target: ConnectionStrings__Default
        uid: \"1001\"
        gid: \"1001\"
        mode: 0400
      - source: ${STACK_NAME}_storage_access_key
        target: Storage__AccessKey
        uid: \"1001\"
        gid: \"1001\"
        mode: 0400
      - source: ${STACK_NAME}_storage_secret_key
        target: Storage__SecretKey
        uid: \"1001\"
        gid: \"1001\"
        mode: 0400
      - source: ${STACK_NAME}_token_pepper
        target: Security__TokenPepper
        uid: \"1001\"
        gid: \"1001\"
        mode: 0400
      - source: ${STACK_NAME}_ip_hash_pepper
        target: Security__IpHashPepper
        uid: \"1001\"
        gid: \"1001\"
        mode: 0400
${SMTP_MOUNT}"

STACK_FILE="${APP_DIR}/docker-compose.prod.yml"
cat > "$STACK_FILE" <<STACK
# Stack gerada por install.sh. Sem senha neste arquivo: só referência a segredo.
services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_DB: "${POSTGRES_DB}"
      POSTGRES_USER_FILE: /run/secrets/${STACK_NAME}_db_user
      POSTGRES_PASSWORD_FILE: /run/secrets/${STACK_NAME}_db_password
    secrets:
      - ${STACK_NAME}_db_user
      - ${STACK_NAME}_db_password
    volumes:
      - ${APP_DIR}/data/postgres:/var/lib/postgresql/data
    networks:
      - internal
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U \$\$(cat /run/secrets/${STACK_NAME}_db_user) -d ${POSTGRES_DB}"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 20s
    deploy:
      replicas: 1
      placement:
        constraints: ["node.role == manager"]
      restart_policy:
        condition: on-failure
        delay: 5s

  minio:
    image: bitnamilegacy/minio:latest
    environment:
      MINIO_ROOT_USER_FILE: /run/secrets/${STACK_NAME}_minio_root_user
      MINIO_ROOT_PASSWORD_FILE: /run/secrets/${STACK_NAME}_minio_root_password
      MINIO_SCHEME: http
    secrets:
      - ${STACK_NAME}_minio_root_user
      - ${STACK_NAME}_minio_root_password
    volumes:
      - ${MINIO_DATA_DIR}:/bitnami/minio/data
    networks:
      - internal
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:9000/minio/health/live || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 10
      start_period: 20s
    deploy:
      replicas: 1
      placement:
        constraints: ["node.role == manager"]
      resources:
        limits:
          memory: 512M
      restart_policy:
        condition: on-failure
        delay: 5s

  app:
    image: ${STACK_NAME}_app:${IMAGE_TAG}
    entrypoint: ["/bin/sh", "/entrypoint.sh"]
    command: ["dotnet", "OpenTube.Web.dll"]
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: http://+:8080
      AllowedHosts: "${ALLOWED_HOSTS}"
      Storage__Endpoint: http://minio:9000
      Storage__PublicEndpoint: "${PUBLIC_URL}"
      Storage__SegmentAuthorization: "true"
      Storage__OriginalsBucket: originals
      Storage__VodBucket: vod
      Security__PublicUrl: "${PUBLIC_URL}"
      Security__AdminEmails__0: "${ADMIN_EMAIL}"
${SMTP_ENV}
    secrets:
${APP_MOUNTS}
    volumes:
      - ${APP_DIR}/scripts/entrypoint.sh:/entrypoint.sh:ro
    networks:
      - internal
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:8080/saude || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 5
      start_period: 60s
    deploy:
      replicas: 1
      placement:
        constraints: ["node.role == manager"]
      resources:
        limits:
          memory: 512M
      restart_policy:
        condition: on-failure
        delay: 5s

  worker:
    image: ${STACK_NAME}_worker:${IMAGE_TAG}
    entrypoint: ["/bin/sh", "/entrypoint.sh"]
    command: ["dotnet", "OpenTube.Worker.dll"]
    environment:
      DOTNET_ENVIRONMENT: Production
      Storage__Endpoint: http://minio:9000
      Storage__OriginalsBucket: originals
      Storage__VodBucket: vod
    secrets:
      - source: ${STACK_NAME}_connection_string
        target: ConnectionStrings__Default
        uid: "1001"
        gid: "1001"
        mode: 0400
      - source: ${STACK_NAME}_storage_access_key
        target: Storage__AccessKey
        uid: "1001"
        gid: "1001"
        mode: 0400
      - source: ${STACK_NAME}_storage_secret_key
        target: Storage__SecretKey
        uid: "1001"
        gid: "1001"
        mode: 0400
      - source: ${STACK_NAME}_token_pepper
        target: Security__TokenPepper
        uid: "1001"
        gid: "1001"
        mode: 0400
      - source: ${STACK_NAME}_ip_hash_pepper
        target: Security__IpHashPepper
        uid: "1001"
        gid: "1001"
        mode: 0400
    volumes:
      - ${APP_DIR}/scripts/entrypoint.sh:/entrypoint.sh:ro
    networks:
      - internal
    deploy:
      replicas: 1
      placement:
        constraints: ["node.role == manager"]
      resources:
        limits:
          memory: 2G
      restart_policy:
        condition: on-failure
        delay: 5s

  caddy:
    image: caddy:2-alpine
    ports:
      - target: 80
        published: 80
        protocol: tcp
        mode: ingress
      - target: 443
        published: 443
        protocol: tcp
        mode: ingress
    volumes:
      - ${APP_DIR}/etc/Caddyfile:/etc/caddy/Caddyfile:ro
      - ${APP_DIR}/data/caddy:/data
    networks:
      - internal
    deploy:
      replicas: 1
      placement:
        constraints: ["node.role == manager"]
      restart_policy:
        condition: on-failure
        delay: 5s

networks:
  internal:
    driver: overlay
    driver_opts:
      encrypted: "true"
    attachable: true

secrets:
  ${STACK_NAME}_db_user:
    external: true
  ${STACK_NAME}_db_password:
    external: true
  ${STACK_NAME}_minio_root_user:
    external: true
  ${STACK_NAME}_minio_root_password:
    external: true
  ${STACK_NAME}_connection_string:
    external: true
  ${STACK_NAME}_storage_access_key:
    external: true
  ${STACK_NAME}_storage_secret_key:
    external: true
  ${STACK_NAME}_token_pepper:
    external: true
  ${STACK_NAME}_ip_hash_pepper:
    external: true
${SMTP_TOP}
STACK
chmod 600 "$STACK_FILE" "${APP_DIR}/etc/Caddyfile"

cat > "$INSTALL_CONF" <<EOF
APP_NAME='${APP_NAME}'
STACK_NAME='${STACK_NAME}'
SOURCE_DIR='${ROOT}'
INSTALL_MODE='${INSTALL_MODE}'
PUBLIC_HOST='${PUBLIC_HOST}'
PUBLIC_URL='${PUBLIC_URL}'
ADMIN_EMAIL='${ADMIN_EMAIL}'
POSTGRES_DB='${POSTGRES_DB}'
POSTGRES_USER='${POSTGRES_USER}'
MINIO_DATA_DIR='${MINIO_DATA_DIR}'
SMTP_HOST='${SMTP_HOST}'
SMTP_PORT='${SMTP_PORT}'
SMTP_USER='${SMTP_USER}'
SMTP_FROM='${SMTP_FROM}'
CERTBOT_EMAIL='${CERTBOT_EMAIL}'
EOF
chmod 600 "$INSTALL_CONF"

cat > "${APP_DIR}/scripts/update.sh" <<'UPD'
#!/usr/bin/env bash
# Reconstrói as imagens a partir do SOURCE_DIR do install.conf e republica a stack.
# Não regenera senha. Puxe o código novo no SOURCE_DIR antes de rodar.
set -euo pipefail
APP_DIR="$(cd "$(dirname "$0")/.." && pwd)"
CONF="${APP_DIR}/etc/install.conf"
ler() { grep -m1 "^${1}=" "$CONF" | cut -d= -f2- | sed "s/^'//;s/'\$//" || true; }
SOURCE="$(ler SOURCE_DIR)"
STACK="$(ler STACK_NAME)"
[[ -n "$SOURCE" && -d "$SOURCE" ]] || { echo "SOURCE_DIR inválido em ${CONF}" >&2; exit 1; }
[[ -n "$STACK" ]] || { echo "STACK_NAME ausente em ${CONF}" >&2; exit 1; }
TAG="$(date -u +%Y%m%d%H%M%S)"
docker build -t "${STACK}_app:${TAG}" -f "${SOURCE}/src/OpenTube.Web/Dockerfile" "$SOURCE"
docker build -t "${STACK}_worker:${TAG}" -f "${SOURCE}/src/OpenTube.Worker/Dockerfile" "$SOURCE"
install -m 0755 "${SOURCE}/scripts/swarm-entrypoint.sh" "${APP_DIR}/scripts/entrypoint.sh"
STACK_FILE="${APP_DIR}/docker-compose.prod.yml"
sed -i "s|${STACK}_app:[^[:space:]]*|${STACK}_app:${TAG}|" "$STACK_FILE"
sed -i "s|${STACK}_worker:[^[:space:]]*|${STACK}_worker:${TAG}|" "$STACK_FILE"
docker stack deploy --compose-file "$STACK_FILE" --resolve-image never --prune "$STACK"
UPD
chmod 755 "${APP_DIR}/scripts/update.sh"
ok "Stack, Caddy e update.sh"

# ==============================================================================
phase "FASE 9 — Deploy"
# ==============================================================================

docker stack deploy --compose-file "$STACK_FILE" --resolve-image never --prune "$STACK_NAME"
ok "Stack ${STACK_NAME} publicada"

# ==============================================================================
phase "FASE 10 — Firewall"
# ==============================================================================

if command -v ufw >/dev/null 2>&1; then
  ufw allow 22/tcp comment "SSH-${APP_NAME}" >/dev/null || true
  if [[ "$INSTALL_MODE" == "local" ]]; then
    for cidr in 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16; do
      ufw allow from "$cidr" to any port 80 proto tcp comment "LAN-${APP_NAME}" >/dev/null || true
      ufw allow from "$cidr" to any port 443 proto tcp comment "LAN-${APP_NAME}" >/dev/null || true
    done
    ufw deny 80/tcp comment "Block-direct-${APP_NAME}" >/dev/null || true
    ufw deny 443/tcp comment "Block-direct-${APP_NAME}" >/dev/null || true
  else
    ufw allow 80/tcp comment "Web-${APP_NAME}" >/dev/null || true
    ufw allow 443/tcp comment "Web-${APP_NAME}" >/dev/null || true
  fi
  ufw --force enable >/dev/null || true
  ok "UFW atualizado (22 e o acesso web)"
  warn "O Docker publica a porta na chain própria. O UFW cobre o host; não substitui uma regra DOCKER-USER se esta máquina ficar exposta."
else
  warn "ufw não encontrado — firewall não foi alterado."
fi

# ==============================================================================
phase "FASE 11 — Subida"
# ==============================================================================

info "À espera das réplicas (até 3 minutos)..."
pronto="n"
for _ in $(seq 1 30); do
  total="$(docker stack services "$STACK_NAME" --format '{{.Name}}' 2>/dev/null | wc -l | tr -d ' ')"
  pendente="$(docker stack services "$STACK_NAME" --format '{{.Replicas}}' 2>/dev/null | grep -vc '1/1' || true)"
  if [[ "${total:-0}" -ge 5 && "${pendente:-1}" -eq 0 ]]; then
    pronto="y"
    break
  fi
  sleep 6
done
docker stack services "$STACK_NAME" || true
if [[ "$pronto" == "y" ]]; then
  ok "Serviços no ar"
else
  warn "Ainda há serviço fora de 1/1. Veja: docker stack ps ${STACK_NAME} --no-trunc"
fi

# ==============================================================================
phase "FASE 12 — Resumo"
# ==============================================================================

echo ""
sep
echo -e "  URL       : ${BOLD}${PUBLIC_URL}${NC}"
echo -e "  Admin     : ${BOLD}${ADMIN_EMAIL}${NC}"
echo -e "  ${DIM}Não há senha de administrador. O código chega por email (ou pelo Mailpit, em dev).${NC}"
echo -e "  Diretório : ${APP_DIR}"
echo -e "  Atualizar : ${APP_DIR}/scripts/update.sh"
echo -e "  Banco     : ${POSTGRES_DB} / ${POSTGRES_USER}"
if [[ "$SECRETS_EXIST" == "n" ]]; then
  echo ""
  echo -e "  ${YELLOW}Copie agora. Isto não fica em disco e o Swarm não devolve o valor.${NC}"
  echo -e "  Senha do banco     : ${BOLD}${POSTGRES_PASSWORD}${NC}"
  echo -e "  Usuário MinIO      : ${BOLD}${MINIO_ROOT_USER}${NC}"
  echo -e "  Senha MinIO        : ${BOLD}${MINIO_ROOT_PASSWORD}${NC}"
  echo -e "  Token pepper       : ${DIM}${TOKEN_PEPPER}${NC}"
  echo -e "  IP pepper          : ${DIM}${IP_HASH_PEPPER}${NC}"
else
  echo -e "  Segredos  : ${DIM}mantidos da instalação anterior${NC}"
fi
sep
echo ""
