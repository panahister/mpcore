#!/bin/bash
# Restores the API-compatibility baseline into artifacts/packages, from nuget.org.
#
# src/Directory.Build.props turns on API-compatibility validation for a package only when its
# baseline .nupkg is already sitting in the git-ignored artifacts/packages folder — true on a
# maintainer's machine after building the previous cohort, false on a clean checkout, where
# validation silently skips every package instead (a high-importance message says so, but nothing
# fails). This script makes a clean checkout, such as GitHub's runner, look like the machine that
# already has the baseline: the same packages, fetched from where they were published.
#
#   ./eng/restore-api-baseline.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/eng/package-ids.sh"

BASE_V="$(sed -n 's/.*<MPCoreBaselineVersion>\(.*\)<\/MPCoreBaselineVersion>.*/\1/p' "$ROOT/src/Directory.Build.props" | head -1)"
if [ -z "$BASE_V" ]; then
  echo "no MPCoreBaselineVersion declared in src/Directory.Build.props" >&2
  exit 1
fi

OUT="$ROOT/artifacts/packages"
mkdir -p "$OUT"

is_new_in_cohort() {
  local id="$1" candidate
  for candidate in ${NEW_IN_COHORT[@]+"${NEW_IN_COHORT[@]}"}; do [ "$candidate" = "$id" ] && return 0; done
  return 1
}

fetched=0
skipped=0
stale=0
for id in "${RUNTIME_IDS[@]}"; do
  if is_new_in_cohort "$id"; then
    # A package listed as new is skipped, so its API is never compared with anything. That is right only
    # while the baseline really does not exist: once it is published, the listing switches validation off
    # in silence, which is how four packages went unvalidated from 0.9.0 on.
    lower="$(printf '%s' "$id" | tr '[:upper:]' '[:lower:]')"
    status="$(curl -s -o /dev/null -w '%{http_code}' "https://api.nuget.org/v3-flatcontainer/$lower/$BASE_V/$lower.nuspec" || true)"
    if [ "$status" = "200" ]; then
      echo "STALE: $id is listed as new in this cohort (eng/package-ids.sh, NEW_IN_COHORT), but $BASE_V is published; its API would never be validated" >&2
      stale=$((stale + 1))
      continue
    fi

    echo "skip (new in this cohort, no $BASE_V baseline exists): $id"
    skipped=$((skipped + 1))
    continue
  fi
  lower="$(printf '%s' "$id" | tr '[:upper:]' '[:lower:]')"
  dest="$OUT/$id.$BASE_V.nupkg"
  if [ -f "$dest" ]; then
    echo "already present: $id $BASE_V"
  else
    echo "fetching $id $BASE_V"
    curl -fsSL "https://api.nuget.org/v3-flatcontainer/$lower/$BASE_V/$lower.$BASE_V.nupkg" -o "$dest"
  fi
  fetched=$((fetched + 1))
done

echo
echo "restored $fetched baseline package(s) at $BASE_V into $OUT, $skipped new in this cohort"
if [ "$stale" -gt 0 ]; then
  echo "$stale package(s) listed as new in this cohort are published at $BASE_V: remove them from NEW_IN_COHORT" >&2
  exit 1
fi
