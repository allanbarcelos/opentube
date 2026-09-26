#!/usr/bin/env bash
# Computes the next A.B.C.D version tag for one component from the commits
# since its last tag, and creates and pushes that tag when a bump is warranted.
#
# Usage: bump-version.sh <prefix> <path> [path...]
#   prefix   tag prefix, e.g. "app" or "worker" -> tags look like "app-v1.2.3.4"
#   path     one or more git pathspecs the component is built from
#
# Version model (A.B.C.D). A bump resets every level to its right:
#   A — breaking change        : commit type "feat!"/"fix!"/…, or a "BREAKING CHANGE:" footer
#   B — new feature            : commit type "feat"
#   C — improvement            : commit type "improve" or "perf"
#   D — everything else        : fix, docs, chore, and any non-conventional subject
#
# Only one bump is applied per run, at the highest level found among the commits
# since this component's last tag.
#
# Prints the new tag on stdout. Prints nothing (and exits 0) when no commit
# touched the given paths since the last tag.
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

SUBJECTS=$(git log "$RANGE" --format='%s' -- "${PATHS[@]}" || true)
BODIES=$(git log "$RANGE" --format='%B' -- "${PATHS[@]}" || true)

if [ -z "$SUBJECTS" ]; then
  exit 0
fi

LEVEL="D"

while IFS= read -r subject; do
  [ -z "$subject" ] && continue

  if [[ "$subject" =~ ^[a-z]+(\([a-zA-Z0-9_,\ /-]+\))?!: ]]; then
    LEVEL="A"; break
  fi

  TYPE=$(echo "$subject" | sed -E 's/^([a-z]+)(\(.+\))?:.*/\1/')
  case "$TYPE" in
    feat)
      [ "$LEVEL" != "A" ] && LEVEL="B"
      ;;
    improve|perf)
      [ "$LEVEL" != "A" ] && [ "$LEVEL" != "B" ] && LEVEL="C"
      ;;
  esac
done <<< "$SUBJECTS"

if echo "$BODIES" | grep -q "BREAKING CHANGE:"; then
  LEVEL="A"
fi

case "$LEVEL" in
  A) A=$((A + 1)); B=0; C=0; D=0 ;;
  B) B=$((B + 1)); C=0; D=0 ;;
  C) C=$((C + 1)); D=0 ;;
  D) D=$((D + 1)) ;;
esac

NEW_TAG="${PREFIX}-v${A}.${B}.${C}.${D}"

git tag -a "$NEW_TAG" -m "Auto-bump (${LEVEL}) from ${LAST_TAG}"
git push origin "$NEW_TAG"

echo "$NEW_TAG"
