#!/usr/bin/env bash
# Tells whether any of the given paths changed between a base commit and HEAD.
# Markdown files never count: a README edit does not justify building an image.
#
# Usage: changed.sh <base-sha> <path> [path...]
#   base-sha  github.event.before on a push, the base commit on a pull request
#   path      git pathspecs the image is built from
#
# Prints "true" or "false". With no usable base (the branch's first push, or a
# rewritten history) it prints "true": better one build too many than a stale image.
set -euo pipefail

BASE="$1"
shift

if [[ -z "$BASE" || "$BASE" =~ ^0+$ ]] || ! git cat-file -e "${BASE}^{commit}" 2>/dev/null; then
  echo "true"
  exit 0
fi

if git diff --quiet "$BASE" HEAD -- "$@" ':(exclude)*.md'; then
  echo "false"
else
  echo "true"
fi
