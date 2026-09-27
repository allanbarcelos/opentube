#!/usr/bin/env bash
# Prepara a transcrição automática de legendas para o 'make watch': instala o whisper.cpp
# (whisper-cli) e baixa um modelo para .whisper/, conferindo o SHA-256 publicado.
#
# Uso: bash scripts/whisper.sh [modelo]      (padrão: small-q5_1)
#
# O scripts/dev-env.sh liga a transcrição sozinho quando encontra o whisper-cli e o
# .whisper/modelo.bin. Nada disso vai para o repositório nem para as imagens.
set -euo pipefail

MODELO="${1:-small-q5_1}"
PASTA=".whisper"
ORIGEM="https://huggingface.co/ggerganov/whisper.cpp/resolve/main"

# SHA-256 dos arquivos oficiais do whisper.cpp, na mesma tabela que a imagem de produção usa.
# Um modelo fora da lista é recusado: baixar sem conferir seria aceitar qualquer arquivo com esse nome.
TABELA="docker/whisper/modelos.txt"
SHA="$(awk -v m="$MODELO" '$1 == m { print $2 }' "$TABELA")"

if [[ -z "$SHA" ]]; then
  echo "Modelo desconhecido: ${MODELO}" >&2
  echo "Use um destes: $(awk '!/^#/ && NF { printf "%s ", $1 }' "$TABELA")" >&2
  exit 1
fi

# ── Programa ──────────────────────────────────────────────────────────────────
if command -v whisper-cli >/dev/null 2>&1; then
  echo "  whisper-cli já instalado: $(command -v whisper-cli)"
elif command -v brew >/dev/null 2>&1; then
  echo "  Instalando o whisper.cpp pelo Homebrew..."
  brew install whisper-cpp
else
  echo "  O whisper-cli não foi encontrado e não há Homebrew para instalá-lo." >&2
  echo "  Instale o whisper.cpp (https://github.com/ggml-org/whisper.cpp) e rode de novo." >&2
  exit 1
fi

# ── Modelo ────────────────────────────────────────────────────────────────────
mkdir -p "$PASTA"
ARQUIVO="${PASTA}/ggml-${MODELO}.bin"

confere() { [[ -f "$1" ]] && [[ "$(shasum -a 256 "$1" | cut -d' ' -f1)" == "$SHA" ]]; }

if confere "$ARQUIVO"; then
  echo "  Modelo ${MODELO} já baixado e conferido"
else
  echo "  Baixando o modelo ${MODELO}..."
  PARCIAL="${ARQUIVO}.parcial"
  curl -fL --retry 3 --progress-bar -o "$PARCIAL" "${ORIGEM}/ggml-${MODELO}.bin"

  if ! confere "$PARCIAL"; then
    rm -f "$PARCIAL"
    echo "  O arquivo baixado não confere com o SHA-256 publicado; nada foi instalado." >&2
    exit 1
  fi

  mv "$PARCIAL" "$ARQUIVO"
  echo "  Modelo conferido: ${ARQUIVO}"
fi

# O modelo em uso é um apelido fixo, para o dev-env.sh não precisar saber qual foi escolhido.
ln -sf "ggml-${MODELO}.bin" "${PASTA}/modelo.bin"

echo ""
echo "  Transcrição pronta com o modelo ${MODELO}. Reinicie o 'make watch' para o worker usá-la."
