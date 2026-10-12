#!/bin/bash
# Restores the API-compatibility baseline into artifacts/packages, from nuget.org, and proves that each
# package is the one nuget.org published from the tagged commit.
#
# src/Directory.Build.props validates the API of every runtime package against its package of the version
# MPCoreBaselineVersion names, and a build fails when that package, or the record that it was checked, is
# missing or no longer matches (ADR-010, addendum of 2026-10-12). This script is the one way the package gets
# there, on a maintainer's machine, in CI and in the release workflow alike. For each runtime package:
#
#   1. it downloads <id>.<baseline>.nupkg from nuget.org's flat container, and from no other feed;
#   2. `dotnet nuget verify --all` accepts the file with a repository signature of nuget.org whose certificate
#      is one that nuget.org lists in its service index (RepositorySignatures), and no other;
#   3. the commit the package records (the nuspec's <repository commit>) is the commit of the tag v<baseline>;
#   4. then, and only then, it writes <id>.<baseline>.nupkg.verified, whose first line is the package's
#      SHA-256: the build compares it with the file, so a package replaced after the check fails the build.
#
# The restore of every other package keeps NuGet's default verification: in require mode, macOS refuses
# third-party packages whose signatures expired under a timestamping chain that macOS distrusts. Whether CI and
# the release run require mode for the whole restore is said in the release notes of each version.
#
# A package listed in MPCoreNewInCohort (src/Directory.Build.props) first ships in this cohort and has no
# baseline. The script proves nuget.org has none and writes <id>.<baseline>.new. When nuget.org has one, the
# listing would switch its validation off, so the script fails (STALE) and the build does too.
#
#   ./eng/restore-api-baseline.sh               restore and check every baseline package
#   ./eng/restore-api-baseline.sh --self-test   prove the check refuses a package for each reason it must
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/eng/package-ids.sh"

FLAT_CONTAINER="https://api.nuget.org/v3-flatcontainer"
SERVICE_INDEX="https://api.nuget.org/v3/index.json"

baseline_version() {
  sed -n 's/.*<MPCoreBaselineVersion>\(.*\)<\/MPCoreBaselineVersion>.*/\1/p' "$ROOT/src/Directory.Build.props" | head -1
}

lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

sha256_of() { shasum -a 256 "$1" | cut -d' ' -f1; }

is_new_in_cohort() {
  local id="$1" candidate
  for candidate in ${NEW_IN_COHORT[@]+"${NEW_IN_COHORT[@]}"}; do [ "$candidate" = "$id" ] && return 0; done
  return 1
}

# The SHA-256 fingerprints of the repository-signing certificates nuget.org lists, lower-case, one per line.
listed_fingerprints() {
  local resource
  resource="$(curl -fsSL "$SERVICE_INDEX" | python3 -I -c '
import json, sys
index = json.load(sys.stdin)
ids = [r["@id"] for r in index["resources"] if r["@type"].startswith("RepositorySignatures/5.0.0")]
print(ids[0] if ids else "")')"
  [ -n "$resource" ] || { echo "nuget.org's service index lists no RepositorySignatures/5.0.0 resource" >&2; return 1; }
  curl -fsSL "$resource" | python3 -I -c '
import json, sys
for certificate in json.load(sys.stdin)["signingCertificates"]:
    print(certificate["fingerprints"]["2.16.840.1.101.3.4.2.1"].lower())'
}

# The commit of the tag v<version>, or nothing when the tag is not in this clone.
tag_commit() {
  git -C "$ROOT" rev-parse -q --verify "refs/tags/v$1^{commit}" 2>/dev/null || true
}

# verify_package FILE ID VERSION EXPECTED_COMMIT FINGERPRINT...
# Prints why it refuses the file; returns 0 only when every check passes.
verify_package() {
  local file="$1" id="$2" version="$3" commit="$4" fingerprint out nuspec recorded_id recorded_version recorded_commit
  shift 4
  local args=()
  for fingerprint in "$@"; do args+=(--certificate-fingerprint "$fingerprint"); done

  if ! out="$(dotnet nuget verify --all "$file" ${args[@]+"${args[@]}"} -v n 2>&1)"; then
    echo "REFUSED $id $version: dotnet nuget verify --all: $(printf '%s\n' "$out" | grep -E -o 'NU[0-9]{4}: .*' | head -1)"
    return 1
  fi
  if ! grep -q '^Signature type: Repository' <<< "$out"; then
    echo "REFUSED $id $version: its primary signature is not a repository signature"
    return 1
  fi
  if ! grep -q '^Service index: https://api.nuget.org/v3/index.json' <<< "$out"; then
    echo "REFUSED $id $version: its repository signature does not name nuget.org's service index"
    return 1
  fi

  nuspec="$(unzip -p "$file" "$id.nuspec" 2>/dev/null || true)"
  recorded_id="$(sed -n 's/.*<id>\(.*\)<\/id>.*/\1/p' <<< "$nuspec" | head -1)"
  recorded_version="$(sed -n 's/.*<version>\(.*\)<\/version>.*/\1/p' <<< "$nuspec" | head -1)"
  recorded_commit="$(sed -n 's/.*<repository[^>]* commit="\([0-9a-f]\{40\}\)".*/\1/p' <<< "$nuspec" | head -1)"
  if [ "$(lower "$recorded_id")" != "$(lower "$id")" ] || [ "$recorded_version" != "$version" ]; then
    echo "REFUSED $id $version: the package is ${recorded_id:-unnamed} ${recorded_version:-without a version}"
    return 1
  fi
  if [ "$recorded_commit" != "$commit" ]; then
    echo "REFUSED $id $version: it records commit ${recorded_commit:-none}, and the tag v$version is $commit"
    return 1
  fi
  return 0
}

restore() {
  local version commit out incoming fingerprints=() fingerprint id dest stamp status
  local verified=0 reused=0 new=0 refused=0 stale=0
  version="$(baseline_version)"
  [ -n "$version" ] || { echo "no MPCoreBaselineVersion declared in src/Directory.Build.props" >&2; return 1; }
  commit="$(tag_commit "$version")"
  if [ -z "$commit" ]; then
    echo "the tag v$version is not in this clone; fetch it first: git fetch origin tag v$version" >&2
    return 1
  fi
  while IFS= read -r fingerprint; do [ -n "$fingerprint" ] && fingerprints+=("$fingerprint"); done < <(listed_fingerprints)
  [ "${#fingerprints[@]}" -gt 0 ] || { echo "nuget.org lists no repository-signing certificate" >&2; return 1; }

  out="$ROOT/artifacts/packages"
  incoming="$out/.incoming"
  mkdir -p "$incoming"
  echo "baseline $version, tag v$version at $commit, nuget.org repository certificates: ${fingerprints[*]}"

  for id in "${RUNTIME_IDS[@]}"; do
    if is_new_in_cohort "$id"; then
      status="$(curl -s -o /dev/null -w '%{http_code}' "$FLAT_CONTAINER/$(lower "$id")/$version/$(lower "$id").nuspec" || true)"
      case "$status" in
        404)
          printf 'nuget.org has no %s %s\n' "$id" "$version" > "$out/$id.$version.new"
          echo "new in this cohort, no $version baseline on nuget.org: $id"
          new=$((new + 1)) ;;
        200)
          echo "STALE: $id is listed as new in this cohort (MPCoreNewInCohort), but nuget.org has $version; its API would never be validated" >&2
          stale=$((stale + 1)) ;;
        *)
          echo "nuget.org answered '$status' for $id $version; cannot tell whether it is published" >&2
          return 1 ;;
      esac
      continue
    fi

    dest="$out/$id.$version.nupkg"
    stamp="$dest.verified"
    if [ -f "$dest" ] && [ -f "$stamp" ] && [ "$(sha256_of "$dest")" = "$(head -1 "$stamp")" ]; then
      echo "already verified: $id $version"
      reused=$((reused + 1))
      continue
    fi
    curl -fsSL "$FLAT_CONTAINER/$(lower "$id")/$version/$(lower "$id").$version.nupkg" -o "$incoming/$id.$version.nupkg"
    if verify_package "$incoming/$id.$version.nupkg" "$id" "$version" "$commit" "${fingerprints[@]}"; then
      mv -f "$incoming/$id.$version.nupkg" "$dest"
      {
        sha256_of "$dest"
        echo "signed by nuget.org (certificate fingerprints listed by its service index), records commit $commit, the tag v$version"
      } > "$stamp"
      echo "verified: $id $version"
      verified=$((verified + 1))
    else
      refused=$((refused + 1))
    fi
  done

  echo
  echo "baseline $version: $verified downloaded and verified, $reused already verified, $new new in this cohort, $refused refused, $stale stale"
  if [ "$stale" -gt 0 ]; then
    echo "$stale package(s) listed in MPCoreNewInCohort are published at $version: remove them from the list" >&2
  fi
  [ "$refused" -eq 0 ] && [ "$stale" -eq 0 ]
}

self_test() {
  local work version commit fingerprints=() fingerprint id=MPCore.Domain good failed=0 other
  work="$(mktemp -d)"
  trap 'rm -rf "${work:?}"' RETURN
  version="$(baseline_version)"
  commit="$(tag_commit "$version")"
  [ -n "$commit" ] || { echo "self-test FAILED: the tag v$version is not in this clone"; return 1; }
  while IFS= read -r fingerprint; do [ -n "$fingerprint" ] && fingerprints+=("$fingerprint"); done < <(listed_fingerprints)
  good="$work/$id.$version.nupkg"
  curl -fsSL "$FLAT_CONTAINER/$(lower "$id")/$version/$(lower "$id").$version.nupkg" -o "$good"

  # expect LABEL REASON FILE ID COMMIT FINGERPRINT...: refused, and for REASON; or accepted when REASON is "-".
  expect() {
    local label="$1" reason="$2" out rc
    shift 2
    out="$(verify_package "$1" "$2" "$version" "$3" "${@:4}")" && rc=0 || rc=$?
    if { [ "$reason" = "-" ] && [ "$rc" -eq 0 ]; } || { [ "$reason" != "-" ] && [ "$rc" -ne 0 ] && grep -q -- "$reason" <<< "$out"; }; then
      echo "  PASS  $label"
    else
      echo "  FAIL  $label: expected ${reason/#-/acceptance}, got exit $rc: $out"; failed=1
    fi
  }

  expect "the package nuget.org published from the tag is accepted" - "$good" "$id" "$commit" "${fingerprints[@]}"

  cp "$good" "$work/unsigned.nupkg"
  python3 -I - "$good" "$work/unsigned.nupkg" <<'PY'
import sys, zipfile
source, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(source) as original, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as rezipped:
    for entry in original.infolist():
        if entry.filename != ".signature.p7s":
            rezipped.writestr(entry, original.read(entry.filename))
PY
  expect "a baseline package re-zipped without its signature" "NU3004" "$work/unsigned.nupkg" "$id" "$commit" "${fingerprints[@]}"

  expect "a signature by a certificate nuget.org does not list" "NU3034" \
    "$good" "$id" "$commit" 0000000000000000000000000000000000000000000000000000000000000000

  other="$(printf '%s' "$commit" | tr '0-9a-f' '1-9a-f0')"
  expect "a baseline whose recorded commit is not the tag's" "records commit $commit, and the tag v$version is $other" \
    "$good" "$id" "$other" "${fingerprints[@]}"

  expect "a package of another id under this one's name" "the package is" "$good" MPCore.Application "$commit" "${fingerprints[@]}"

  [ "$failed" -eq 0 ] && { echo "self-test PASSED"; return 0; }
  echo "self-test FAILED"; return 1
}

if [ "${1:-}" = "--self-test" ]; then self_test; else restore; fi
