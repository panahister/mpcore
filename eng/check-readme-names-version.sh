#!/bin/bash
# Fails when README.md does not name the version about to be published.
#
# A reader installs what README names and reads the notes it links. A release that README does not name
# leaves every reader on the previous version, with notes that describe another cohort. So the release
# commit names its version first: every install line of the CLI and of the template, and every
# "Release notes" link, name that version, and the notes it links exist.
#
#   ./eng/check-readme-names-version.sh <version>     check this clone's README.md for a release version
#   ./eng/check-readme-names-version.sh --self-test   prove the check passes and fails for the right reasons
#
# A prerelease of main, <declared>-main.<n>, is not named in README, which names releases only; the release
# workflow checks releases and says so when it skips a prerelease.
set -euo pipefail

# Prints every version README names in one kind of line, one per line. The pattern's first group is the version.
named() {
  local readme="$1" pattern="$2"
  grep -oE "$pattern" "$readme" | sed -E "s#$pattern#\1#" || true
}

check() {
  local root="$1" version="$2" readme="$1/README.md" failed=0 label pattern versions other
  [ -f "$readme" ] || { echo "FAIL  no README.md in $root"; return 1; }

  while IFS='|' read -r label pattern; do
    versions="$(named "$readme" "$pattern")"
    if [ -z "$versions" ]; then
      echo "FAIL  README.md has no $label"
      failed=1
      continue
    fi

    other="$(printf '%s\n' "$versions" | grep -vxF -- "$version" | sort -u | tr '\n' ' ' || true)"
    if [ -n "$other" ]; then
      echo "FAIL  README.md names ${other% } in its $label, not $version"
      failed=1
    fi
  done <<'KINDS'
install line of the CLI|MPCore\.Cli --version ([0-9A-Za-z.+-]+)
install line of the template|MPCore\.Templates::([0-9A-Za-z.+-]+)
release-notes link|\[Release notes\]\(docs/releases/([0-9A-Za-z.+-]+)\.md\)
KINDS

  if [ ! -f "$root/docs/releases/$version.md" ]; then
    echo "FAIL  README.md links docs/releases/$version.md, which does not exist"
    failed=1
  fi

  if [ "$failed" -eq 0 ]; then
    echo "PASS  README.md names $version in its install lines and its release-notes links"
    return 0
  fi

  echo "      Name $version in README.md, in the commit that is released, before it is published."
  return 1
}

self_test() {
  local work failed=0
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' RETURN
  mkdir -p "$work/docs/releases"
  printf '# notes\n' > "$work/docs/releases/1.2.3.md"

  readme() {
    printf '%s\n' \
      "[Release notes](docs/releases/$1.md)" \
      '```bash' "dotnet tool install --global MPCore.Cli --version $2" '```' \
      '```bash' "dotnet new install MPCore.Templates::$3" '```' \
      "| [Release notes](docs/releases/$4.md) | See what changed |" > "$work/README.md"
  }

  expect() {
    local label="$1" wanted="$2" reason="${3:-}" out rc
    out="$(check "$work" 1.2.3 2>&1)" && rc=0 || rc=$?
    if { [ "$wanted" = pass ] && [ "$rc" -eq 0 ]; } || { [ "$wanted" = fail ] && [ "$rc" -ne 0 ] && grep -qF -- "$reason" <<< "$out"; }; then
      echo "  PASS  $label"
    else
      echo "  FAIL  $label: expected $wanted${reason:+ ($reason)}, got exit $rc: $out"; failed=1
    fi
  }

  readme 1.2.3 1.2.3 1.2.3 1.2.3
  expect "README names the version everywhere" pass
  readme 1.2.3 1.2.2 1.2.3 1.2.3
  expect "the CLI's install line names the previous version" fail "names 1.2.2 in its install line of the CLI"
  readme 1.2.3 1.2.3 1.2.2 1.2.3
  expect "the template's install line names the previous version" fail "names 1.2.2 in its install line of the template"
  readme 1.2.3 1.2.3 1.2.3 1.2.2
  expect "one of two release-notes links names the previous version" fail "names 1.2.2 in its release-notes link"
  printf 'dotnet tool install --global MPCore.Cli --version 1.2.3\ndotnet new install MPCore.Templates::1.2.3\n' > "$work/README.md"
  expect "README has no release-notes link" fail "has no release-notes link"
  readme 1.2.3 1.2.3 1.2.3 1.2.3
  rm "$work/docs/releases/1.2.3.md"
  expect "the notes README links do not exist" fail "which does not exist"

  [ "$failed" -eq 0 ] && { echo "self-test PASSED"; return 0; }
  echo "self-test FAILED"; return 1
}

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
case "${1:-}" in
  --self-test) self_test ;;
  "") echo "usage: $0 <version> | --self-test" >&2; exit 2 ;;
  *) check "$ROOT" "$1" ;;
esac
