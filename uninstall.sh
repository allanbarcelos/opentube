#!/usr/bin/env bash
# ==============================================================================
#  uninstall.sh — remove o que o install.sh criou para uma instalação
#
#  Uso: sudo bash uninstall.sh
#
#  Remove a stack, os segredos do Swarm com o prefixo dela, as imagens locais,
#  o diretório /opt/<nome> (incluindo o banco) e o disco do MinIO se estiver
#  fora desse diretório. Não remove o Docker, o Swarm nem outros stacks.
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

[[ $EUID -eq 0 ]] || die "Rode como root: sudo bash uninstall.sh"

clear
echo -e "${BOLD}${RED}OpenTube${NC}  ·  isto apaga a aplicação e os dados"
sep
echo ""

phase "FASE 1 — Qual instalação"

read -rp "$(echo -e "  ${BOLD}Nome usado na instalação${NC}: ")" APP_NAME_RAW </dev/tty
APP_NAME="$(slugify "$APP_NAME_RAW")"
[[ -n "$APP_NAME" ]] || die "Nome inválido."
APP_DIR="/opt/${APP_NAME}"
STACK_NAME="${APP_NAME//-/_}"
INSTALL_CONF="${APP_DIR}/etc/install.conf"

MINIO_DATA_DIR=""
if [[ -f "$INSTALL_CONF" ]]; then
  MINIO_DATA_DIR="$(grep -m1 '^MINIO_DATA_DIR=' "$INSTALL_CONF" | cut -d= -f2- | sed "s/^'//;s/'\$//" || true)"
fi

echo ""
echo -e "  Diretório : ${CYAN}${APP_DIR}${NC}"
echo -e "  Stack     : ${CYAN}${STACK_NAME}${NC}"
if [[ -n "$MINIO_DATA_DIR" && "$MINIO_DATA_DIR" != "$APP_DIR" && "$MINIO_DATA_DIR" != "$APP_DIR"/* ]]; then
  echo -e "  MinIO     : ${RED}${MINIO_DATA_DIR}${NC}"
fi
echo ""
echo -e "  ${RED}O banco, os objetos e os segredos desta instalação somem.${NC}"
echo ""
read -rp "$(echo -e "  ${BOLD}Digite o nome para confirmar${NC} [${CYAN}${APP_NAME}${NC}]: ")" _CONFIRM </dev/tty
[[ "$_CONFIRM" == "$APP_NAME" ]] || die "O nome não confere."
read -rp "$(echo -e "  ${BOLD}Tem certeza?${NC} ${DIM}[yes/N]${NC}: ")" _SURE </dev/tty
[[ "$_SURE" == "yes" ]] || { echo "Cancelado."; exit 0; }

phase "FASE 2 — Stack"

if docker stack ls --format '{{.Name}}' 2>/dev/null | grep -qx "$STACK_NAME"; then
  info "Removendo a stack ${STACK_NAME}..."
  docker stack rm "$STACK_NAME" >/dev/null || true
  elapsed=0
  until ! docker stack ps "$STACK_NAME" --format '{{.ID}}' 2>/dev/null | grep -q .; do
    sleep 3
    elapsed=$((elapsed + 3))
    [[ "$elapsed" -ge 90 ]] && { warn "Ainda há tarefa encerrando"; break; }
  done
  sleep 3
  ok "Stack removida"
else
  warn "Stack ${STACK_NAME} não está no ar"
fi

phase "FASE 3 — Segredos e imagens"

if docker secret ls --format '{{.Name}}' >/dev/null 2>&1; then
  while read -r nome; do
    [[ -z "$nome" ]] && continue
    docker secret rm "$nome" >/dev/null 2>&1 && ok "Segredo ${nome}" || warn "Não removi ${nome} (ainda em uso?)"
  done < <(docker secret ls --format '{{.Name}}' | grep "^${STACK_NAME}_" || true)
fi

if docker image ls --format '{{.Repository}}:{{.Tag}}' >/dev/null 2>&1; then
  while read -r imagem; do
    [[ -z "$imagem" ]] && continue
    docker rmi "$imagem" >/dev/null 2>&1 && ok "Imagem ${imagem}" || warn "Não removi ${imagem}"
  done < <(docker image ls --format '{{.Repository}}:{{.Tag}}' | grep -E "^${STACK_NAME}_(app|worker):" || true)
fi

phase "FASE 4 — Arquivos"

if [[ -d "$APP_DIR" ]]; then
  rm -rf "$APP_DIR"
  ok "Removido ${APP_DIR}"
else
  warn "Diretório ${APP_DIR} não existe"
fi

if [[ -n "$MINIO_DATA_DIR" && "$MINIO_DATA_DIR" != "$APP_DIR" && "$MINIO_DATA_DIR" != "$APP_DIR"/* && -d "$MINIO_DATA_DIR" ]]; then
  rm -rf "$MINIO_DATA_DIR"
  ok "Removido ${MINIO_DATA_DIR}"
fi

phase "FASE 5 — Firewall"

if command -v ufw >/dev/null 2>&1; then
  # Apaga pelo comentário, de trás para frente, para o número não andar.
  for tag in "SSH-${APP_NAME}" "Web-${APP_NAME}" "LAN-${APP_NAME}" "Block-direct-${APP_NAME}"; do
    while true; do
      num="$(ufw status numbered | grep -F "$tag" | head -1 | sed -n 's/.*\[\s*\([0-9][0-9]*\)\].*/\1/p' || true)"
      [[ -n "$num" ]] || break
      echo y | ufw delete "$num" >/dev/null 2>&1 || break
    done
  done
  ok "Regras UFW com a marca ${APP_NAME} removidas, se existiam"
fi

echo ""
ok "Desinstalação de ${APP_NAME} concluída. O Swarm em si permanece."
echo ""
