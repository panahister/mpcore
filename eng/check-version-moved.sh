#!/bin/bash
# Fails when shipped code changed since the published version that the source still declares.
#
# ADR-010: a version never names two sets of bytes. A package is a function of the commit it was built
# from, so a version names one commit: the one its published packages record (their nuspec carries the
# repository commit, written by Source Link). When the source still declares a published version and the
# shipped code differs from that commit, the next package built from it would carry the published number
# over different bytes. The cure is to move VersionPrefix to the next unused version.
#
#   ./eng/check-version-moved.sh               check this clone (needs the full history: fetch-depth 0)
#   ./eng/check-version-moved.sh --self-test   prove the check passes and fails for the right reasons
#
# Shipped code is everything that reaches a package of the cohort: the runtime sources, the CLI and the
# template (they ship under the same version, ADR-004 and ADR-010), the README packed into every runtime
# package, and the build and package-version files that decide what is compiled and which dependencies a
# package declares.
set -euo pipefail

SHIPPED=(src tools/MPCore.Cli tools/MPCore.Templates docs/nuget Directory.Build.props Directory.Packages.props)

# Prints the commit recorded by the published MPCore.Domain <version>, nothing when that version was never
# published, and fails when nuget.org cannot answer: an unknown state is never read as "unpublished".
# MPCORE_PUBLISHED_COMMITS names a file of "<version> <commit>" lines instead; the self-test uses it.
published_commit_of() {
  local version="$1"
  if [ -n "${MPCORE_PUBLISHED_COMMITS:-}" ]; then
    awk -v v="$version" '$1 == v { print $2 }' "$MPCORE_PUBLISHED_COMMITS"
    return 0
  fi

  local lower body status
  lower="$(printf '%s' "$version" | tr '[:upper:]' '[:lower:]')"
  body="$(mktemp)"
  status="$(curl -s -o "$body" -w '%{http_code}' "https://api.nuget.org/v3-flatcontainer/mpcore.domain/$lower/mpcore.domain.nuspec" || true)"
  case "$status" in
    200) sed -n 's/.*<repository[^>]* commit="\([0-9a-f]\{40\}\)".*/\1/p' "$body" | head -1; rm -f "$body" ;;
    404) rm -f "$body" ;;
    *) rm -f "$body"; echo "nuget.org answered '$status' for MPCore.Domain $version; cannot tell whether it is published" >&2; return 2 ;;
  esac
}

check() {
  local root="$1" declared commit changed
  declared="$(sed -n 's/.*<VersionPrefix>\(.*\)<\/VersionPrefix>.*/\1/p' "$root/src/Directory.Build.props" | head -1)"
  [ -n "$declared" ] || { echo "FAIL  no VersionPrefix in src/Directory.Build.props"; return 1; }

  commit="$(published_commit_of "$declared")" || return 1
  if [ -z "$commit" ]; then
    echo "PASS  $declared is not published; shipped changes go out under it"
    return 0
  fi

  if ! git -C "$root" cat-file -e "$commit^{commit}" 2>/dev/null; then
    echo "FAIL  $declared was published from $commit, which is not in this clone; fetch the full history"
    return 1
  fi

  changed="$(git -C "$root" diff --name-only "$commit" HEAD -- "${SHIPPED[@]}")"
  if [ -z "$changed" ]; then
    echo "PASS  $declared was published from $commit, and no shipped file changed since"
    return 0
  fi

  echo "FAIL  $declared was published from $commit, and shipped files changed since:"
  printf '%s\n' "$changed" | sed 's/^/        /'
  echo "      Move VersionPrefix to the next unused version in every place the cohort declares it (ADR-010)."
  return 1
}

self_test() {
  local work failed=0
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' RETURN
  git -C "$work" init -q
  git -C "$work" config user.email "self-test@example.invalid"
  git -C "$work" config user.name "self-test"
  mkdir -p "$work/src" "$work/docs"
  printf '<Project><PropertyGroup><VersionPrefix>1.0.0</VersionPrefix></PropertyGroup></Project>\n' > "$work/src/Directory.Build.props"
  printf 'class A {}\n' > "$work/src/A.cs"
  printf 'guide\n' > "$work/docs/guide.md"
  git -C "$work" add -A && git -C "$work" commit -q -m published
  printf '1.0.0 %s\n' "$(git -C "$work" rev-parse HEAD)" > "$work/published.txt"
  export MPCORE_PUBLISHED_COMMITS="$work/published.txt"

  expect() {
    local label="$1" wanted="$2" out rc
    out="$(check "$work" 2>&1)" && rc=0 || rc=$?
    if { [ "$wanted" = pass ] && [ "$rc" -eq 0 ]; } || { [ "$wanted" = fail ] && [ "$rc" -ne 0 ] && grep -q "shipped files changed" <<< "$out"; }; then
      echo "  PASS  $label"
    else
      echo "  FAIL  $label: expected $wanted, got exit $rc: $out"; failed=1
    fi
  }

  expect "the published commit itself" pass
  printf 'more guide\n' >> "$work/docs/guide.md"; git -C "$work" commit -qam "a document outside the packages"
  expect "a change outside the shipped paths" pass
  printf 'class B {}\n' >> "$work/src/A.cs"; git -C "$work" commit -qam "shipped code"
  expect "shipped code changed under the published version" fail
  sed -i.bak 's/1\.0\.0/1.0.1/' "$work/src/Directory.Build.props" && rm -f "$work/src/Directory.Build.props.bak"
  git -C "$work" commit -qam "the version moves"
  expect "the version moved to an unpublished one" pass

  unset MPCORE_PUBLISHED_COMMITS
  [ "$failed" -eq 0 ] && { echo "self-test PASSED"; return 0; }
  echo "self-test FAILED"; return 1
}

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ "${1:-}" = "--self-test" ]; then self_test; else check "$ROOT"; fi
