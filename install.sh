#!/usr/bin/env bash
# ==============================================================================
#  install.sh — OpenTube production installer (single-node Docker Swarm)
#
#  Usage: sudo bash install.sh
#
#  Same shape as the Archeo installer, limited to what this application needs:
#    1. Interactive configuration (name, admin email, access, SMTP, disk)
#    2. Packages (docker, openssl, ufw)
#    3. Single-node Swarm
#    4. User, password, and keys generated here — none of that lives in the repo
#    5. Directory /opt/<name>
#    6. Swarm secrets (not environment variables, and not written to disk)
#    7. Images pulled from ghcr.io/allanbarcelos/opentube/{app,worker}
#    8. Stack file + Caddyfile
#    9. docker stack deploy
#   10. UFW (22, and 80/443 according to the mode)
#   11. scripts/update.sh to pull the published images and republish
#   12. Wait for the services
#   13. Summary. The password is shown only this first time: Swarm does not return it.
#
#  Running again does not replace a secret that already exists, and does not
#  change the database user.
# ==============================================================================
set -euo pipefail
IFS=$'\n\t'

export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:${PATH:-}"

APP_IMAGE="ghcr.io/allanbarcelos/opentube/app:latest"
WORKER_IMAGE="ghcr.io/allanbarcelos/opentube/worker:latest"

# Re-exec from a real file when the script is piped (`curl … | sudo bash`).
# Otherwise bash reads the script from the pipe while a later prompt blocks,
# and curl dies with "Failure writing output to destination".
OPENTUBE_INSTALL_URL="${OPENTUBE_INSTALL_URL:-https://raw.githubusercontent.com/allanbarcelos/opentube/main/install.sh}"
if [ -z "${OPENTUBE_FROMFILE:-}" ] && [ -p /dev/stdin ]; then
  _self="$(mktemp "${TMPDIR:-/tmp}/opentube-install.XXXXXX")" || _self=""
  if [ -n "$_self" ] \
     && command -v curl >/dev/null 2>&1 \
     && curl -fsSL "$OPENTUBE_INSTALL_URL" -o "$_self" \
     && [ -s "$_self" ]; then
    cat >/dev/null 2>&1 || true
    export OPENTUBE_FROMFILE=1
    if { : </dev/tty; } 2>/dev/null; then
      exec bash "$_self" "$@" </dev/tty
    else
      exec bash "$_self" "$@"
    fi
  fi
  [ -n "$_self" ] && rm -f "$_self"
fi

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
      echo -e "  ${RED}Required.${NC}"
    done
  fi
  printf -v "$var_name" '%s' "$value"
}

ask_secret() {
  local prompt="$1" var_name="$2" value
  while true; do
    read -rsp "$(echo -e "  ${BOLD}${prompt}${NC}: ")" value </dev/tty; echo
    [[ -n "$value" ]] && break
    echo -e "  ${RED}Required.${NC}"
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

require_root() { [[ $EUID -eq 0 ]] || die "Run as root: sudo bash install.sh"; }

swarm_secret_exists() { docker secret inspect "$1" &>/dev/null; }

create_swarm_secret() {
  local name="$1" value="$2"
  if swarm_secret_exists "$name"; then
    warn "Secret ${name} already exists — kept"
  else
    printf '%s' "$value" | docker secret create "$name" - >/dev/null
    ok "Secret created: ${name}"
  fi
}

require_root
command -v openssl >/dev/null 2>&1 || die "Install openssl before continuing."

clear
echo -e "${BOLD}${CYAN}OpenTube${NC}  ·  production installer  ·  Docker Swarm"
sep

# ==============================================================================
phase "PHASE 1 — Configuration"
# ==============================================================================

echo ""
ask "Application name" "opentube" APP_NAME_RAW
APP_NAME="$(slugify "$APP_NAME_RAW")"
[[ -n "$APP_NAME" ]] || die "Invalid name."
APP_DIR="/opt/${APP_NAME}"
STACK_NAME="${APP_NAME//-/_}"
INSTALL_CONF="${APP_DIR}/etc/install.conf"
echo -e "  ${DIM}Directory: ${APP_DIR}  |  Stack: ${STACK_NAME}${NC}"
echo ""

_admin_default="$(read_conf "$INSTALL_CONF" ADMIN_EMAIL)"
ask "Administrator email (signs in with a code, no password)" "$_admin_default" ADMIN_EMAIL
[[ "$ADMIN_EMAIL" == *@*.* ]] || die "Invalid email: ${ADMIN_EMAIL}"
echo ""

sep
echo -e "  ${BOLD}GitHub Container Registry (GHCR)${NC}"
echo -e "  ${DIM}Images: ${APP_IMAGE} and ${WORKER_IMAGE}${NC}"
echo ""
_ghcr_default="$(read_conf "$INSTALL_CONF" GHCR_USER)"
[[ -z "$_ghcr_default" ]] && _ghcr_default="allanbarcelos"
ask "GitHub username" "$_ghcr_default" GHCR_USER
ask_secret "GitHub personal access token (read:packages scope)" GHCR_TOKEN
echo ""

_mode_default="$(read_conf "$INSTALL_CONF" INSTALL_MODE)"
_access_default="1"
[[ "$_mode_default" == "local" ]] && _access_default="2"
echo -e "  ${BOLD}Access${NC}"
echo -e "  ${BOLD}1)${NC} Public hostname — Caddy requests a certificate (ports 80 and 443)"
echo -e "  ${BOLD}2)${NC} Local network — internal certificate, UFW limited to private networks"
echo ""
while true; do
  read -rp "$(echo -e "  ${BOLD}Mode${NC} ${DIM}[${_access_default}]${NC}: ")" ACCESS_CHOICE </dev/tty
  ACCESS_CHOICE="${ACCESS_CHOICE:-$_access_default}"
  [[ "$ACCESS_CHOICE" == "1" || "$ACCESS_CHOICE" == "2" ]] && break
  echo -e "  ${RED}Choose 1 or 2.${NC}"
done

CERTBOT_EMAIL=""
if [[ "$ACCESS_CHOICE" == "1" ]]; then
  INSTALL_MODE="letsencrypt"
  _host_default="$(read_conf "$INSTALL_CONF" PUBLIC_HOST)"
  ask "Domain (it must point at this machine)" "$_host_default" PUBLIC_HOST
  PUBLIC_HOST="${PUBLIC_HOST#http://}"; PUBLIC_HOST="${PUBLIC_HOST#https://}"
  PUBLIC_HOST="${PUBLIC_HOST%%/*}"; PUBLIC_HOST="${PUBLIC_HOST%%:*}"
  [[ "$PUBLIC_HOST" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?\.[A-Za-z]{2,}$ ]] \
    || die "Invalid domain: ${PUBLIC_HOST}"
  _mail_default="$(read_conf "$INSTALL_CONF" CERTBOT_EMAIL)"
  [[ -z "$_mail_default" ]] && _mail_default="$ADMIN_EMAIL"
  ask "Email for Let's Encrypt" "$_mail_default" CERTBOT_EMAIL
  PUBLIC_URL="https://${PUBLIC_HOST}"
else
  INSTALL_MODE="local"
  _host_default="$(read_conf "$INSTALL_CONF" PUBLIC_HOST)"
  [[ -z "$_host_default" ]] && _host_default="opentube.local"
  ask "Name on this network" "$_host_default" PUBLIC_HOST
  PUBLIC_HOST="${PUBLIC_HOST#http://}"; PUBLIC_HOST="${PUBLIC_HOST#https://}"
  PUBLIC_HOST="${PUBLIC_HOST%%/*}"; PUBLIC_HOST="${PUBLIC_HOST%%:*}"
  [[ "$PUBLIC_HOST" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] \
    || die "Invalid name: ${PUBLIC_HOST}"
  PUBLIC_URL="https://${PUBLIC_HOST}"
fi
ALLOWED_HOSTS="${PUBLIC_HOST};localhost"
echo ""

_smtp_default="$(read_conf "$INSTALL_CONF" SMTP_HOST)"
_smtp_yn="n"
[[ -n "$_smtp_default" ]] && _smtp_yn="y"
ask_yn "Configure SMTP now? Without it the access code is not sent." CONFIGURE_SMTP "$_smtp_yn"
SMTP_HOST=""; SMTP_PORT="587"; SMTP_USER=""; SMTP_FROM=""; SMTP_PASSWORD=""
if [[ "$CONFIGURE_SMTP" == "y" ]]; then
  ask "SMTP server" "${_smtp_default}" SMTP_HOST
  _smtp_port="$(read_conf "$INSTALL_CONF" SMTP_PORT)"
  [[ -z "$_smtp_port" ]] && _smtp_port="587"
  ask "Port" "$_smtp_port" SMTP_PORT
  [[ "$SMTP_PORT" =~ ^[0-9]+$ ]] || die "Invalid SMTP port."
  ask_optional "SMTP user (empty if the server does not require one)" "$(read_conf "$INSTALL_CONF" SMTP_USER)" SMTP_USER
  _from_default="$(read_conf "$INSTALL_CONF" SMTP_FROM)"
  [[ -z "$_from_default" ]] && _from_default="no-reply@${PUBLIC_HOST}"
  ask "From address" "$_from_default" SMTP_FROM
fi
echo ""

_EXISTING_MINIO="$(read_conf "$INSTALL_CONF" MINIO_DATA_DIR)"
echo -e "  ${BOLD}MinIO disk${NC}"
df -h -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null || true
echo ""
ask "Absolute path for objects" "${_EXISTING_MINIO:-${APP_DIR}/data/minio}" MINIO_DATA_DIR
[[ "$MINIO_DATA_DIR" == /* ]] || die "The MinIO path must be absolute."
MINIO_DATA_DIR="${MINIO_DATA_DIR%/}"
case "$MINIO_DATA_DIR" in
  /|/boot|/etc|/usr|/bin|/sbin|/lib|/root|/dev|/proc|/sys|/run)
    die "MinIO path refused: ${MINIO_DATA_DIR}" ;;
esac
if [[ -n "$_EXISTING_MINIO" && "$_EXISTING_MINIO" != "$MINIO_DATA_DIR" ]]; then
  die "MinIO is already at ${_EXISTING_MINIO}. Keep that path, or move the data by hand first."
fi

echo ""
sep
echo -e "  Application : ${CYAN}${APP_NAME}${NC}  →  ${CYAN}${APP_DIR}${NC}"
echo -e "  Admin       : ${CYAN}${ADMIN_EMAIL}${NC}"
echo -e "  Access      : ${CYAN}${INSTALL_MODE}${NC}  ${PUBLIC_URL}"
echo -e "  MinIO     : ${CYAN}${MINIO_DATA_DIR}${NC}"
if [[ -n "$SMTP_HOST" ]]; then
  echo -e "  SMTP      : ${CYAN}${SMTP_HOST}:${SMTP_PORT}${NC}"
else
  echo -e "  SMTP        : ${YELLOW}not configured${NC}"
fi
sep
echo ""
read -rp "$(echo -e "  ${BOLD}Install?${NC} ${DIM}[Y/n]${NC}: ")" _CONFIRM </dev/tty
[[ "${_CONFIRM:-y}" =~ ^[Yy]$ ]] || { echo "Cancelled."; exit 0; }

# ==============================================================================
phase "PHASE 2 — Packages"
# ==============================================================================

if command -v apt-get >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq curl ca-certificates openssl ufw >/dev/null
  ok "Base packages"
else
  warn "No apt-get — docker, curl, and ufw must already be installed."
fi

if ! command -v docker >/dev/null 2>&1; then
  info "Installing Docker..."
  curl -fsSL https://get.docker.com | sh
fi
docker info >/dev/null 2>&1 || die "Docker is not reachable."
ok "Docker"

# ==============================================================================
phase "PHASE 3 — Docker Swarm"
# ==============================================================================

HOST_IP="$(hostname -I 2>/dev/null | awk '{print $1}' || true)"
[[ -n "$HOST_IP" ]] || die "Could not find an IP on this machine to advertise Swarm."

state="$(docker info --format '{{.Swarm.LocalNodeState}}' 2>/dev/null || echo inactive)"
if [[ "$state" == "active" ]]; then
  ok "Swarm is already active"
else
  docker swarm init --advertise-addr "$HOST_IP" >/dev/null
  ok "Swarm started at ${HOST_IP}"
fi

# ==============================================================================
phase "PHASE 4 — Credentials"
# ==============================================================================

# The database name and user are not secrets (they go in the stack file), but
# they cannot change on a reinstall: the volume was already initialized with them.
EXISTING_DB="$(read_conf "$INSTALL_CONF" POSTGRES_DB)"
EXISTING_USER="$(read_conf "$INSTALL_CONF" POSTGRES_USER)"
if [[ -n "$EXISTING_DB" && -n "$EXISTING_USER" ]]; then
  POSTGRES_DB="$EXISTING_DB"
  POSTGRES_USER="$EXISTING_USER"
  info "Reusing database ${POSTGRES_DB} and user ${POSTGRES_USER}"
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
for name in "${CORE_SECRETS[@]}"; do
  swarm_secret_exists "${STACK_NAME}_${name}" && found=$((found + 1))
done
if [[ "$found" -ne 0 && "$found" -ne ${#CORE_SECRETS[@]} ]]; then
  die "Swarm secrets are incomplete (${found}/${#CORE_SECRETS[@]}). Delete the ${STACK_NAME}_* secrets only if you are discarding the data, then run again."
fi
if [[ "$found" -eq ${#CORE_SECRETS[@]} ]]; then
  SECRETS_EXIST="y"
  info "Secrets already exist — the password and keys will not be changed or printed again."
else
  SECRETS_EXIST="n"
  if [[ -d "${APP_DIR}/data/postgres" ]] && find "${APP_DIR}/data/postgres" -mindepth 1 -print -quit 2>/dev/null | grep -q .; then
    die "There is data in ${APP_DIR}/data/postgres without the Swarm secrets. The password cannot be recreated."
  fi
  POSTGRES_PASSWORD="$(gen_pass 32)"
  MINIO_ROOT_USER="minio_$(openssl rand -hex 4)"
  MINIO_ROOT_PASSWORD="$(gen_pass 32)"
  TOKEN_PEPPER="$(gen_pass 48)"
  IP_HASH_PEPPER="$(gen_pass 48)"
  ok "User, password, and keys generated"
fi

if [[ -n "$SMTP_HOST" ]] && ! swarm_secret_exists "${STACK_NAME}_smtp_password"; then
  ask_secret "SMTP password" SMTP_PASSWORD
fi

# ==============================================================================
phase "PHASE 5 — Directories"
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

info "Pulling base images so the volume owners can be set..."
docker pull postgres:17-alpine >/dev/null
docker pull bitnamilegacy/minio:latest >/dev/null
docker pull caddy:2-alpine >/dev/null

MINIO_UID="$(docker run --rm --entrypoint id bitnamilegacy/minio:latest -u 2>/dev/null || echo 1001)"
CADDY_UID="$(docker run --rm --entrypoint id caddy:2-alpine -u 2>/dev/null || echo 1000)"
chown -R "${MINIO_UID}:${MINIO_UID}" "${MINIO_DATA_DIR}" || true
chown -R "${CADDY_UID}:${CADDY_UID}" "${APP_DIR}/data/caddy" || true
ok "Directories ready"

# ==============================================================================
phase "PHASE 6 — Swarm secrets"
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
phase "PHASE 7 — Images"
# ==============================================================================

GHCR_CREDS="${APP_DIR}/etc/.ghcr-credentials"
cat > "$GHCR_CREDS" <<EOF
GHCR_USER='${GHCR_USER}'
GHCR_TOKEN='${GHCR_TOKEN}'
EOF
chmod 600 "$GHCR_CREDS"

info "Logging in to ghcr.io..."
echo "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_USER" --password-stdin
ok "Authenticated with ghcr.io"

info "Pulling images..."
docker pull "$APP_IMAGE"
docker pull "$WORKER_IMAGE"
ok "Images pulled"

# ==============================================================================
phase "PHASE 8 — Stack and Caddy"
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

# Secrets mounted under the name KeyPerFile turns into a configuration key
# (__ becomes ':'). The file is readable only by uid 1001, the process user.
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
# Stack generated by install.sh. No password in this file: only a secret reference.
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
    image: ${APP_IMAGE}
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
    networks:
      - internal
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:8080/health || exit 1"]
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
    image: ${WORKER_IMAGE}
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
INSTALL_MODE='${INSTALL_MODE}'
PUBLIC_HOST='${PUBLIC_HOST}'
PUBLIC_URL='${PUBLIC_URL}'
ADMIN_EMAIL='${ADMIN_EMAIL}'
GHCR_USER='${GHCR_USER}'
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
# Pull the published images and republish the stack.
# Does not regenerate the password.
set -euo pipefail
APP_DIR="$(cd "$(dirname "$0")/.." && pwd)"
CONF="${APP_DIR}/etc/install.conf"
CREDS="${APP_DIR}/etc/.ghcr-credentials"
read_conf() { grep -m1 "^${1}=" "$CONF" | cut -d= -f2- | sed "s/^'//;s/'\$//" || true; }
STACK="$(read_conf STACK_NAME)"
[[ -n "$STACK" ]] || { echo "Missing STACK_NAME in ${CONF}" >&2; exit 1; }
[[ -f "$CREDS" ]] || { echo "Missing ${CREDS}. Run install.sh again." >&2; exit 1; }
set -a
# shellcheck disable=SC1090
. "$CREDS"
set +a
echo "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_USER" --password-stdin
docker pull ghcr.io/allanbarcelos/opentube/app:latest
docker pull ghcr.io/allanbarcelos/opentube/worker:latest
docker stack deploy \
  --compose-file "${APP_DIR}/docker-compose.prod.yml" \
  --with-registry-auth \
  --resolve-image always \
  --prune \
  "$STACK"
UPD
chmod 755 "${APP_DIR}/scripts/update.sh"
ok "Stack, Caddy, and update.sh"

# ==============================================================================
phase "PHASE 9 — Deploy"
# ==============================================================================

docker stack deploy --compose-file "$STACK_FILE" --with-registry-auth --resolve-image always --prune "$STACK_NAME"
ok "Stack ${STACK_NAME} published"

# ==============================================================================
phase "PHASE 10 — Firewall"
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
  ok "UFW updated (22 and web access)"
  warn "Docker publishes the port on its own chain. UFW covers the host; it does not replace a DOCKER-USER rule if this machine is exposed."
else
  warn "ufw not found — the firewall was not changed."
fi

# ==============================================================================
phase "PHASE 11 — Startup"
# ==============================================================================

info "Waiting for replicas (up to 3 minutes)..."
ready="n"
for _ in $(seq 1 30); do
  total="$(docker stack services "$STACK_NAME" --format '{{.Name}}' 2>/dev/null | wc -l | tr -d ' ')"
  pending="$(docker stack services "$STACK_NAME" --format '{{.Replicas}}' 2>/dev/null | grep -vc '1/1' || true)"
  if [[ "${total:-0}" -ge 5 && "${pending:-1}" -eq 0 ]]; then
    ready="y"
    break
  fi
  sleep 6
done
docker stack services "$STACK_NAME" || true
if [[ "$ready" == "y" ]]; then
  ok "Services are up"
else
  warn "A service is still not 1/1. See: docker stack ps ${STACK_NAME} --no-trunc"
fi

# ==============================================================================
phase "PHASE 12 — Summary"
# ==============================================================================

echo ""
sep
echo -e "  URL       : ${BOLD}${PUBLIC_URL}${NC}"
echo -e "  Admin     : ${BOLD}${ADMIN_EMAIL}${NC}"
echo -e "  ${DIM}There is no administrator password. The code arrives by email (or Mailpit, in dev).${NC}"
echo -e "  Directory : ${APP_DIR}"
echo -e "  Update    : ${APP_DIR}/scripts/update.sh"
echo -e "  Database  : ${POSTGRES_DB} / ${POSTGRES_USER}"
if [[ "$SECRETS_EXIST" == "n" ]]; then
  echo ""
  echo -e "  ${YELLOW}Copy this now. It is not kept on disk, and Swarm does not return the value.${NC}"
  echo -e "  Database password  : ${BOLD}${POSTGRES_PASSWORD}${NC}"
  echo -e "  MinIO user         : ${BOLD}${MINIO_ROOT_USER}${NC}"
  echo -e "  MinIO password     : ${BOLD}${MINIO_ROOT_PASSWORD}${NC}"
  echo -e "  Token pepper       : ${DIM}${TOKEN_PEPPER}${NC}"
  echo -e "  IP pepper          : ${DIM}${IP_HASH_PEPPER}${NC}"
else
  echo -e "  Secrets   : ${DIM}kept from the previous installation${NC}"
fi
sep
echo ""
