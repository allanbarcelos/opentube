#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

# Computes the next A.B.C.D version tag for one component from the commits
# since its last tag, and creates and pushes that tag when a bump is warranted.
#
# Usage: bump-version.sh <prefix> <path> [path...]
#   prefix   tag prefix, e.g. "app" or "worker" -> tags look like "app-v1.2.3.4"
#   path     one or more git pathspecs the component is built from
#
# Version model (A.B.C.D). A bump resets every level to its right:
#   A — engine      : a change to the platform's core
#   B — feature     : a new capability
#   C — improvement : a change or refinement of something that already exists
#   D — bug fix     : anything else
#
# Each commit's level comes, in this order, from:
#   1. a "Tipo:" line in the message body (the project's convention, which keeps
#      the subject a plain sentence):
#        Tipo: motor | feature | melhoria | correção
#      English and unaccented spellings are accepted too (engine, improvement,
#      fix, correcao).
#   2. a Conventional Commits subject: "feat!:"/"fix!:"/… or a "BREAKING CHANGE:"
#      footer -> A; "feat:" -> B; "improve:"/"perf:" -> C.
#   3. otherwise, D.
#
# Only one bump is applied per run, at the highest level found among the commits
# since this component's last tag.
#
# Prints the new tag on stdout. Prints nothing (and exits 0) when no commit
# touched the given paths since the last tag. With BUMP_DRY_RUN=1 it prints the
# level of each commit and the tag it would create, without tagging or pushing.
set -euo pipefail

PREFIX="$1"
shift
PATHS=("$@")

LAST_TAG=$(git tag -l "${PREFIX}-v*" --sort=-v:refname | head -1)
if [ -z "$LAST_TAG" ]; then
  echo "::warning::No existing ${PREFIX}-v* tag found — falling back to ${PREFIX}-v1.0.0.0 as the baseline." >&2
  A=1; B=0; C=0; D=0
  RANGE="HEAD"
  LAST_TAG="(none — full history scan)"
else
  VERSION_PART="${LAST_TAG#${PREFIX}-v}"
  IFS='.' read -r A B C D <<< "$VERSION_PART"
  RANGE="${LAST_TAG}..HEAD"
fi

COMMITS=$(git log "$RANGE" --format='%H' -- "${PATHS[@]}" || true)

if [ -z "$COMMITS" ]; then
  exit 0
fi

# Level of a single commit, from its full message.
nivel_do_commit() {
  local mensagem="$1"
  local assunto tipo
  assunto=$(printf '%s\n' "$mensagem" | head -1)

  tipo=$(printf '%s\n' "$mensagem" \
    | grep -iE '^[[:space:]]*Tipo:' | tail -1 \
    | sed -E 's/^[[:space:]]*[Tt][Ii][Pp][Oo]:[[:space:]]*//; s/[[:space:]]+$//' \
    | tr '[:upper:]' '[:lower:]')

  case "$tipo" in
    motor|engine) echo A; return ;;
    feature|funcionalidade) echo B; return ;;
    melhoria|improvement) echo C; return ;;
    "correção"|correcao|fix|bugfix) echo D; return ;;
    "") ;;
    *) echo "::warning::Unknown 'Tipo: ${tipo}' in \"${assunto}\" — counted as a bug fix." >&2 ;;
  esac

  if [[ "$assunto" =~ ^[a-z]+(\([a-zA-Z0-9_,\ /-]+\))?!: ]] \
     || printf '%s\n' "$mensagem" | grep -q "^BREAKING CHANGE:"; then
    echo A; return
  fi

  case "$(printf '%s\n' "$assunto" | sed -nE 's/^([a-z]+)(\(.+\))?:.*/\1/p')" in
    feat) echo B ;;
    improve|perf) echo C ;;
    *) echo D ;;
  esac
}

ordem() { case "$1" in A) echo 4 ;; B) echo 3 ;; C) echo 2 ;; *) echo 1 ;; esac; }

LEVEL="D"

while IFS= read -r hash; do
  [ -z "$hash" ] && continue

  nivel=$(nivel_do_commit "$(git log -1 --format='%B' "$hash")")

  if [ "${BUMP_DRY_RUN:-}" = "1" ]; then
    echo "${nivel}  $(git log -1 --format='%h %s' "$hash")" >&2
  fi

  if [ "$(ordem "$nivel")" -gt "$(ordem "$LEVEL")" ]; then
    LEVEL="$nivel"
  fi
done <<< "$COMMITS"

case "$LEVEL" in
  A) A=$((A + 1)); B=0; C=0; D=0 ;;
  B) B=$((B + 1)); C=0; D=0 ;;
  C) C=$((C + 1)); D=0 ;;
  D) D=$((D + 1)) ;;
esac

NEW_TAG="${PREFIX}-v${A}.${B}.${C}.${D}"

if [ "${BUMP_DRY_RUN:-}" = "1" ]; then
  echo "${LEVEL} bump from ${LAST_TAG}" >&2
  echo "$NEW_TAG"
  exit 0
fi

git tag -a "$NEW_TAG" -m "Auto-bump (${LEVEL}) from ${LAST_TAG}"
git push origin "$NEW_TAG"

echo "$NEW_TAG"
