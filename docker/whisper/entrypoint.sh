#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

# Sobe o servidor do Whisper ajustado à máquina em que está: detecta GPU NVIDIA, núcleos e
# memória disponíveis ao container, escolhe o modelo e as threads, baixa o modelo na primeira
# vez (conferindo o SHA-256) e publica o que escolheu em /info.json para o worker mostrar.
#
# Variáveis que mudam a escolha automática:
#   WHISPER_MODEL    nome do modelo (tabela em /opt/whisper/modelos.txt) ou "auto" (padrão)
#   WHISPER_THREADS  threads de CPU, ou "auto" (padrão)
#   WHISPER_DEVICE   "auto" (padrão), "gpu" ou "cpu"
set -euo pipefail

MODELOS_DIR="${WHISPER_MODELS_DIR:-/models}"
TABELA="/opt/whisper/modelos.txt"
PUBLICO="/tmp/whisper-public"
ORIGEM="${WHISPER_MODEL_SOURCE:-https://huggingface.co/ggerganov/whisper.cpp/resolve/main}"
PORTA="${WHISPER_PORT:-8080}"

log() { echo "[whisper] $*" >&2; }

# ── Hardware ──────────────────────────────────────────────────────────────────

# GPU NVIDIA: só conta se o driver responde de dentro do container (runtime nvidia).
tem_gpu() {
  [[ "${WHISPER_DEVICE:-auto}" != "cpu" ]] || return 1
  [[ -n "${WHISPER_CUDA:-}" ]] || return 1          # imagem sem CUDA
  command -v nvidia-smi >/dev/null 2>&1 || return 1
  nvidia-smi -L >/dev/null 2>&1
}

memoria_gpu_mb() {
  nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | head -1 | tr -dc '0-9'
}

# Núcleos que o container pode usar: o nproc respeita o cpuset; a cota do cgroup (--cpus,
# limits.cpus do Swarm) é conferida à parte, porque o nproc não a enxerga.
nucleos() {
  local n cota periodo limite
  n="$(nproc)"

  if [[ -r /sys/fs/cgroup/cpu.max ]]; then
    read -r cota periodo < /sys/fs/cgroup/cpu.max
    if [[ "$cota" != "max" && "$periodo" -gt 0 ]]; then
      limite=$(( (cota + periodo - 1) / periodo ))
      (( limite < n )) && n=$limite
    fi
  fi

  (( n < 1 )) && n=1
  echo "$n"
}

# Memória disponível ao container, em MB: o limite do cgroup ou, sem limite, a da máquina.
memoria_mb() {
  local total limite
  total=$(( $(awk '/^MemTotal:/ { print $2 }' /proc/meminfo) / 1024 ))

  if [[ -r /sys/fs/cgroup/memory.max ]]; then
    limite="$(cat /sys/fs/cgroup/memory.max)"
    if [[ "$limite" != "max" ]]; then
      limite=$(( limite / 1024 / 1024 ))
      (( limite < total )) && total=$limite
    fi
  fi

  echo "$total"
}

# Instruções vetoriais da CPU. Sem AVX2 (Celeron, Atom, Pentium Silver e VPS que escondem as
# instruções) o Whisper fica várias vezes mais lento: o modelo escolhido precisa ser mais leve.
simd() {
  local flags
  flags="$(grep -m1 '^flags' "${WHISPER_CPUINFO:-/proc/cpuinfo}" 2>/dev/null || true)"
  if   [[ " $flags " == *" avx512f "* ]]; then echo "avx512"
  elif [[ " $flags " == *" avx2 "* ]];    then echo "avx2"
  elif [[ " $flags " == *" avx "* ]];     then echo "avx"
  elif [[ " $flags " == *" sse4_2 "* ]];  then echo "sse4.2"
  else echo "basic"
  fi
}

# ── Escolhas ──────────────────────────────────────────────────────────────────

NUCLEOS="$(nucleos)"
MEMORIA="$(memoria_mb)"
SIMD="$(simd)"

if [[ "${WHISPER_DEVICE:-auto}" == "gpu" ]] && ! tem_gpu; then
  log "WHISPER_DEVICE=gpu, mas nenhuma GPU NVIDIA responde neste container; seguindo em CPU."
fi

if tem_gpu; then
  ACELERACAO="cuda"
  VRAM="$(memoria_gpu_mb)"
  log "GPU: $(nvidia-smi --query-gpu=name --format=csv,noheader | head -1) (${VRAM:-?} MB)"
else
  ACELERACAO="cpu"
  VRAM=0
fi

log "CPU: ${NUCLEOS} núcleos disponíveis · instruções: ${SIMD} · memória: ${MEMORIA} MB"

MODELO="${WHISPER_MODEL:-auto}"
if [[ "$MODELO" == "auto" ]]; then
  if [[ "$ACELERACAO" == "cuda" && "${VRAM:-0}" -ge 3000 ]]; then
    MODELO="large-v3-turbo-q5_0"      # na GPU, o mais preciso sai praticamente de graça
  elif [[ "$ACELERACAO" == "cuda" ]]; then
    MODELO="small-q5_1"
  elif [[ "$SIMD" != "avx2" && "$SIMD" != "avx512" ]]; then
    # Sem AVX2 o "small" gasta perto de 4 minutos por minuto de fala; o "base" fica perto do
    # tempo real. WHISPER_MODEL=small-q5_1 troca velocidade por precisão, se preferir.
    MODELO="base-q5_1"
    log "CPU sem AVX2: usando o modelo leve para a transcrição não demorar horas."
  elif (( NUCLEOS >= 8 && MEMORIA >= 4000 )); then
    MODELO="small"
  elif (( NUCLEOS >= 4 && MEMORIA >= 2000 )); then
    MODELO="small-q5_1"
  else
    MODELO="base-q5_1"                # máquina modesta: rápido e leve
  fi
fi

SHA="$(awk -v m="$MODELO" '$1 == m { print $2 }' "$TABELA")"
if [[ -z "$SHA" ]]; then
  log "Modelo desconhecido: ${MODELO}. Use um destes: $(awk '!/^#/ && NF { printf "%s ", $1 }' "$TABELA")"
  exit 1
fi

THREADS="${WHISPER_THREADS:-auto}"
if [[ "$THREADS" == "auto" ]]; then
  if [[ "$ACELERACAO" == "cuda" ]]; then
    THREADS=4                         # na GPU, a CPU só alimenta o decodificador
  else
    # Acima de ~8 threads o ganho some e a disputa com o FFmpeg do worker aumenta.
    THREADS=$(( NUCLEOS > 8 ? 8 : NUCLEOS ))
  fi
fi

# ── Modelo ────────────────────────────────────────────────────────────────────

mkdir -p "$MODELOS_DIR"
ARQUIVO="${MODELOS_DIR}/ggml-${MODELO}.bin"

confere() { [[ -f "$1" ]] && [[ "$(sha256sum "$1" | cut -d' ' -f1)" == "$SHA" ]]; }

if confere "$ARQUIVO"; then
  log "Modelo ${MODELO} já baixado e conferido."
else
  log "Baixando o modelo ${MODELO} (só na primeira vez; fica no volume)..."
  PARCIAL="${ARQUIVO}.parcial"

  until curl -fL --retry 5 --retry-delay 5 --retry-all-errors -sS -o "$PARCIAL" "${ORIGEM}/ggml-${MODELO}.bin"; do
    log "Falha no download; nova tentativa em 30 s."
    sleep 30
  done

  if ! confere "$PARCIAL"; then
    rm -f "$PARCIAL"
    log "O arquivo baixado não confere com o SHA-256 publicado; nada foi instalado."
    exit 1
  fi

  mv "$PARCIAL" "$ARQUIVO"
  log "Modelo conferido: ${ARQUIVO}"
fi

# ── Servidor ──────────────────────────────────────────────────────────────────

mkdir -p "$PUBLICO"
cat > "${PUBLICO}/info.json" <<JSON
{"acceleration":"${ACELERACAO}","simd":"${SIMD}","model":"${MODELO}","threads":${THREADS},"cores":${NUCLEOS},"memoryMb":${MEMORIA},"vramMb":${VRAM:-0}}
JSON

ARGS=(--host 0.0.0.0 --port "$PORTA" -m "$ARQUIVO" -t "$THREADS" -l auto --public "$PUBLICO")
[[ "$ACELERACAO" == "cpu" ]] && ARGS+=(-ng)

log "Iniciando: ${ACELERACAO^^} · ${MODELO} · ${THREADS} threads · detecção automática de idioma"
exec whisper-server "${ARGS[@]}"
