#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

# ==============================================================================
#  install.sh — OpenTube production installer (single-node Docker Swarm)
#
#  Usage: sudo bash install.sh              first installation, or to change settings
#         sudo bash install.sh --update [name]  update without questions (what update.sh runs)
#
#  Requires a 64-bit x86 (amd64) Linux server with apt (Ubuntu 22.04/24.04, Debian 12).
#
#  Same shape as the Archeo installer, limited to what this application needs:
#    1. Interactive configuration (name, admin email, access, SMTP, disk)
#    2. Packages (docker, openssl, ufw)
#    3. Single-node Swarm
#    4. User, password, and keys generated here — none of that lives in the repo
#    5. Directory /opt/<name>
#    6. Swarm secrets (not environment variables, and not written to disk)
#    7. Images pulled from ghcr.io/allanbarcelos/opentube/{app,worker,whisper}
#       (public images: no GitHub account or token needed)
#       Automatic captions run Whisper in its own container: the CUDA image when
#       an NVIDIA GPU is found (and the NVIDIA runtime is set up), the CPU one
#       otherwise. The container picks the model for the hardware on its own.
#    8. Stack file + Caddyfile
#    9. docker stack deploy
#   10. Firewall: UFW (22, and web access according to the mode). In Cloudflare
#       mode the origin port only answers Cloudflare, in UFW and in DOCKER-USER.
#   11. scripts/update.sh, which fetches the latest installer and runs it with --update:
#       images, stack, Caddy, firewall, and update.sh itself come out as the latest
#       version describes them, reusing the answers in etc/install.conf
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
WHISPER_IMAGE_BASE="ghcr.io/allanbarcelos/opentube/whisper"

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

# --update [name]: no questions. Every answer comes from the existing etc/install.conf and
# the existing secrets are kept; anything that needs a decision (installing the NVIDIA
# toolkit, restarting Docker) is left for an interactive run.
UPDATE_MODE="n"
UPDATE_NAME="opentube"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --update)
      UPDATE_MODE="y"
      if [[ -n "${2:-}" && "${2:-}" != --* ]]; then UPDATE_NAME="$2"; shift; fi
      ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
  shift
done

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
BLUE='\033[0;34m'; CYAN='\033[0;36m'; BOLD='\033[1m'; DIM='\033[2m'; NC='\033[0m'

info()    { echo -e "${BLUE}[INFO]${NC}  $*"; }
ok()      { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC}  $*"; }
die()     { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }
phase()   { echo -e "\n${BOLD}${CYAN}━━━  $*  ━━━${NC}"; }
sep()     { echo -e "${DIM}──────────────────────────────────────────────────────${NC}"; }

# In update mode the saved answer is taken as is and shown, so the log says what was used.
answered() { echo -e "  ${BOLD}$1${NC}: ${2:-${DIM}(empty)${NC}}"; }

ask() {
  local prompt="$1" default="${2:-}" var_name="$3" value
  if [[ "$UPDATE_MODE" == "y" ]]; then
    [[ -n "$default" ]] || die "No saved answer for \"${prompt}\". Run the installer without --update once."
    answered "$prompt" "$default"
    printf -v "$var_name" '%s' "$default"
    return
  fi
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
  if [[ "$UPDATE_MODE" == "y" ]]; then
    warn "${prompt}: not asked in update mode. Run the installer without --update to set it."
    printf -v "$var_name" '%s' ""
    return
  fi
  while true; do
    read -rsp "$(echo -e "  ${BOLD}${prompt}${NC}: ")" value </dev/tty; echo
    [[ -n "$value" ]] && break
    echo -e "  ${RED}Required.${NC}"
  done
  printf -v "$var_name" '%s' "$value"
}

ask_optional() {
  local prompt="$1" default="${2:-}" var_name="$3" value
  if [[ "$UPDATE_MODE" == "y" ]]; then
    answered "$prompt" "$default"
    printf -v "$var_name" '%s' "$default"
    return
  fi
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
  if [[ "$UPDATE_MODE" == "y" ]]; then
    answered "$prompt" "$default"
    printf -v "$var_name" '%s' "$default"
    return
  fi
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

# Cloudflare ranges, used when https://www.cloudflare.com/ips-v4 and ips-v6
# cannot be fetched: an install never ends up with an empty allow list.
CF_FALLBACK_V4="173.245.48.0/20
103.21.244.0/22
103.22.200.0/22
103.31.4.0/22
141.101.64.0/18
108.162.192.0/18
190.93.240.0/20
188.114.96.0/20
197.234.240.0/22
198.41.128.0/17
162.158.0.0/15
104.16.0.0/13
104.24.0.0/14
172.64.0.0/13
131.0.72.0/22"
CF_FALLBACK_V6="2400:cb00::/32
2606:4700::/32
2803:f800::/32
2405:b500::/32
2405:8100::/32
2a06:98c0::/29
2c0f:f248::/32"

# Sets CF_IPV4 and CF_IPV6. Only lines shaped like a CIDR are kept, so an error
# page served instead of the list never turns into firewall rules.
fetch_cloudflare_ips() {
  CF_IPV4="$(curl -fsS --max-time 15 https://www.cloudflare.com/ips-v4 2>/dev/null | grep -E '^[0-9.]+/[0-9]+$' || true)"
  CF_IPV6="$(curl -fsS --max-time 15 https://www.cloudflare.com/ips-v6 2>/dev/null | grep -E '^[0-9a-fA-F:]+/[0-9]+$' || true)"
  if [[ -z "$CF_IPV4" || -z "$CF_IPV6" ]]; then
    warn "Could not fetch the Cloudflare ranges — using the embedded list"
    CF_IPV4="$CF_FALLBACK_V4"
    CF_IPV6="$CF_FALLBACK_V6"
  fi
}

# Deletes the DOCKER-USER rules carrying the comment. "iptables -D" only matches
# a full rule specification, so each rule is read back with -S and removed as is.
docker_user_delete_tagged() {
  local cmd="$1" tag="$2" rule
  local -a args
  "$cmd" -n -L DOCKER-USER >/dev/null 2>&1 || return 0
  while IFS= read -r rule; do
    read -ra args <<< "${rule/#-A /-D }"
    "$cmd" "${args[@]}" 2>/dev/null || true
  done < <("$cmd" -S DOCKER-USER | grep -E -- "--comment \"?${tag}\"?( |$)" || true)
}

port_in_use() { ss -ltnH "( sport = :$1 )" 2>/dev/null | grep -q .; }

# NVIDIA GPU on the host, as the driver reports it. Empty when there is none.
host_gpu_name() {
  command -v nvidia-smi >/dev/null 2>&1 || return 0
  nvidia-smi --query-gpu=name,memory.total --format=csv,noheader 2>/dev/null | head -1 || true
}

docker_default_runtime() { docker info --format '{{.DefaultRuntime}}' 2>/dev/null || true; }

# ------------------------------------------------------------------------------
# Own certificate for the public hostname.
#
# Caddy serves a PEM chain plus an unencrypted PEM key. Operators arrive with
# PEM, DER, PKCS#7 or PKCS#12, sometimes with the key in a second file and
# sometimes encrypted. Conversion happens here, and the file is rejected before
# the stack changes when it cannot be a certificate for this hostname.
# The passphrase decrypts the key and is never written down: Caddy cannot ask
# for one when it starts.
# ------------------------------------------------------------------------------

# Set by the certificate checks so the installer can explain a file that is
# usable but incomplete (private CA, missing intermediate, close to expiry).
TLS_ERROR=""
TLS_CHAIN_INCOMPLETE="n"
TLS_SELF_SIGNED="n"
TLS_UNTRUSTED="n"
TLS_IS_CA="n"
TLS_EXPIRES_SOON="n"
CERT_LABEL=""
CERT_FINGERPRINT=""

cleanup_tls_stage() {
  if [[ -n "${TLS_STAGE:-}" && -d "$TLS_STAGE" ]]; then
    rm -rf "$TLS_STAGE"
  fi
}

# OpenSSL's wording for "this blob is encrypted and that passphrase is not it".
# Kept narrow on purpose: a file that simply is not a key must not look like
# a password problem, or the installer would ask for a passphrase forever.
tls_password_error() {
  # "wrong tag" / "no start line" is a file that is not an encrypted key at all.
  # OpenSSL sometimes mentions a password in the same breath; that must not win.
  if grep -Eiq 'wrong tag|not enough data|no start line|decode error' <<<"${1:-}"; then
    return 1
  fi
  grep -Eiq 'bad decrypt|mac verify error|invalid password|maybe wrong password|pkcs12 cipherfinal|bad password read' <<<"${1:-}"
}

tls_legacy_error() {
  grep -Eiq 'unsupported|digital envelope|unknown cipher|RC2' <<<"${1:-}"
}

tls_require_file() {
  local path="$1" label="$2"
  [[ -n "$path" ]] || die "${label} is required."
  [[ "$path" == /* ]] || die "${label} must be an absolute path: ${path}"
  [[ "$path" != *$'\n'* && "$path" != *$'\r'* && "$path" != *"'"* ]] \
    || die "${label} contains a character the installer cannot store safely."
  case "$path" in
    "${APP_DIR}/etc/tls"|"${APP_DIR}/etc/tls"/*)
      die "${label} is the copy this installer writes. Point at the original file, outside ${APP_DIR}/etc/tls."
      ;;
  esac
  if [[ -d "$path" ]]; then
    die "${label} is a directory. Point at the certificate file itself: ${path}"
  fi
  [[ -f "$path" ]] || die "${label} not found: ${path}"
  [[ -s "$path" ]] || die "${label} is empty: ${path}"
  [[ -r "$path" ]] || die "${label} is not readable: ${path}"
}

tls_pub_digest() {
  local file="$1" kind="$2" digest=""
  if [[ "$kind" == "cert" ]]; then
    digest="$(openssl x509 -in "$file" -noout -pubkey 2>/dev/null | openssl pkey -pubin -outform DER 2>/dev/null | openssl dgst -sha256 2>/dev/null | awk '{print $NF}')" || true
  else
    digest="$(openssl pkey -in "$file" -pubout -outform DER 2>/dev/null | openssl dgst -sha256 2>/dev/null | awk '{print $NF}')" || true
  fi
  printf '%s' "$digest"
}

tls_subject() {
  openssl x509 -in "$1" -noout -subject -nameopt RFC2253 2>/dev/null | sed 's/^subject=//' | tr -d '\r'
}

tls_issuer() {
  openssl x509 -in "$1" -noout -issuer -nameopt RFC2253 2>/dev/null | sed 's/^issuer=//' | tr -d '\r'
}

# Writes one clean PEM certificate. The same public key is stored once: a
# PKCS#12 often repeats the leaf next to the chain.
tls_add_cert() {
  local raw="$1" dir="$2" clean="" fp="" existing=""
  clean="$(mktemp "${dir}/raw.XXXXXX")"
  if ! openssl x509 -in "$raw" -out "$clean" >/dev/null 2>&1; then
    rm -f "$clean"
    return 1
  fi
  fp="$(tls_pub_digest "$clean" cert)"
  [[ -n "$fp" ]] || { rm -f "$clean"; return 1; }
  for existing in "${dir}"/c-*.pem; do
    [[ -f "$existing" ]] || continue
    if [[ "$(tls_pub_digest "$existing" cert)" == "$fp" ]]; then
      rm -f "$clean"
      return 0
    fi
  done
  local n=1
  while [[ -e "${dir}/c-${n}.pem" ]]; do n=$((n + 1)); done
  mv "$clean" "${dir}/c-${n}.pem"
}

tls_add_certs_from_pem() {
  local src="$1" dir="$2" rc=0
  [[ -s "$src" ]] || return 0
  local n=0 block=""
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == *"BEGIN CERTIFICATE"* ]]; then
      n=$((n + 1))
      block="${dir}/split-${n}.pem"
      : > "$block"
    fi
    if [[ -n "${block:-}" ]]; then
      printf '%s\n' "$line" >> "$block"
    fi
    if [[ "$line" == *"END CERTIFICATE"* ]]; then
      tls_add_cert "$block" "$dir" || rc=1
      rm -f "$block"
      block=""
    fi
  done < "$src"
  rm -f "${dir}"/split-*.pem
  return "$rc"
}

tls_add_key() {
  local raw="$1" dir="$2" clean="" fp="" existing=""
  clean="$(mktemp "${dir}/kraw.XXXXXX")"
  if ! openssl pkey -in "$raw" -out "$clean" >/dev/null 2>&1; then
    rm -f "$clean"
    return 1
  fi
  fp="$(tls_pub_digest "$clean" key)"
  [[ -n "$fp" ]] || { rm -f "$clean"; return 1; }
  for existing in "${dir}"/k-*.pem; do
    [[ -f "$existing" ]] || continue
    if [[ "$(tls_pub_digest "$existing" key)" == "$fp" ]]; then
      rm -f "$clean"
      return 0
    fi
  done
  local n=1
  while [[ -e "${dir}/k-${n}.pem" ]]; do n=$((n + 1)); done
  mv "$clean" "${dir}/k-${n}.pem"
  chmod 600 "${dir}/k-${n}.pem"
}

# Writes the passphrase to a file so it never appears in the process list.
# An empty passphrase is not passed this way: OpenSSL rejects an empty file
# even for a key that is not encrypted. Callers use "-passin pass:" instead.
tls_passfile() {
  local dir="$1" passphrase="${2:-}" file
  file="$(mktemp "${dir}/pass.XXXXXX")"
  chmod 600 "$file"
  printf '%s' "$passphrase" > "$file"
  printf '%s' "$file"
}

# 0 = key stored, 2 = a passphrase is required, 1 = this file has no key.
# An empty passphrase is "-passin pass:" — an empty password file makes OpenSSL
# refuse the read even when the key is not encrypted.
tls_read_key() {
  local src="$1" dir="$2" passphrase="${3:-}" passfile="" err="" rc=0 out=""
  out="$(mktemp "${dir}/keyout.XXXXXX")"
  err="$(openssl pkey -in "$src" -passin pass: -out "$out" 2>&1)" || rc=$?
  if [[ "$rc" -ne 0 ]] && tls_password_error "$err" && [[ -n "$passphrase" ]]; then
    passfile="$(tls_passfile "$dir" "$passphrase")"
    rc=0
    err="$(openssl pkey -in "$src" -passin "file:${passfile}" -out "$out" 2>&1)" || rc=$?
    rm -f "$passfile"
  fi
  if [[ "$rc" -ne 0 ]]; then
    rm -f "$out"
    if tls_password_error "$err"; then
      return 2
    fi
    return 1
  fi
  tls_add_key "$out" "$dir"
  local add_rc=$?
  rm -f "$out"
  return "$add_rc"
}

tls_try_pkcs12() {
  local src="$1" certdir="$2" keydir="$3" passphrase="${4:-}" legacy="${5:-n}"
  local -a extra=()
  [[ "$legacy" == "y" ]] && extra=(-legacy)
  local passfile="" err="" rc=0 certout="" keyout="" passin="pass:"
  certout="$(mktemp "${certdir}/p12c.XXXXXX")"
  keyout="$(mktemp "${keydir}/p12k.XXXXXX")"

  err="$(openssl pkcs12 -in "$src" "${extra[@]}" -nokeys -passin "$passin" -out "$certout" 2>&1)" || rc=$?
  if [[ "$rc" -ne 0 ]] && tls_password_error "$err" && [[ -n "$passphrase" ]]; then
    passfile="$(tls_passfile "$certdir" "$passphrase")"
    passin="file:${passfile}"
    rc=0
    err="$(openssl pkcs12 -in "$src" "${extra[@]}" -nokeys -passin "$passin" -out "$certout" 2>&1)" || rc=$?
  fi
  if [[ "$rc" -ne 0 ]]; then
    rm -f "$passfile" "$certout" "$keyout"
    # An old PKCS#12 (RC2, 3DES) fails in OpenSSL 3 before the password check,
    # sometimes with both "unsupported" and a password line. Retry with -legacy
    # first, or a correct passphrase would be reported as rejected.
    if [[ "$legacy" != "y" ]] && tls_legacy_error "$err"; then
      local legacy_rc=0
      tls_try_pkcs12 "$src" "$certdir" "$keydir" "$passphrase" y || legacy_rc=$?
      return "$legacy_rc"
    fi
    if tls_password_error "$err"; then
      return 2
    fi
    return 1
  fi

  rc=0
  err="$(openssl pkcs12 -in "$src" "${extra[@]}" -nocerts -noenc -passin "$passin" -out "$keyout" 2>&1)" || rc=$?
  if [[ "$rc" -ne 0 ]] && grep -q 'unknown option' <<<"$err"; then
    rc=0
    err="$(openssl pkcs12 -in "$src" "${extra[@]}" -nocerts -nodes -passin "$passin" -out "$keyout" 2>&1)" || rc=$?
  fi
  rm -f "$passfile"
  tls_add_certs_from_pem "$certout" "$certdir" || true
  if [[ "$rc" -eq 0 ]]; then
    tls_add_key "$keyout" "$keydir" || true
  fi
  rm -f "$certout" "$keyout"
  if tls_password_error "$err"; then
    return 2
  fi
  return 0
}

# Pulls every certificate and every key out of one file into the two pools.
# Returns 2 when the file is encrypted and the passphrase was not the right one.
tls_ingest_file() {
  local src="$1" certdir="$2" keydir="$3" passphrase="${4:-}" rc=0
  if grep -aq -- '-----BEGIN ' "$src"; then
    if grep -aq -- 'PRIVATE KEY' "$src"; then
      tls_read_key "$src" "$keydir" "$passphrase" || rc=$?
      if [[ "$rc" -eq 2 ]]; then
        return 2
      fi
      if [[ "$rc" -ne 0 ]] && ! grep -aq -- 'BEGIN CERTIFICATE' "$src"; then
        TLS_ERROR="Could not read the private key in ${src}."
        return 1
      fi
    fi
    if grep -aq -- 'BEGIN PKCS7' "$src" || grep -aq -- 'BEGIN PKCS #7' "$src"; then
      local p7
      p7="$(mktemp "${certdir}/p7.XXXXXX")"
      if openssl pkcs7 -print_certs -in "$src" -out "$p7" >/dev/null 2>&1; then
        tls_add_certs_from_pem "$p7" "$certdir" || true
      fi
      rm -f "$p7"
    fi
    if grep -aq -- 'BEGIN CERTIFICATE' "$src"; then
      tls_add_certs_from_pem "$src" "$certdir" || {
        TLS_ERROR="Could not read a certificate in ${src}."
        return 1
      }
    fi
    return 0
  fi

  tls_try_pkcs12 "$src" "$certdir" "$keydir" "$passphrase" || rc=$?
  if [[ "$rc" -eq 0 || "$rc" -eq 2 ]]; then
    return "$rc"
  fi

  local der
  der="$(mktemp "${certdir}/der.XXXXXX")"
  if openssl x509 -inform DER -in "$src" -out "$der" >/dev/null 2>&1; then
    tls_add_cert "$der" "$certdir" || true
    rm -f "$der"
    return 0
  fi
  if openssl pkcs7 -inform DER -print_certs -in "$src" -out "$der" >/dev/null 2>&1; then
    tls_add_certs_from_pem "$der" "$certdir" || true
    rm -f "$der"
    return 0
  fi
  rm -f "$der"
  return 0
}

tls_count_pem() {
  local n=0 file
  for file in "$1"/$2-*.pem; do
    [[ -f "$file" ]] || continue
    n=$((n + 1))
  done
  printf '%s' "$n"
}

# Checks the pair Caddy will serve. The first certificate in the chain file is
# the leaf. Sets CERT_LABEL, CERT_FINGERPRINT and the TLS_* warning flags.
tls_check_pair() {
  local chain="$1" key="$2" host="$3"
  TLS_ERROR=""
  TLS_CHAIN_INCOMPLETE="n"
  TLS_SELF_SIGNED="n"
  TLS_UNTRUSTED="n"
  TLS_IS_CA="n"
  TLS_EXPIRES_SOON="n"
  CERT_LABEL=""
  CERT_FINGERPRINT=""

  local leaf_fp="" key_fp=""
  leaf_fp="$(tls_pub_digest "$chain" cert)"
  key_fp="$(tls_pub_digest "$key" key)"
  if [[ -z "$leaf_fp" || -z "$key_fp" ]]; then
    TLS_ERROR="The certificate or the private key could not be read."
    return 1
  fi
  if [[ "$leaf_fp" != "$key_fp" ]]; then
    TLS_ERROR="The private key does not match the certificate."
    return 1
  fi

  local eku=""
  eku="$(openssl x509 -in "$chain" -noout -ext extendedKeyUsage 2>/dev/null || true)"
  if [[ -n "$eku" ]] && ! grep -Eq 'TLS Web Server Authentication|Any Extended Key Usage' <<<"$eku"; then
    TLS_ERROR="This certificate is not valid for a TLS server (extended key usage does not allow server authentication)."
    return 1
  fi

  local bc=""
  bc="$(openssl x509 -in "$chain" -noout -ext basicConstraints 2>/dev/null || true)"
  if grep -q 'CA:TRUE' <<<"$bc"; then
    TLS_IS_CA="y"
  fi

  if ! openssl x509 -in "$chain" -noout -checkend 0 >/dev/null 2>&1; then
    local ended=""
    ended="$(openssl x509 -in "$chain" -noout -enddate 2>/dev/null | sed 's/^notAfter=//')"
    TLS_ERROR="Certificate expired (${ended})."
    return 1
  fi

  local start_raw="" start_epoch="" now_epoch=""
  start_raw="$(openssl x509 -in "$chain" -noout -startdate 2>/dev/null | sed 's/^notBefore=//')"
  start_epoch="$(date -u -d "$start_raw" +%s 2>/dev/null)" || start_epoch=""
  now_epoch="$(date -u +%s)"
  # A few minutes of clock skew is normal between the machine that issued the
  # certificate and this one. More than that, the certificate is not valid yet.
  if [[ -n "$start_epoch" && "$start_epoch" -gt $((now_epoch + 600)) ]]; then
    TLS_ERROR="Certificate is not valid yet (${start_raw})."
    return 1
  fi
  if ! openssl x509 -in "$chain" -noout -checkend 2592000 >/dev/null 2>&1; then
    TLS_EXPIRES_SOON="y"
  fi

  if ! openssl x509 -in "$chain" -noout -checkhost "$host" >/dev/null 2>&1; then
    local names="" cn=""
    names="$(openssl x509 -in "$chain" -noout -ext subjectAltName 2>/dev/null | tr '\n' ' ' | sed 's/  */ /g' || true)"
    cn="$(tls_subject "$chain")"
    TLS_ERROR="Certificate does not cover ${host}. It is for: ${names:-$cn}"
    return 1
  fi

  local subj="" iss=""
  subj="$(tls_subject "$chain")"
  iss="$(tls_issuer "$chain")"
  local work="" intermediates=""
  work="$(mktemp -d)"
  tls_add_certs_from_pem "$chain" "$work" || true
  if [[ "$subj" == "$iss" ]]; then
    TLS_SELF_SIGNED="y"
  else
    local piece="" found="n"
    for piece in "$work"/c-*.pem; do
      [[ -f "$piece" ]] || continue
      if [[ "$(tls_subject "$piece")" == "$iss" ]]; then
        found="y"
        break
      fi
    done
    if [[ "$found" != "y" ]]; then
      TLS_CHAIN_INCOMPLETE="y"
      TLS_UNTRUSTED="y"
    else
      intermediates="$(mktemp)"
      for piece in "$work"/c-*.pem; do
        [[ -f "$piece" ]] || continue
        # The leaf is first in the chain file and was stored too. verify wants
        # only the intermediates in -untrusted.
        [[ "$(tls_pub_digest "$piece" cert)" == "$leaf_fp" ]] && continue
        cat "$piece" >> "$intermediates"
      done
      local verify=""
      verify="$(openssl verify -untrusted "$intermediates" "$chain" 2>&1)" || true
      if grep -q ': OK$' <<<"$verify"; then
        TLS_UNTRUSTED="n"
      elif grep -Eiq 'expired|not yet valid' <<<"$verify"; then
        rm -rf "$work"
        rm -f "$intermediates"
        TLS_ERROR="A certificate in the chain is outside its validity window: ${verify}"
        return 1
      else
        TLS_UNTRUSTED="y"
      fi
      rm -f "$intermediates"
    fi
  fi
  rm -rf "$work"

  local cn="" end_raw="" end_pretty=""
  cn="$(tls_subject "$chain")"
  cn="$(sed -n 's/.*CN=\([^,+]*\).*/\1/p' <<<"$cn")"
  [[ -n "$cn" ]] || cn="$(tls_subject "$chain")"
  end_raw="$(openssl x509 -in "$chain" -noout -enddate 2>/dev/null | sed 's/^notAfter=//')"
  end_pretty="$(date -u -d "$end_raw" +%Y-%m-%d 2>/dev/null || true)"
  [[ -n "$end_pretty" ]] || end_pretty="$end_raw"
  CERT_LABEL="${cn}, valid until ${end_pretty}"
  CERT_FINGERPRINT="$(openssl x509 -in "$chain" -noout -fingerprint -sha256 2>/dev/null | sed 's/.*=//;s/://g')"
  return 0
}

tls_warn_about_certificate() {
  if [[ "$TLS_SELF_SIGNED" == "y" ]]; then
    warn "This certificate is self-signed. A browser trusts it only where this CA is installed."
  fi
  if [[ "$TLS_IS_CA" == "y" ]]; then
    warn "This certificate is a certificate authority. Browsers usually refuse it for a site."
  fi
  if [[ "$TLS_EXPIRES_SOON" == "y" ]]; then
    warn "This certificate expires within 30 days (${CERT_LABEL})."
  fi
  if [[ "$TLS_CHAIN_INCOMPLETE" == "y" ]]; then
    warn "No intermediate certificate was found. Browsers may reject the site unless every device already has the CA."
  elif [[ "$TLS_UNTRUSTED" == "y" && "$TLS_SELF_SIGNED" != "y" ]]; then
    warn "The chain does not end at a CA in this system's trust store. A private CA has to be installed on each device."
  fi
}

# Builds fullchain.pem and key.pem in dest.
# Returns 0 on success, 2 when a passphrase is required, 3 when the files have
# certificates but no private key, 1 on any other problem (see TLS_ERROR).
tls_import_certificate() {
  local dest="$1" host="$2" cert_path="$3" key_path="${4:-}" chain_path="${5:-}" passphrase="${6:-}"
  local work="" certdir="" keydir="" rc=0
  TLS_ERROR=""
  work="$(mktemp -d "${dest}/work.XXXXXX")"
  certdir="${work}/certs"
  keydir="${work}/keys"
  mkdir -p "$certdir" "$keydir"
  chmod 700 "$work" "$keydir"

  tls_ingest_file "$cert_path" "$certdir" "$keydir" "$passphrase" || rc=$?
  if [[ "$rc" -eq 2 ]]; then
    rm -rf "$work"
    TLS_ERROR="A passphrase is required."
    return 2
  fi
  if [[ "$rc" -ne 0 ]]; then
    rm -rf "$work"
    [[ -n "$TLS_ERROR" ]] || TLS_ERROR="Could not read ${cert_path}."
    return 1
  fi

  if [[ -n "$key_path" ]]; then
    rc=0
    tls_ingest_file "$key_path" "$certdir" "$keydir" "$passphrase" || rc=$?
    if [[ "$rc" -eq 2 ]]; then
      rm -rf "$work"
      TLS_ERROR="A passphrase is required."
      return 2
    fi
    if [[ "$rc" -ne 0 ]]; then
      rm -rf "$work"
      [[ -n "$TLS_ERROR" ]] || TLS_ERROR="Could not read ${key_path}."
      return 1
    fi
  fi

  if [[ -n "$chain_path" ]]; then
    rc=0
    tls_ingest_file "$chain_path" "$certdir" "$keydir" "$passphrase" || rc=$?
    if [[ "$rc" -eq 2 ]]; then
      rm -rf "$work"
      TLS_ERROR="A passphrase is required for the CA chain file."
      return 2
    fi
    if [[ "$rc" -ne 0 ]]; then
      rm -rf "$work"
      [[ -n "$TLS_ERROR" ]] || TLS_ERROR="Could not read ${chain_path}."
      return 1
    fi
  fi

  local ncerts="" nkeys=""
  ncerts="$(tls_count_pem "$certdir" c)"
  nkeys="$(tls_count_pem "$keydir" k)"
  if [[ "$ncerts" -eq 0 ]]; then
    rm -rf "$work"
    TLS_ERROR="No certificate found in the file. PEM, DER, PKCS#7 and PKCS#12 are accepted."
    return 1
  fi
  if [[ "$nkeys" -eq 0 ]]; then
    rm -rf "$work"
    TLS_ERROR="No private key found. The certificate file does not contain one."
    return 3
  fi

  local leaf="" kfile="" matched=""
  for kfile in "$keydir"/k-*.pem; do
    [[ -f "$kfile" ]] || continue
    local kfp=""
    kfp="$(tls_pub_digest "$kfile" key)"
    for leaf in "$certdir"/c-*.pem; do
      [[ -f "$leaf" ]] || continue
      if [[ "$(tls_pub_digest "$leaf" cert)" == "$kfp" ]]; then
        matched="$kfile"
        break
      fi
    done
    [[ -n "$matched" ]] && break
  done
  if [[ -z "$matched" || -z "$leaf" ]]; then
    rm -rf "$work"
    TLS_ERROR="The private key does not match the certificate."
    return 1
  fi

  # Leaf first, then each issuer, and stop before a self-signed root. Clients
  # already have the root when they trust the CA; sending it only adds bytes.
  local ordered=("$leaf") current="" guard=0 piece=""
  current="$(tls_issuer "$leaf")"
  while [[ "$guard" -lt 20 ]]; do
    guard=$((guard + 1))
    local next=""
    for piece in "$certdir"/c-*.pem; do
      [[ -f "$piece" ]] || continue
      [[ "$piece" == "$leaf" ]] && continue
      if [[ "$(tls_subject "$piece")" == "$current" ]]; then
        next="$piece"
        break
      fi
    done
    [[ -n "$next" ]] || break
    if [[ "$(tls_subject "$next")" == "$(tls_issuer "$next")" ]]; then
      break
    fi
    ordered+=("$next")
    current="$(tls_issuer "$next")"
  done

  : > "${work}/fullchain.pem"
  local item="" first="y"
  for item in "${ordered[@]}"; do
    [[ "$first" == "y" ]] || printf '\n' >> "${work}/fullchain.pem"
    first="n"
    openssl x509 -in "$item" -outform PEM >> "${work}/fullchain.pem"
  done
  openssl pkey -in "$matched" -out "${work}/key.pem" >/dev/null 2>&1
  chmod 644 "${work}/fullchain.pem"
  chmod 600 "${work}/key.pem"

  rc=0
  tls_check_pair "${work}/fullchain.pem" "${work}/key.pem" "$host" || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    rm -rf "$work"
    return 1
  fi
  mv "${work}/fullchain.pem" "${dest}/fullchain.pem"
  mv "${work}/key.pem" "${dest}/key.pem"
  chmod 600 "${dest}/key.pem"
  rm -rf "$work"
  return 0
}

# The installed copy is what Caddy serves. Update keeps it when the original
# file cannot be read again without a question (it moved, or it is encrypted).
tls_reuse_installed() {
  local why="$1" dir="${APP_DIR}/etc/tls" rc=0
  if [[ -f "${dir}/fullchain.pem" && -f "${dir}/key.pem" ]]; then
    warn "${why} Keeping the certificate already installed."
    TLS_REUSE="y"
    tls_check_pair "${dir}/fullchain.pem" "${dir}/key.pem" "$PUBLIC_HOST" || rc=$?
    [[ "$rc" -eq 0 ]] || die "${TLS_ERROR:-The installed certificate is no longer valid.}"
    tls_warn_about_certificate
    ok "Using the installed certificate: ${CERT_LABEL}"
    return 0
  fi
  die "${why} There is no certificate installed yet. Run the installer without --update."
}

# Asks only for what the file does not already contain. --update never asks:
# an encrypted source stays as the copy already on disk.
prepare_own_certificate() {
  local saved_cert="" saved_key="" saved_chain="" rc=0 pass=""
  TLS_REUSE="n"
  TLS_STAGE="$(mktemp -d "${TMPDIR:-/tmp}/opentube-tls.XXXXXX")"
  chmod 700 "$TLS_STAGE"
  trap cleanup_tls_stage EXIT

  if [[ "$UPDATE_MODE" == "y" ]]; then
    CERT_FILE="$(read_conf "$INSTALL_CONF" CERT_FILE)"
    KEY_FILE="$(read_conf "$INSTALL_CONF" KEY_FILE)"
    CHAIN_FILE="$(read_conf "$INSTALL_CONF" CHAIN_FILE)"
    answered "Certificate file" "$CERT_FILE"
    [[ -n "$KEY_FILE" ]] && answered "Private key file" "$KEY_FILE"
    [[ -n "$CHAIN_FILE" ]] && answered "CA chain file" "$CHAIN_FILE"
    [[ -n "$CERT_FILE" ]] || die "No saved certificate path in ${INSTALL_CONF}. Run the installer without --update."
    if [[ ! -f "$CERT_FILE" ]]; then
      tls_reuse_installed "Certificate file ${CERT_FILE} is not on this machine anymore."
      return 0
    fi
    if [[ -n "$KEY_FILE" && ! -f "$KEY_FILE" ]]; then
      die "Private key file not found: ${KEY_FILE}"
    fi
    if [[ -n "$CHAIN_FILE" && ! -f "$CHAIN_FILE" ]]; then
      warn "CA chain file ${CHAIN_FILE} is not on this machine anymore. Continuing without it."
      CHAIN_FILE=""
    fi
    tls_import_certificate "$TLS_STAGE" "$PUBLIC_HOST" "$CERT_FILE" "$KEY_FILE" "$CHAIN_FILE" "" || rc=$?
    if [[ "$rc" -eq 2 ]]; then
      tls_reuse_installed "The certificate file is encrypted, and an update does not ask for the passphrase."
      return 0
    fi
    if [[ "$rc" -eq 3 ]]; then
      die "The certificate file has no private key, and no key file is saved. Run the installer without --update."
    fi
    if [[ "$rc" -ne 0 ]]; then
      die "${TLS_ERROR:-Could not read the certificate.}"
    fi
    tls_warn_about_certificate
    ok "Certificate ready: ${CERT_LABEL}"
    return 0
  fi

  saved_cert="$(read_conf "$INSTALL_CONF" CERT_FILE)"
  saved_key="$(read_conf "$INSTALL_CONF" KEY_FILE)"
  saved_chain="$(read_conf "$INSTALL_CONF" CHAIN_FILE)"
  ask "Absolute path of the certificate file" "$saved_cert" CERT_FILE
  tls_require_file "$CERT_FILE" "Certificate file"
  KEY_FILE=""
  CHAIN_FILE=""

  tls_import_certificate "$TLS_STAGE" "$PUBLIC_HOST" "$CERT_FILE" "" "" "" || rc=$?
  if [[ "$rc" -eq 2 ]]; then
    ask_secret "Passphrase for the private key" pass
    rc=0
    tls_import_certificate "$TLS_STAGE" "$PUBLIC_HOST" "$CERT_FILE" "" "" "$pass" || rc=$?
    if [[ "$rc" -eq 2 ]]; then
      pass=""
      die "The passphrase was rejected."
    fi
  fi
  if [[ "$rc" -eq 3 ]]; then
    ask "Absolute path of the private key file" "$saved_key" KEY_FILE
    tls_require_file "$KEY_FILE" "Private key file"
    rc=0
    tls_import_certificate "$TLS_STAGE" "$PUBLIC_HOST" "$CERT_FILE" "$KEY_FILE" "" "$pass" || rc=$?
    if [[ "$rc" -eq 2 ]]; then
      ask_secret "Passphrase for the private key" pass
      rc=0
      tls_import_certificate "$TLS_STAGE" "$PUBLIC_HOST" "$CERT_FILE" "$KEY_FILE" "" "$pass" || rc=$?
      if [[ "$rc" -eq 2 ]]; then
        pass=""
        die "The passphrase was rejected."
      fi
    fi
  fi
  if [[ "$rc" -ne 0 ]]; then
    pass=""
    die "${TLS_ERROR:-Could not read the certificate.}"
  fi

  if [[ "$TLS_CHAIN_INCOMPLETE" == "y" ]]; then
    ask_optional "Absolute path of the CA chain, if it is a separate file (empty to continue)" "$saved_chain" CHAIN_FILE
    if [[ -n "$CHAIN_FILE" ]]; then
      tls_require_file "$CHAIN_FILE" "CA chain file"
      rc=0
      tls_import_certificate "$TLS_STAGE" "$PUBLIC_HOST" "$CERT_FILE" "$KEY_FILE" "$CHAIN_FILE" "$pass" || rc=$?
      if [[ "$rc" -ne 0 ]]; then
        pass=""
        die "${TLS_ERROR:-Could not read the CA chain.}"
      fi
    fi
  else
    CHAIN_FILE=""
  fi
  pass=""
  tls_warn_about_certificate
  ok "Certificate ready: ${CERT_LABEL}"
}

# Deletes every UFW rule whose comment contains the tag, from the top each time,
# so the rule numbers stay valid.
ufw_delete_tagged() {
  local tag="$1" num
  while true; do
    num="$(ufw status numbered | grep -F "$tag" | head -1 | sed -n 's/.*\[\s*\([0-9][0-9]*\)\].*/\1/p' || true)"
    [[ -n "$num" ]] || break
    echo y | ufw delete "$num" >/dev/null 2>&1 || break
  done
}

# The certificate routines above are sourced by their test. A normal run falls through.
if [[ "${OPENTUBE_LIB_ONLY:-}" == "1" ]]; then
  return 0
fi

require_root
# The published images (app, worker, whisper) are built for x86-64 only.
case "$(uname -m)" in
  x86_64|amd64) ;;
  *) die "Unsupported architecture: $(uname -m). OpenTube runs in production on x86-64 (amd64) only." ;;
esac
command -v openssl >/dev/null 2>&1 || die "Install openssl before continuing."

if [[ "$UPDATE_MODE" == "y" ]]; then
  echo -e "${BOLD}${CYAN}OpenTube${NC}  ·  update  ·  $(date -u '+%Y-%m-%d %H:%M UTC')"
else
  clear 2>/dev/null || true
  echo -e "${BOLD}${CYAN}OpenTube${NC}  ·  production installer  ·  Docker Swarm"
fi
sep

# ==============================================================================
phase "PHASE 1 — Configuration"
# ==============================================================================

echo ""
if [[ "$UPDATE_MODE" == "y" ]]; then
  APP_NAME_RAW="$UPDATE_NAME"
  answered "Application name" "$APP_NAME_RAW"
else
  ask "Application name" "opentube" APP_NAME_RAW
fi
APP_NAME="$(slugify "$APP_NAME_RAW")"
[[ -n "$APP_NAME" ]] || die "Invalid name."
APP_DIR="/opt/${APP_NAME}"
STACK_NAME="${APP_NAME//-/_}"
INSTALL_CONF="${APP_DIR}/etc/install.conf"
if [[ "$UPDATE_MODE" == "y" && ! -f "$INSTALL_CONF" ]]; then
  die "No installation at ${APP_DIR} (${INSTALL_CONF} is missing). Run the installer without --update."
fi
echo -e "  ${DIM}Directory: ${APP_DIR}  |  Stack: ${STACK_NAME}${NC}"
echo ""

_admin_default="$(read_conf "$INSTALL_CONF" ADMIN_EMAIL)"
ask "Administrator email (signs in with a code, no password)" "$_admin_default" ADMIN_EMAIL
[[ "$ADMIN_EMAIL" == *@*.* ]] || die "Invalid email: ${ADMIN_EMAIL}"
echo ""

_mode_default="$(read_conf "$INSTALL_CONF" INSTALL_MODE)"
_access_default="1"
[[ "$_mode_default" == "local" ]] && _access_default="2"
[[ "$_mode_default" == "cloudflare" ]] && _access_default="3"
echo -e "  ${BOLD}Access${NC}"
echo -e "  ${BOLD}1)${NC} Public hostname — Let's Encrypt or your own certificate (ports 80 and 443)"
echo -e "  ${BOLD}2)${NC} Local network — internal certificate, UFW limited to private networks"
echo -e "  ${BOLD}3)${NC} Cloudflare — Cloudflare terminates HTTPS; this server answers HTTP on one"
echo -e "     port that only Cloudflare can reach (UFW and DOCKER-USER)"
echo ""
while [[ "$UPDATE_MODE" == "n" ]]; do
  read -rp "$(echo -e "  ${BOLD}Mode${NC} ${DIM}[${_access_default}]${NC}: ")" ACCESS_CHOICE </dev/tty
  ACCESS_CHOICE="${ACCESS_CHOICE:-$_access_default}"
  [[ "$ACCESS_CHOICE" =~ ^[123]$ ]] && break
  echo -e "  ${RED}Choose 1, 2, or 3.${NC}"
done
if [[ "$UPDATE_MODE" == "y" ]]; then
  [[ -n "$_mode_default" ]] || die "No saved access mode in ${INSTALL_CONF}. Run the installer without --update."
  ACCESS_CHOICE="$_access_default"
  answered "Mode" "$_mode_default"
fi

CERTBOT_EMAIL=""
APP_PORT=""
CERT_FILE=""
KEY_FILE=""
CHAIN_FILE=""
TLS_REUSE="n"
CERT_LABEL=""
CERT_FINGERPRINT=""
if [[ "$ACCESS_CHOICE" == "3" ]]; then
  INSTALL_MODE="cloudflare"
  _host_default="$(read_conf "$INSTALL_CONF" PUBLIC_HOST)"
  ask "Domain (proxied through Cloudflare, orange cloud)" "$_host_default" PUBLIC_HOST
  PUBLIC_HOST="${PUBLIC_HOST#http://}"; PUBLIC_HOST="${PUBLIC_HOST#https://}"
  PUBLIC_HOST="${PUBLIC_HOST%%/*}"; PUBLIC_HOST="${PUBLIC_HOST%%:*}"
  [[ "$PUBLIC_HOST" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?\.[A-Za-z]{2,}$ ]] \
    || die "Invalid domain: ${PUBLIC_HOST}"

  # 8080 is one of the HTTP ports Cloudflare proxies without an Origin Rule.
  _port_default="$(read_conf "$INSTALL_CONF" APP_PORT)"
  [[ -z "$_port_default" ]] && _port_default="8080"
  while true; do
    ask "Port Cloudflare connects to on this server" "$_port_default" APP_PORT
    if ! [[ "$APP_PORT" =~ ^[0-9]+$ && "$APP_PORT" -ge 1 && "$APP_PORT" -le 65535 ]]; then
      echo -e "  ${RED}Invalid port. Use a number between 1 and 65535.${NC}"
      [[ "$UPDATE_MODE" == "y" ]] && die "Invalid port in ${INSTALL_CONF}. Run the installer without --update."
      continue
    fi
    # On a re-run the port is held by this installation's own Caddy.
    if port_in_use "$APP_PORT" \
       && ! [[ "$_mode_default" == "cloudflare" && "$APP_PORT" == "$_port_default" ]]; then
      echo -e "  ${RED}Port ${APP_PORT} is already in use on this machine.${NC}"
      [[ "$UPDATE_MODE" == "y" ]] && die "Port ${APP_PORT} is taken by something else. Run the installer without --update."
      continue
    fi
    break
  done
  PUBLIC_URL="https://${PUBLIC_HOST}"
elif [[ "$ACCESS_CHOICE" == "1" ]]; then
  _host_default="$(read_conf "$INSTALL_CONF" PUBLIC_HOST)"
  ask "Domain (it must point at this machine)" "$_host_default" PUBLIC_HOST
  PUBLIC_HOST="${PUBLIC_HOST#http://}"; PUBLIC_HOST="${PUBLIC_HOST#https://}"
  PUBLIC_HOST="${PUBLIC_HOST%%/*}"; PUBLIC_HOST="${PUBLIC_HOST%%:*}"
  [[ "$PUBLIC_HOST" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?\.[A-Za-z]{2,}$ ]] \
    || die "Invalid domain: ${PUBLIC_HOST}"
  PUBLIC_URL="https://${PUBLIC_HOST}"

  # Let's Encrypt is the default. An installation that already uses its own
  # certificate keeps that choice on update, and offers it again as the default
  # the next time the questions are asked.
  _tls_default="1"
  [[ "$_mode_default" == "certificate" ]] && _tls_default="2"
  echo ""
  echo -e "  ${BOLD}Certificate${NC}"
  echo -e "  ${BOLD}1)${NC} Let's Encrypt — Caddy requests a certificate"
  echo -e "  ${BOLD}2)${NC} Your own certificate — a file already on this machine"
  echo -e "     ${DIM}PEM, DER, PKCS#7 or PKCS#12. The key may be in the same file or a second one.${NC}"
  echo ""
  if [[ "$UPDATE_MODE" == "y" ]]; then
    TLS_CHOICE="$_tls_default"
    answered "Certificate" "$([[ "$TLS_CHOICE" == "2" ]] && echo "own file" || echo "Let's Encrypt")"
  else
    while true; do
      read -rp "$(echo -e "  ${BOLD}Certificate${NC} ${DIM}[${_tls_default}]${NC}: ")" TLS_CHOICE </dev/tty
      TLS_CHOICE="${TLS_CHOICE:-$_tls_default}"
      [[ "$TLS_CHOICE" =~ ^[12]$ ]] && break
      echo -e "  ${RED}Choose 1 or 2.${NC}"
    done
  fi
  if [[ "$TLS_CHOICE" == "2" ]]; then
    INSTALL_MODE="certificate"
    prepare_own_certificate
  else
    INSTALL_MODE="letsencrypt"
    _mail_default="$(read_conf "$INSTALL_CONF" CERTBOT_EMAIL)"
    [[ -z "$_mail_default" ]] && _mail_default="$ADMIN_EMAIL"
    ask "Email for Let's Encrypt" "$_mail_default" CERTBOT_EMAIL
  fi
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

echo -e "  ${BOLD}Automatic captions${NC} ${DIM}(Whisper in its own container; it detects the spoken language)${NC}"
GPU_NAME="$(host_gpu_name)"
_cores="$(nproc 2>/dev/null || echo '?')"
_mem_gb="$(awk '/^MemTotal:/ { printf "%.0f", $2 / 1048576 }' /proc/meminfo 2>/dev/null || echo '?')"
if [[ -n "$GPU_NAME" ]]; then
  echo -e "  NVIDIA GPU found: ${CYAN}${GPU_NAME}${NC}"
else
  echo -e "  ${DIM}No NVIDIA GPU found: Whisper runs on the CPU (${_cores} cores, ${_mem_gb} GB of memory).${NC}"
fi
echo -e "  ${DIM}Without it, captions are uploaded or written in the application's editor.${NC}"
_wh_default="$(read_conf "$INSTALL_CONF" WHISPER_ENABLED)"
# An installation from before automatic captions keeps them off on update: turning them on
# downloads models and uses CPU, a decision for an interactive run.
if [[ -z "$_wh_default" ]]; then
  [[ "$UPDATE_MODE" == "y" ]] && _wh_default="n" || _wh_default="y"
fi
ask_yn "Enable automatic captions?" WHISPER_ENABLED "$_wh_default"
WHISPER_VARIANT="cpu"
if [[ "$WHISPER_ENABLED" == "y" && -n "$GPU_NAME" ]]; then
  _gpu_default="y"
  [[ "$(read_conf "$INSTALL_CONF" WHISPER_VARIANT)" == "cpu" ]] && _gpu_default="n"
  # On update the GPU is used only if the installation already used it.
  [[ "$UPDATE_MODE" == "y" && "$(read_conf "$INSTALL_CONF" WHISPER_VARIANT)" != "cuda" ]] && _gpu_default="n"
  ask_yn "Use the GPU for transcription? (needs the NVIDIA Container Toolkit)" _USE_GPU "$_gpu_default"
  [[ "$_USE_GPU" == "y" ]] && WHISPER_VARIANT="cuda"
fi
# Model: "auto" lets the container choose for the hardware. Override with
# WHISPER_MODEL=small (for instance) in the environment of this script.
WHISPER_MODEL="${WHISPER_MODEL:-$(read_conf "$INSTALL_CONF" WHISPER_MODEL)}"
WHISPER_MODEL="${WHISPER_MODEL:-auto}"
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
case "$INSTALL_MODE" in
  letsencrypt) ACCESS_LABEL="public hostname, Let's Encrypt" ;;
  certificate) ACCESS_LABEL="public hostname, own certificate" ;;
  local) ACCESS_LABEL="local network" ;;
  cloudflare) ACCESS_LABEL="Cloudflare" ;;
  *) ACCESS_LABEL="$INSTALL_MODE" ;;
esac
echo -e "  Access      : ${CYAN}${ACCESS_LABEL}${NC}  ${PUBLIC_URL}"
if [[ "$INSTALL_MODE" == "certificate" ]]; then
  echo -e "  Certificate : ${CYAN}${CERT_LABEL}${NC}"
fi
echo -e "  MinIO     : ${CYAN}${MINIO_DATA_DIR}${NC}"
if [[ -n "$SMTP_HOST" ]]; then
  echo -e "  SMTP      : ${CYAN}${SMTP_HOST}:${SMTP_PORT}${NC}"
else
  echo -e "  SMTP        : ${YELLOW}not configured${NC}"
fi
if [[ "$WHISPER_ENABLED" == "y" ]]; then
  echo -e "  Captions    : ${CYAN}automatic (Whisper, ${WHISPER_VARIANT^^}, model ${WHISPER_MODEL})${NC}"
else
  echo -e "  Captions    : ${DIM}upload and editor only${NC}"
fi
sep
echo ""
if [[ "$UPDATE_MODE" == "n" ]]; then
  read -rp "$(echo -e "  ${BOLD}Install?${NC} ${DIM}[Y/n]${NC}: ")" _CONFIRM </dev/tty
  [[ "${_CONFIRM:-y}" =~ ^[Yy]$ ]] || { echo "Cancelled."; exit 0; }
fi

# ==============================================================================
phase "PHASE 2 — Packages"
# ==============================================================================

if command -v apt-get >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq curl ca-certificates openssl ufw cron iproute2 >/dev/null
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

# Swarm cannot hand a GPU to a service the way "docker run --gpus" does. What works
# is the NVIDIA runtime as Docker's default: the container asks for the GPU with
# NVIDIA_VISIBLE_DEVICES (the Whisper image already sets it). For containers that
# do not ask, the runtime changes nothing.
if [[ "$WHISPER_VARIANT" == "cuda" && "$(docker_default_runtime)" != "nvidia" && "$UPDATE_MODE" == "y" ]]; then
  warn "The NVIDIA runtime is not Docker's default — Whisper runs on the CPU. Run the installer without --update to set it up."
  WHISPER_VARIANT="cpu"
elif [[ "$WHISPER_VARIANT" == "cuda" && "$(docker_default_runtime)" != "nvidia" ]]; then
  if ! command -v nvidia-ctk >/dev/null 2>&1 && command -v apt-get >/dev/null 2>&1; then
    ask_yn "The NVIDIA Container Toolkit is not installed. Install it now?" _INSTALL_CTK "y"
    if [[ "$_INSTALL_CTK" == "y" ]]; then
      info "Installing the NVIDIA Container Toolkit..."
      apt-get install -y -qq gnupg >/dev/null
      curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey \
        | gpg --dearmor --yes -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
      curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
        | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' \
        > /etc/apt/sources.list.d/nvidia-container-toolkit.list
      apt-get update -qq
      apt-get install -y -qq nvidia-container-toolkit >/dev/null
      ok "NVIDIA Container Toolkit"
    fi
  fi

  if command -v nvidia-ctk >/dev/null 2>&1; then
    warn "Setting the NVIDIA runtime as Docker's default restarts Docker (running containers restart with it)."
    ask_yn "Set it now?" _SET_RUNTIME "y"
    if [[ "$_SET_RUNTIME" == "y" ]]; then
      nvidia-ctk runtime configure --runtime=docker --set-as-default >/dev/null
      systemctl restart docker
      for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 2; done
    fi
  fi

  if [[ "$(docker_default_runtime)" == "nvidia" ]]; then
    ok "NVIDIA runtime is Docker's default"
  else
    warn "The NVIDIA runtime is not Docker's default — Whisper will run on the CPU."
    WHISPER_VARIANT="cpu"
  fi
elif [[ "$WHISPER_VARIANT" == "cuda" ]]; then
  ok "NVIDIA runtime is Docker's default"
fi
WHISPER_IMAGE=""
[[ "$WHISPER_ENABLED" == "y" ]] && WHISPER_IMAGE="${WHISPER_IMAGE_BASE}:${WHISPER_VARIANT}"

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
if [[ "$WHISPER_ENABLED" == "y" ]]; then
  # Whisper models (60 to 600 MB each), downloaded by the container on first start.
  mkdir -p "${APP_DIR}/data/whisper"
  chown -R 1001:1001 "${APP_DIR}/data/whisper" || true
fi
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

# The images are public. A login left by an older installation is removed: once
# its token expires, Docker would send it anyway and even public pulls would fail.
rm -f "${APP_DIR}/etc/.ghcr-credentials"
docker logout ghcr.io >/dev/null 2>&1 || true

info "Pulling images..."
docker pull "$APP_IMAGE"
docker pull "$WORKER_IMAGE"
if [[ -n "$WHISPER_IMAGE" ]] && ! docker pull "$WHISPER_IMAGE"; then
  warn "Could not pull ${WHISPER_IMAGE} — continuing without automatic captions."
  WHISPER_ENABLED="n"
  WHISPER_IMAGE=""
fi
ok "Images pulled"

# ==============================================================================
phase "PHASE 8 — Stack and Caddy"
# ==============================================================================

TLS_LINE=""
ACME_BLOCK=""
SITE_ADDRESS="${PUBLIC_HOST}"
PROXY_HEADERS=""
CADDY_CERT_LABEL=""
CF_TRUSTED="${APP_DIR}/etc/cloudflare-trusted.caddy"
if [[ "$INSTALL_MODE" != "certificate" && -d "${APP_DIR}/etc/tls" ]]; then
  # The copy made for a previous "own certificate" run. This mode does not use
  # it, and the key would otherwise stay on disk with nothing pointing at it.
  rm -rf "${APP_DIR}/etc/tls"
  info "Removed the previously installed certificate; this mode does not use it."
fi
if [[ "$INSTALL_MODE" == "certificate" ]]; then
  # The converted files live on the host. Caddy only ever sees the fixed
  # paths inside the container, so a strange character in the original path
  # cannot change the Caddyfile.
  mkdir -p "${APP_DIR}/etc/tls"
  chmod 700 "${APP_DIR}/etc/tls"
  if [[ "$TLS_REUSE" != "y" ]]; then
    [[ -f "${TLS_STAGE}/fullchain.pem" && -f "${TLS_STAGE}/key.pem" ]] \
      || die "Converted certificate is missing."
    cp "${TLS_STAGE}/fullchain.pem" "${APP_DIR}/etc/tls/fullchain.pem"
    cp "${TLS_STAGE}/key.pem" "${APP_DIR}/etc/tls/key.pem"
  fi
  [[ -f "${APP_DIR}/etc/tls/fullchain.pem" && -f "${APP_DIR}/etc/tls/key.pem" ]] \
    || die "No certificate installed in ${APP_DIR}/etc/tls."
  chown -R "${CADDY_UID}:${CADDY_UID}" "${APP_DIR}/etc/tls" || true
  chmod 700 "${APP_DIR}/etc/tls"
  chmod 644 "${APP_DIR}/etc/tls/fullchain.pem"
  chmod 600 "${APP_DIR}/etc/tls/key.pem"
  TLS_LINE=$'\n\ttls /etc/caddy/certs/fullchain.pem /etc/caddy/certs/key.pem'
  if [[ -n "$CERT_FINGERPRINT" ]]; then
    # A new certificate does not change the compose text otherwise, and Swarm
    # would keep the old Caddy process, still serving the previous file.
    CADDY_CERT_LABEL="
      labels:
        opentube.certificate: \"${CERT_FINGERPRINT}\""
  fi
  ok "Certificate installed for Caddy"
elif [[ "$INSTALL_MODE" == "local" ]]; then
  TLS_LINE=$'\n\ttls internal'
elif [[ "$INSTALL_MODE" == "cloudflare" ]]; then
  # Cloudflare terminates TLS and talks plain HTTP to this port. The real client
  # comes in CF-Connecting-IP, trusted only when the connection itself comes from
  # a Cloudflare range; the application receives it as X-Forwarded-For.
  fetch_cloudflare_ips
  printf '%s\n' "$CF_IPV4" > "${APP_DIR}/etc/cloudflare-ips-v4.txt"
  printf '%s\n' "$CF_IPV6" > "${APP_DIR}/etc/cloudflare-ips-v6.txt"
  printf 'trusted_proxies static %s\n' "$(printf '%s\n%s\n' "$CF_IPV4" "$CF_IPV6" | tr '\n' ' ')" > "$CF_TRUSTED"
  chmod 600 "$CF_TRUSTED" "${APP_DIR}/etc/cloudflare-ips-v4.txt" "${APP_DIR}/etc/cloudflare-ips-v6.txt"
  ok "Cloudflare ranges: $(wc -l <<<"$CF_IPV4" | tr -d ' ') IPv4, $(wc -l <<<"$CF_IPV6" | tr -d ' ') IPv6"

  ACME_BLOCK="{
	servers {
		import /etc/caddy/cloudflare-trusted.caddy
		client_ip_headers CF-Connecting-IP
	}
}
"
  SITE_ADDRESS="http://${PUBLIC_HOST}"
  PROXY_HEADERS=$'\n\t\t\theader_up X-Forwarded-For {client_ip}\n\t\t\theader_up X-Forwarded-Proto https'
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
${ACME_BLOCK}${SITE_ADDRESS} {${TLS_LINE}
	encode zstd gzip

	handle_path /vod/* {
		forward_auth app:8080 {
			uri /_authz
			copy_headers Cookie
			header_up X-Forwarded-Uri {http.request.orig_uri}${PROXY_HEADERS}
		}
		rewrite * /vod{uri}
		reverse_proxy minio:9000 {
			# Segments are authorized per request: no shared cache (a CDN in
			# front, for instance) may keep a copy and serve it to someone else.
			header_down Cache-Control "private, max-age=3600"
		}
	}

	handle /originals/* {
		reverse_proxy minio:9000 {
			header_up Host {http.request.host}
		}
	}

	handle {
		reverse_proxy app:8080 {${PROXY_HEADERS}
		}
	}

	header {
		Referrer-Policy "same-origin"
		X-Content-Type-Options "nosniff"
		X-Frame-Options "SAMEORIGIN"
		X-Robots-Tag "noindex, nofollow"
	}
}
EOF

# Published in host mode: the ingress routing mesh replaces the client address
# with an internal one, and the application would see everyone as the same
# origin (rate limits, simultaneous playbacks, Cloudflare ranges).
CADDY_EXTRA_VOLUMES=""
if [[ "$INSTALL_MODE" == "cloudflare" ]]; then
  CADDY_PORTS="      - target: 80
        published: ${APP_PORT}
        protocol: tcp
        mode: host"
  CADDY_EXTRA_VOLUMES="
      - ${CF_TRUSTED}:/etc/caddy/cloudflare-trusted.caddy:ro"
else
  CADDY_PORTS="      - target: 80
        published: 80
        protocol: tcp
        mode: host
      - target: 443
        published: 443
        protocol: tcp
        mode: host"
  if [[ "$INSTALL_MODE" == "certificate" ]]; then
    CADDY_EXTRA_VOLUMES="
      - ${APP_DIR}/etc/tls:/etc/caddy/certs:ro"
  fi
fi

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

WORKER_WHISPER_ENV=""
WHISPER_SERVICE=""
if [[ "$WHISPER_ENABLED" == "y" ]]; then
  # The worker checks this server every 30 s and tells the application; the
  # captions button only shows while Whisper answers.
  WORKER_WHISPER_ENV="      Transcription__ServerUrl: http://whisper:8080"
  WHISPER_SERVICE="
  whisper:
    image: ${WHISPER_IMAGE}
    environment:
      WHISPER_MODEL: \"${WHISPER_MODEL}\"
    volumes:
      - ${APP_DIR}/data/whisper:/models
    networks:
      - internal
    deploy:
      # Sem o balanceador virtual (IPVS) do Swarm: ele derruba em silêncio conexões paradas
      # há 15 minutos, e o worker fica calado esperando enquanto o Whisper transcreve.
      endpoint_mode: dnsrr
      replicas: 1
      placement:
        constraints: [\"node.role == manager\"]
      restart_policy:
        condition: on-failure
        delay: 10s
"
fi

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
${WORKER_WHISPER_ENV}
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
${WHISPER_SERVICE}
  caddy:
    image: caddy:2-alpine
    ports:
${CADDY_PORTS}
    volumes:
      - ${APP_DIR}/etc/Caddyfile:/etc/caddy/Caddyfile:ro
      - ${APP_DIR}/data/caddy:/data${CADDY_EXTRA_VOLUMES}
    networks:
      - internal
    deploy:${CADDY_CERT_LABEL}
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
POSTGRES_DB='${POSTGRES_DB}'
POSTGRES_USER='${POSTGRES_USER}'
MINIO_DATA_DIR='${MINIO_DATA_DIR}'
SMTP_HOST='${SMTP_HOST}'
SMTP_PORT='${SMTP_PORT}'
SMTP_USER='${SMTP_USER}'
SMTP_FROM='${SMTP_FROM}'
CERTBOT_EMAIL='${CERTBOT_EMAIL}'
CERT_FILE='${CERT_FILE}'
KEY_FILE='${KEY_FILE}'
CHAIN_FILE='${CHAIN_FILE}'
APP_PORT='${APP_PORT}'
WHISPER_ENABLED='${WHISPER_ENABLED}'
WHISPER_VARIANT='${WHISPER_VARIANT}'
WHISPER_MODEL='${WHISPER_MODEL}'
WHISPER_IMAGE='${WHISPER_IMAGE}'
EOF
chmod 600 "$INSTALL_CONF"

cat > "${APP_DIR}/scripts/update.sh" <<'UPD'
#!/usr/bin/env bash
# Update this installation to the latest version: fetches the current installer and runs it
# with --update, which reuses the answers in etc/install.conf and the existing secrets. The
# images, the stack, Caddy, the firewall rules, and this script itself come out as the latest
# version describes them. Nothing is asked, and no password or key changes.
#
# Usage: sudo /opt/<name>/scripts/update.sh
#        OPENTUBE_INSTALL_URL=<url> to take the installer from somewhere else (a fork, a branch).
#
# Everything runs inside main(): bash reads the whole function before running it, so the
# installer can rewrite this file while it is running.
main() {
  set -euo pipefail
  export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:${PATH:-}"

  [[ $EUID -eq 0 ]] || { echo "Run as root: sudo $0" >&2; exit 1; }

  local app_dir name url log
  app_dir="$(cd "$(dirname "$0")/.." && pwd)"
  name="$(basename "$app_dir")"
  url="${OPENTUBE_INSTALL_URL:-https://raw.githubusercontent.com/allanbarcelos/opentube/main/install.sh}"
  log="${app_dir}/logs/update.log"
  mkdir -p "${app_dir}/logs"

  # Global, not local: the cleanup runs on exit, after main() has returned.
  OPENTUBE_UPDATE_INSTALLER="$(mktemp "${TMPDIR:-/tmp}/opentube-update.XXXXXX")"
  trap 'rm -f "${OPENTUBE_UPDATE_INSTALLER:-}"' EXIT
  local installer="$OPENTUBE_UPDATE_INSTALLER"

  echo "Fetching the latest installer from ${url}"
  curl -fsSL --retry 3 "$url" -o "$installer"

  # A cut download or an error page must not run as root.
  bash -n "$installer" || { echo "The downloaded installer is not a valid script; nothing changed." >&2; exit 1; }
  grep -q 'OpenTube production installer' "$installer" \
    || { echo "The downloaded file is not the OpenTube installer; nothing changed." >&2; exit 1; }

  OPENTUBE_FROMFILE=1 bash "$installer" --update "$name" 2>&1 | tee -a "$log"
  return "${PIPESTATUS[0]}"
}

main "$@"; exit $?
UPD
chmod 755 "${APP_DIR}/scripts/update.sh"
ok "Stack, Caddy, and update.sh"

# ==============================================================================
phase "PHASE 9 — Deploy"
# ==============================================================================

docker stack deploy --compose-file "$STACK_FILE" --resolve-image always --prune "$STACK_NAME"
ok "Stack ${STACK_NAME} published"

# ==============================================================================
phase "PHASE 10 — Firewall"
# ==============================================================================

CF_UNIT="docker-user-rules-${STACK_NAME}.service"
CF_RULES_SCRIPT="${APP_DIR}/scripts/docker-user-rules.sh"
CF_UPDATE_SCRIPT="${APP_DIR}/scripts/update-cloudflare-ips.sh"
CF_CRON="/etc/cron.d/${STACK_NAME}-cloudflare"

# Undo the Cloudflare lock-down: used when a re-run switches to another mode.
remove_cloudflare_lockdown() {
  if systemctl list-unit-files "$CF_UNIT" >/dev/null 2>&1 && systemctl cat "$CF_UNIT" >/dev/null 2>&1; then
    systemctl disable --now "$CF_UNIT" >/dev/null 2>&1 || true
    rm -f "/etc/systemd/system/${CF_UNIT}"
    systemctl daemon-reload || true
  fi
  for cmd in iptables ip6tables; do
    command -v "$cmd" >/dev/null 2>&1 || continue
    for tag in "CF-${STACK_NAME}" "BLOCK-${STACK_NAME}" "ESTAB-${STACK_NAME}"; do
      docker_user_delete_tagged "$cmd" "$tag"
    done
  done
  rm -f "$CF_RULES_SCRIPT" "$CF_UPDATE_SCRIPT" "$CF_CRON"
  command -v ufw >/dev/null 2>&1 && ufw_delete_tagged "Cloudflare-${APP_NAME}"
  return 0
}

if command -v ufw >/dev/null 2>&1; then
  ufw allow 22/tcp comment "SSH-${APP_NAME}" >/dev/null || true
  # A re-run may switch modes: drop what the previous mode opened or closed.
  for tag in "Web-${APP_NAME}" "LAN-${APP_NAME}" "Cloudflare-${APP_NAME}" "Block-direct-${APP_NAME}"; do
    ufw_delete_tagged "$tag"
  done
  if [[ "$INSTALL_MODE" == "cloudflare" ]]; then
    while IFS= read -r ip; do
      [[ -z "$ip" ]] && continue
      ufw allow from "$ip" to any port "$APP_PORT" proto tcp comment "Cloudflare-${APP_NAME}" >/dev/null || true
    done < <(cat "${APP_DIR}/etc/cloudflare-ips-v4.txt" "${APP_DIR}/etc/cloudflare-ips-v6.txt")
    ufw deny "${APP_PORT}/tcp" comment "Block-direct-${APP_NAME}" >/dev/null || true
  elif [[ "$INSTALL_MODE" == "local" ]]; then
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
else
  warn "ufw not found — the firewall was not changed."
fi

if [[ "$INSTALL_MODE" == "cloudflare" ]]; then
  # A port published by Docker skips UFW: the packets go through FORWARD, and
  # DOCKER-USER is the chain Docker leaves for rules like these. Without them the
  # origin would answer anyone who finds its address, bypassing Cloudflare.
  command -v iptables >/dev/null 2>&1 || die "iptables not found — the origin port cannot be restricted to Cloudflare."

  cat > "$CF_RULES_SCRIPT" <<RULES
#!/usr/bin/env bash
# Restrict port ${APP_PORT} (Caddy) to Cloudflare in DOCKER-USER. Run by
# ${CF_UNIT} after docker.service, and by update-cloudflare-ips.sh.
set -euo pipefail
export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
APP_PORT="${APP_PORT}"
STACK_NAME="${STACK_NAME}"
CF_V4="${APP_DIR}/etc/cloudflare-ips-v4.txt"
CF_V6="${APP_DIR}/etc/cloudflare-ips-v6.txt"
RULES
  cat >> "$CF_RULES_SCRIPT" <<'RULES'

# "iptables -D" only matches a full rule specification: read each tagged rule
# back with -S and delete it as it is.
delete_tagged() {
  local cmd="$1" tag="$2" rule
  local -a args
  while IFS= read -r rule; do
    read -ra args <<< "${rule/#-A /-D }"
    "$cmd" "${args[@]}"
  done < <("$cmd" -S DOCKER-USER | grep -E -- "--comment \"?${tag}\"?( |$)" || true)
}

apply() {
  local cmd="$1" list="$2"
  # IPv6 filtering only exists when Docker manages ip6tables.
  "$cmd" -n -L DOCKER-USER >/dev/null 2>&1 || { [[ "$cmd" == ip6tables ]] && return 0; exit 1; }

  for tag in "CF-${STACK_NAME}" "BLOCK-${STACK_NAME}" "ESTAB-${STACK_NAME}"; do
    delete_tagged "$cmd" "$tag"
  done

  # Everything goes in at the top, in reverse, so the order ends up as
  # established -> Cloudflare -> drop, ahead of any RETURN Docker keeps in the chain.
  "$cmd" -I DOCKER-USER 1 -p tcp -m conntrack --ctorigdstport "$APP_PORT" \
    -m comment --comment "BLOCK-${STACK_NAME}" -j DROP

  while IFS= read -r ip; do
    [[ -z "$ip" ]] && continue
    "$cmd" -I DOCKER-USER 1 -p tcp -m conntrack --ctorigdstport "$APP_PORT" \
      -s "$ip" -m comment --comment "CF-${STACK_NAME}" -j RETURN
  done < "$list"

  "$cmd" -I DOCKER-USER 1 -m conntrack --ctstate RELATED,ESTABLISHED \
    -m comment --comment "ESTAB-${STACK_NAME}" -j RETURN
}

apply iptables "$CF_V4"
if command -v ip6tables >/dev/null 2>&1; then apply ip6tables "$CF_V6"; fi
echo "[${STACK_NAME}] DOCKER-USER: port ${APP_PORT} restricted to Cloudflare"
RULES
  chmod 750 "$CF_RULES_SCRIPT"

  cat > "/etc/systemd/system/${CF_UNIT}" <<UNIT
[Unit]
Description=DOCKER-USER rules restricting ${STACK_NAME} to Cloudflare
After=docker.service
BindsTo=docker.service

[Service]
Type=oneshot
ExecStart=${CF_RULES_SCRIPT}
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
UNIT
  systemctl daemon-reload || die "systemctl daemon-reload failed"
  systemctl enable "$CF_UNIT" >/dev/null 2>&1 || die "Could not enable ${CF_UNIT}"
  # restart, not start: on a re-run the oneshot is already "active" and would not run again.
  systemctl restart "$CF_UNIT" \
    || die "Could not apply the DOCKER-USER rules — port ${APP_PORT} would be open to anyone. Fix iptables and run again."
  ok "DOCKER-USER: port ${APP_PORT} open only to Cloudflare (${CF_UNIT})"

  # Cloudflare adds ranges now and then. Once a month: fetch, and only if the
  # list looks valid, rewrite the files, UFW, DOCKER-USER, and Caddy's trust.
  cat > "$CF_UPDATE_SCRIPT" <<UPDCF
#!/usr/bin/env bash
# Refresh the Cloudflare ranges for ${STACK_NAME} (UFW, DOCKER-USER, Caddy).
set -euo pipefail
export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
APP_DIR="${APP_DIR}"
APP_NAME="${APP_NAME}"
STACK_NAME="${STACK_NAME}"
APP_PORT="${APP_PORT}"
UPDCF
  cat >> "$CF_UPDATE_SCRIPT" <<'UPDCF'
LOG="${APP_DIR}/logs/cloudflare-ips.log"
exec >>"$LOG" 2>&1
echo "$(date -u '+%Y-%m-%d %H:%M:%S') refreshing Cloudflare ranges"

V4="$(curl -fsS --max-time 15 https://www.cloudflare.com/ips-v4 | grep -E '^[0-9.]+/[0-9]+$' || true)"
V6="$(curl -fsS --max-time 15 https://www.cloudflare.com/ips-v6 | grep -E '^[0-9a-fA-F:]+/[0-9]+$' || true)"
[[ -n "$V4" && -n "$V6" ]] || { echo "fetch failed; rules left as they were"; exit 1; }

printf '%s
' "$V4" > "${APP_DIR}/etc/cloudflare-ips-v4.txt"
printf '%s
' "$V6" > "${APP_DIR}/etc/cloudflare-ips-v6.txt"
printf 'trusted_proxies static %s
' "$(printf '%s
%s
' "$V4" "$V6" | tr '
' ' ')"   > "${APP_DIR}/etc/cloudflare-trusted.caddy"

if command -v ufw >/dev/null 2>&1; then
  while true; do
    num="$(ufw status numbered | grep -F "Cloudflare-${APP_NAME}" | head -1 | sed -n 's/.*\[\s*\([0-9][0-9]*\)\].*//p' || true)"
    [[ -n "$num" ]] || break
    echo y | ufw delete "$num" >/dev/null 2>&1 || break
  done
  while IFS= read -r ip; do
    [[ -z "$ip" ]] && continue
    ufw allow from "$ip" to any port "$APP_PORT" proto tcp comment "Cloudflare-${APP_NAME}" >/dev/null || true
  done < <(printf '%s
%s
' "$V4" "$V6")
fi

"${APP_DIR}/scripts/docker-user-rules.sh"
docker service update --force --quiet "${STACK_NAME}_caddy" >/dev/null
echo "done"
UPDCF
  chmod 750 "$CF_UPDATE_SCRIPT"

  cat > "$CF_CRON" <<CRON
# Refresh the Cloudflare ranges for ${STACK_NAME} — day 1 of each month, 03:00
0 3 1 * * root ${CF_UPDATE_SCRIPT}
CRON
  chmod 644 "$CF_CRON"
  ok "Monthly refresh of the Cloudflare ranges: ${CF_CRON}"
else
  remove_cloudflare_lockdown
  warn "Docker publishes the port on its own chain. UFW covers the host; it does not replace a DOCKER-USER rule if this machine is exposed."
fi

# ==============================================================================
phase "PHASE 11 — Startup"
# ==============================================================================

info "Waiting for replicas (up to 3 minutes)..."
ready="n"
for _ in $(seq 1 30); do
  total="$(docker stack services "$STACK_NAME" --format '{{.Name}}' 2>/dev/null | wc -l | tr -d ' ')"
  # Whisper is left out: on the first start it downloads its model, which can take a while.
  pending="$(docker stack services "$STACK_NAME" --format '{{.Name}} {{.Replicas}}' 2>/dev/null \
    | grep -v "^${STACK_NAME}_whisper " | grep -vc ' 1/1' || true)"
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
if [[ "$INSTALL_MODE" == "certificate" ]]; then
  echo -e "  Certificate : ${BOLD}${CERT_LABEL}${NC}"
  echo -e "  ${DIM}Caddy reads it from ${APP_DIR}/etc/tls. Run the installer again to replace the file.${NC}"
fi
echo -e "  Admin     : ${BOLD}${ADMIN_EMAIL}${NC}"
echo -e "  ${DIM}There is no administrator password. The code arrives by email (or Mailpit, in dev).${NC}"
echo -e "  Directory : ${APP_DIR}"
echo -e "  Update    : ${APP_DIR}/scripts/update.sh"
echo -e "  Database  : ${POSTGRES_DB} / ${POSTGRES_USER}"
if [[ "$WHISPER_ENABLED" == "y" ]]; then
  echo -e "  Captions  : Whisper (${WHISPER_VARIANT^^}). On first start it picks and downloads its model;"
  echo -e "              ${DIM}the button to generate captions appears once it answers.${NC}"
  echo -e "              ${DIM}Follow it with: docker service logs -f ${STACK_NAME}_whisper${NC}"
else
  echo -e "  Captions  : upload or editor (run install.sh again to enable Whisper)"
fi
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
if [[ "$INSTALL_MODE" == "cloudflare" ]]; then
  echo ""
  echo -e "  ${BOLD}Cloudflare${NC} (dashboard, zone of ${PUBLIC_HOST})"
  echo -e "  - DNS: record for ${PUBLIC_HOST} pointing at this server, ${BOLD}proxied${NC} (orange cloud)."
  echo -e "  - SSL/TLS: ${BOLD}Flexible${NC} (for the zone, or a Configuration Rule for this hostname) —"
  echo -e "    the origin answers plain HTTP."
  echo -e "    Turn on ${BOLD}Always Use HTTPS${NC}: the application builds its links with https."
  case "$APP_PORT" in
    80|8080|8880|2052|2082|2086|2095)
      echo -e "  - Port ${APP_PORT} is one Cloudflare proxies as is; no Origin Rule needed." ;;
    *)
      echo -e "  - ${YELLOW}Origin Rule${NC}: hostname ${PUBLIC_HOST} → destination port ${APP_PORT}." ;;
  esac
  echo -e "  - Origin port ${APP_PORT} only answers Cloudflare (UFW + DOCKER-USER); ranges refreshed monthly."
  echo -e "  ${DIM}Serving video through Cloudflare's CDN is subject to their plan terms; check them for your volume.${NC}"
fi
sep
echo ""
