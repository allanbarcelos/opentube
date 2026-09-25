#!/usr/bin/env bash
# ==============================================================================
#  uninstall.sh — remove what install.sh created for one installation
#
#  Usage: sudo bash uninstall.sh
#
#  Removes the stack, its Swarm secrets, the local images, the /opt/<name>
#  directory (including the database), and the MinIO disk if it lives outside
#  that directory. Does not remove Docker, Swarm, or other stacks.
# ==============================================================================
set -euo pipefail
IFS=$'\n\t'

export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:${PATH:-}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
BLUE='\033[0;34m'; CYAN='\033[0;36m'; BOLD='\033[1m'; DIM='\033[2m'; NC='\033[0m'

info()  { echo -e "${BLUE}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
die()   { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }
phase() { echo -e "\n${BOLD}${CYAN}━━━  $*  ━━━${NC}"; }
sep()   { echo -e "${DIM}──────────────────────────────────────────────────────${NC}"; }

slugify() {
  echo "$1" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/-/g' | sed 's/-\+/-/g' | sed 's/^-\|-$//g'
}

[[ $EUID -eq 0 ]] || die "Run as root: sudo bash uninstall.sh"

clear
echo -e "${BOLD}${RED}OpenTube${NC}  ·  this deletes the application and its data"
sep
echo ""

phase "PHASE 1 — Which installation"

read -rp "$(echo -e "  ${BOLD}Name used at install${NC}: ")" APP_NAME_RAW </dev/tty
APP_NAME="$(slugify "$APP_NAME_RAW")"
[[ -n "$APP_NAME" ]] || die "Invalid name."
APP_DIR="/opt/${APP_NAME}"
STACK_NAME="${APP_NAME//-/_}"
INSTALL_CONF="${APP_DIR}/etc/install.conf"

MINIO_DATA_DIR=""
if [[ -f "$INSTALL_CONF" ]]; then
  MINIO_DATA_DIR="$(grep -m1 '^MINIO_DATA_DIR=' "$INSTALL_CONF" | cut -d= -f2- | sed "s/^'//;s/'\$//" || true)"
fi

echo ""
echo -e "  Directory : ${CYAN}${APP_DIR}${NC}"
echo -e "  Stack     : ${CYAN}${STACK_NAME}${NC}"
if [[ -n "$MINIO_DATA_DIR" && "$MINIO_DATA_DIR" != "$APP_DIR" && "$MINIO_DATA_DIR" != "$APP_DIR"/* ]]; then
  echo -e "  MinIO     : ${RED}${MINIO_DATA_DIR}${NC}"
fi
echo ""
echo -e "  ${RED}The database, objects, and secrets of this installation are removed.${NC}"
echo ""
read -rp "$(echo -e "  ${BOLD}Type the name to confirm${NC} [${CYAN}${APP_NAME}${NC}]: ")" _CONFIRM </dev/tty
[[ "$_CONFIRM" == "$APP_NAME" ]] || die "The name does not match."
read -rp "$(echo -e "  ${BOLD}Are you sure?${NC} ${DIM}[yes/N]${NC}: ")" _SURE </dev/tty
[[ "$_SURE" == "yes" ]] || { echo "Cancelled."; exit 0; }

phase "PHASE 2 — Stack"

if docker stack ls --format '{{.Name}}' 2>/dev/null | grep -qx "$STACK_NAME"; then
  info "Removing stack ${STACK_NAME}..."
  docker stack rm "$STACK_NAME" >/dev/null || true
  elapsed=0
  until ! docker stack ps "$STACK_NAME" --format '{{.ID}}' 2>/dev/null | grep -q .; do
    sleep 3
    elapsed=$((elapsed + 3))
    [[ "$elapsed" -ge 90 ]] && { warn "A task is still shutting down"; break; }
  done
  sleep 3
  ok "Stack removed"
else
  warn "Stack ${STACK_NAME} is not running"
fi

phase "PHASE 3 — Secrets and images"

if docker secret ls --format '{{.Name}}' >/dev/null 2>&1; then
  while read -r name; do
    [[ -z "$name" ]] && continue
    docker secret rm "$name" >/dev/null 2>&1 && ok "Secret ${name}" || warn "Did not remove ${name} (still in use?)"
  done < <(docker secret ls --format '{{.Name}}' | grep "^${STACK_NAME}_" || true)
fi

if docker image ls --format '{{.Repository}}:{{.Tag}}' >/dev/null 2>&1; then
  while read -r image; do
    [[ -z "$image" ]] && continue
    docker rmi "$image" >/dev/null 2>&1 && ok "Image ${image}" || warn "Did not remove ${image}"
  done < <(docker image ls --format '{{.Repository}}:{{.Tag}}' | grep -E "^${STACK_NAME}_(app|worker):" || true)
fi

phase "PHASE 4 — Files"

if [[ -d "$APP_DIR" ]]; then
  rm -rf "$APP_DIR"
  ok "Removed ${APP_DIR}"
else
  warn "Directory ${APP_DIR} does not exist"
fi

if [[ -n "$MINIO_DATA_DIR" && "$MINIO_DATA_DIR" != "$APP_DIR" && "$MINIO_DATA_DIR" != "$APP_DIR"/* && -d "$MINIO_DATA_DIR" ]]; then
  rm -rf "$MINIO_DATA_DIR"
  ok "Removed ${MINIO_DATA_DIR}"
fi

phase "PHASE 5 — Firewall"

if command -v ufw >/dev/null 2>&1; then
  # Delete by comment, from the top each time, so the rule numbers stay valid.
  for tag in "SSH-${APP_NAME}" "Web-${APP_NAME}" "LAN-${APP_NAME}" "Block-direct-${APP_NAME}"; do
    while true; do
      num="$(ufw status numbered | grep -F "$tag" | head -1 | sed -n 's/.*\[\s*\([0-9][0-9]*\)\].*/\1/p' || true)"
      [[ -n "$num" ]] || break
      echo y | ufw delete "$num" >/dev/null 2>&1 || break
    done
  done
  ok "UFW rules tagged ${APP_NAME} removed, if any existed"
fi

echo ""
ok "Uninstall of ${APP_NAME} finished. Swarm itself is left in place."
echo ""
