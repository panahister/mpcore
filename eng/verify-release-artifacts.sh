#!/bin/bash
# Verifies a frozen MP Core release directory by inspecting the actual .nupkg bytes.
#
# Repository source tests cannot see inside a package. The 0.2.0-alpha.2 defect shipped precisely
# because the source was correct and the packed bytes were not, so every assertion here is made
# against the archive that would be uploaded.
#
#   ./eng/verify-release-artifacts.sh <version> [release-dir]
#   ./eng/verify-release-artifacts.sh --self-test <version> [release-dir]
#
# --self-test proves the checker rejects bad artifacts AND that each case is rejected by the
# assertion it exists to exercise. A gate that has only ever been observed passing is not evidence,
# and a gate observed failing for the wrong reason is worse.
set -u -o pipefail

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/package-ids.sh"

EXPECTED_NUPKG=30
EXPECTED_SNUPKG=28
EXPECTED_FROZEN=58
# The manifest schema the CLI writes. Declared once so a bump is a single edit, not a hunt.
EXPECTED_MANIFEST_SCHEMA=4
TFM=net10.0
TEMPLATE_CONTENT="content/content/MPCore.Backend"

FAILURES=0

# sed in place, the same on macOS and on Linux. BSD sed wants a suffix after -i and GNU sed wants none
# unless it is attached, so the one form both accept is an attached suffix; the copy it leaves is removed.
sedi() { local file="${!#}"; sed -i.sedi "$@" && rm -f "$file.sedi"; }
ok()   { printf '  PASS  %s\n' "$*"; }
bad()  { printf '  FAIL  %s\n' "$*"; FAILURES=$((FAILURES + 1)); }
info() { printf '  INFO  %s\n' "$*"; }
head_() { printf '\n== %s ==\n' "$*"; }
expect_eq() { if [ "$2" = "$3" ]; then ok "$1 = $3"; else bad "$1 = $2 (expected $3)"; fi; }

# Comments name APIs in order to explain why they are NOT used, and commented-out code satisfies a
# naive grep while doing nothing at runtime. Both line-comment forms are stripped, so an assertion
# can never be satisfied by text that the compiler ignores.
code_only() { grep -v '^[[:space:]]*//' "$1"; }
# grep -c counts matching LINES. Two calls on one line are one line, which is a one-newline bypass
# for any "how many times does this appear" assertion. Everything below counts occurrences.
count_in() { printf '%s' "$2" | grep -o "$1" | grep -c . ; }

verify_release() {
  local V="$1" D="$2"
  FAILURES=0
  [ -d "$D" ] || { bad "release directory not found: $D"; return 1; }

  head_ "frozen hashes"
  if [ -f "$D/SHA256SUMS.txt" ]; then
    # The exit status is the assertion. Parsing shasum's text would make the gate depend on wording
    # and on grep dialect; a mis-typed pattern there would silently pass tampered bytes.
    local out; out="$(cd "$D" && shasum -a 256 -c SHA256SUMS.txt 2>&1)"
    if [ $? -eq 0 ]; then
      ok "every frozen artifact matches SHA256SUMS.txt"
    else
      bad "SHA256SUMS mismatch:"; printf '%s\n' "$out" | grep -v ': OK$' | sed 's/^/        /'
    fi
    # shasum -c only checks the files it is given, so an unlisted artifact could be swapped freely.
    # A line count is not coverage: a dropped entry plus a duplicated one keeps the count. The
    # manifest's filename set must equal the directory's artifact set exactly, with no duplicates.
    local listed present dupes
    listed="$(awk '{print $2}' "$D/SHA256SUMS.txt" | sed 's|^\*||' | sort)"
    present="$(find "$D" -type f \( -name '*.nupkg' -o -name '*.snupkg' \) -exec basename {} \; | sort)"
    dupes="$(printf '%s\n' "$listed" | uniq -d)"
    if [ "$listed" = "$present" ] && [ -z "$dupes" ]; then
      ok "frozen manifest covers every artifact in the directory, with no duplicate entries"
    else
      bad "frozen manifest does not match the directory:"
      [ -n "$dupes" ] && printf '%s\n' "$dupes" | sed 's/^/        duplicate: /'
      diff <(echo "$listed") <(echo "$present") | sed 's/^/        /'
    fi
    expect_eq "frozen manifest entries" "$(grep -c . "$D/SHA256SUMS.txt")" "$EXPECTED_FROZEN"
    # Anything else in the release directory is unaccounted for and must not travel with a release.
    expect_eq "unmanifested files in the release directory" \
      "$(find "$D" -type f ! -name 'SHA256SUMS.txt' ! -name '*.nupkg' ! -name '*.snupkg' | grep -c .)" "0"
  else
    bad "SHA256SUMS.txt missing - artifacts are not frozen"
  fi

  head_ "package inventory"
  expect_eq "nupkg count"  "$(find "$D" -name '*.nupkg' | wc -l | tr -d ' ')"  "$EXPECTED_NUPKG"
  expect_eq "snupkg count" "$(find "$D" -name '*.snupkg' | wc -l | tr -d ' ')" "$EXPECTED_SNUPKG"
  expect_eq "packages of another version" \
    "$(find "$D" -name '*.nupkg' ! -name "*.$V.nupkg" | wc -l | tr -d ' ')" "0"
  local expected_ids actual_ids
  expected_ids="$(printf '%s\n' "${RUNTIME_IDS[@]}" "${TOOL_IDS[@]}" | sort)"
  actual_ids="$(find "$D" -name "*.$V.nupkg" -exec basename {} \; | sed "s/\.$V\.nupkg$//" | sort)"
  if [ "$expected_ids" = "$actual_ids" ]; then ok "package id set is exactly the expected $EXPECTED_NUPKG ids"
  else bad "package id set differs:"; diff <(echo "$expected_ids") <(echo "$actual_ids") | sed 's/^/        /'; fi

  head_ "every package: identity, payload and cohort dependency pinning"
  # Identity alone is what the 0.2.0-alpha.2 defect satisfied. A correct id and version with the
  # wrong payload is the defect class, so every package is opened, not only the two tooling ones.
  local id pkg nv payload deps offending bad_ident=0 bad_payload=0 bad_deps=0
  for id in "${RUNTIME_IDS[@]}" "${TOOL_IDS[@]}"; do
    pkg="$D/$id.$V.nupkg"
    [ -f "$pkg" ] || { bad "$id: package missing"; bad_ident=1; continue; }
    nv="$(unzip -p "$pkg" "$id.nuspec" 2>/dev/null | sed -n 's/.*<version>\(.*\)<\/version>.*/\1/p' | head -1)"
    [ "$nv" = "$V" ] || { bad "$id nuspec version = $nv (expected $V)"; bad_ident=1; }
    # Cohort integrity: every MPCore dependency must pin this exact version. A package depending on
    # a superseded cohort member silently reintroduces mixed-cohort consumption.
    offending="$(unzip -p "$pkg" "$id.nuspec" 2>/dev/null \
      | grep -oE '<dependency id="MPCore[^"]*" version="[^"]*"' \
      | grep -v "version=\"$V\"" || true)"
    [ -z "$offending" ] || { bad "$id has MPCore dependencies not pinned to $V:"; printf '%s\n' "$offending" | sed 's/^/        /'; bad_deps=1; }
    case " ${TOOL_IDS[*]} " in
      *" $id "*) : ;;  # tooling packages carry a tool/template payload, asserted separately
      *)
        payload="$(unzip -l "$pkg" 2>/dev/null | grep -c "lib/$TFM/$id\.dll")"
        if [ "$payload" != "1" ]; then
          bad "$id is missing lib/$TFM/$id.dll"; bad_payload=1
        else
          # A correct filename over a different assembly is the 0.2.0-alpha.2 defect class, so the
          # payload is opened and its own identity is read. The assembly name lives in the metadata
          # #Strings heap as UTF-8; a substituted assembly does not carry this package's name.
          local asm; asm="$(unzip -p "$pkg" "lib/$TFM/$id.dll" 2>/dev/null | LC_ALL=C tr -d '\000' | LC_ALL=C grep -a -c "$id")"
          [ "${asm:-0}" -gt 0 ] || { bad "$id: lib/$TFM/$id.dll does not identify itself as $id (substituted payload)"; bad_payload=1; }
        fi ;;
    esac
  done
  local bad_sym=0 snup
  for id in "${RUNTIME_IDS[@]}"; do
    snup="$D/$id.$V.snupkg"
    [ -f "$snup" ] || { bad "$id: symbol package missing"; bad_sym=1; continue; }
    [ "$(unzip -l "$snup" 2>/dev/null | grep -c "\.pdb")" -gt 0 ] || { bad "$id: symbol package carries no .pdb"; bad_sym=1; }
  done
  [ "$bad_sym" -eq 0 ]     && ok "all ${#RUNTIME_IDS[@]} symbol packages carry a .pdb"
  [ "$bad_ident" -eq 0 ]   && ok "all $EXPECTED_NUPKG nuspec versions = $V"
  [ "$bad_payload" -eq 0 ] && ok "all ${#RUNTIME_IDS[@]} runtime packages carry lib/$TFM/<id>.dll"
  [ "$bad_deps" -eq 0 ]    && ok "every MPCore dependency in every package is pinned to $V"

  # ---------------- packed template ----------------
  local T; T="$(mktemp -d)"
  unzip -qo "$D/MPCore.Templates.$V.nupkg" -d "$T" 2>/dev/null || bad "MPCore.Templates package is not readable"
  local PROG="$T/$TEMPLATE_CONTENT/src/MPCore.Backend.Api/Program.cs"
  local TJSON="$T/$TEMPLATE_CONTENT/.template.config/template.json"
  local MARKER="$T/$TEMPLATE_CONTENT/.mpcore-template-version"

  head_ "packed template: authorization API"
  if [ -f "$PROG" ]; then
    # Compiled code only, and newlines collapsed so a line break inside a call cannot hide it.
    local CODE FLAT
    CODE="$(code_only "$PROG")"
    FLAT="$(printf '%s' "$CODE" | tr '\n' ' ')"
    expect_eq "parameterless AddMPCoreAuthorization();" "$(count_in 'AddMPCoreAuthorization();' "$FLAT")" "1"
    expect_eq "AddMPCoreAuthorization(<callback>)"      "$(count_in 'AddMPCoreAuthorization([^)]' "$FLAT")" "0"
    # Member access, not a lambda-parameter name: renaming `options` to `opts` must not evade it.
    expect_eq "any .AllowAnonymousHealthEndpoints assignment" \
      "$(count_in '\.AllowAnonymousHealthEndpoints' "$FLAT")" "0"
    expect_eq "host reads Security:AllowAnonymousHealthEndpoints" \
      "$(count_in 'Security:AllowAnonymousHealthEndpoints' "$FLAT")" "1"

    head_ "packed template: host-owned anonymous health and default protection"
    # Occurrences, not lines: two calls on one line are two anonymous endpoints, not one.
    expect_eq "total .AllowAnonymous( calls" "$(count_in '\.AllowAnonymous(' "$FLAT")" "6"
    # AllowAnonymous can also be applied as endpoint metadata, which contains no .AllowAnonymous(
    # text at all, so counting calls alone would miss it entirely.
    expect_eq "AllowAnonymous applied as metadata" "$(count_in 'AllowAnonymousAttribute' "$FLAT")" "0"
    local ep
    for ep in grpcHealthEndpoint livenessEndpoint readinessEndpoint startupEndpoint openApiDocument grpcReflection; do
      expect_eq "$ep is anonymous" "$(count_in "$ep\.AllowAnonymous(" "$FLAT")" "1"
    done
    # The description surfaces may be anonymous only in Development, gated by the environment itself.
    expect_eq "description surfaces gated by the environment" \
      "$(count_in 'var anonymousDescriptionSurface = builder.Environment.IsDevelopment();' "$FLAT")" "1"
    expect_eq "AllowAnonymous on description surfaces sits inside the gate" \
      "$(count_in 'if (anonymousDescriptionSurface)' "$FLAT")" "2"
    for ep in grpcProbeEndpoint restProbeEndpoints; do
      expect_eq "$ep is NOT anonymous" "$(count_in "$ep\.AllowAnonymous" "$FLAT")" "0"
    done

    head_ "packed template: application execution contract (ADR-011)"
    expect_eq "handler discovery is explicit"        "$(count_in 'options.DiscoverHandlersIn(HandlerAssemblies.All);' "$FLAT")" "1"
    expect_eq "no implicit scanning helper"          "$(count_in 'Discovery.IncludeAssembly\|DiscoverHandlerModules\|CustomizeHandlerDiscovery' "$FLAT")" "0"
    expect_eq "one named transaction owner"          "$(count_in 'UseMPCoreWolverine<AppDbContext>(' "$FLAT")" "1"
    expect_eq "no unowned Wolverine composition"     "$(count_in 'builder.Host.UseMPCoreWolverine(' "$FLAT")" "0"

    head_ "packed template: transport separation"
    expect_eq "obsolete RequireHost port enforcement" "$(count_in 'RequireHost' "$FLAT")" "0"
    expect_eq "RequireListenerPort bindings"          "$(count_in 'RequireListenerPort' "$FLAT")" "9"
    expect_eq "UseTransportPortSeparation registered" "$(count_in 'app.UseTransportPortSeparation();' "$FLAT")" "1"
    expect_eq "UseAuthorization registered"           "$(count_in 'app.UseAuthorization();' "$FLAT")" "1"
    expect_eq "UseAuthentication registered"          "$(count_in 'app.UseAuthentication();' "$FLAT")" "1"
    # Each endpoint family must be bound individually; a bare total could be reached by six bindings
    # on one endpoint while another is left reachable from either listener.
    for ep in grpcProbeEndpoint grpcHealthEndpoint restProbeEndpoints livenessEndpoint readinessEndpoint startupEndpoint; do
      expect_eq "$ep is bound to a listener port" "$(count_in "$ep\.RequireListenerPort(" "$FLAT")" "1"
    done
    # Optional surfaces are bound too, so under `both` a description endpoint answers on one port only.
    expect_eq "openApiDocument bound to the REST listener" "$(count_in 'openApiDocument?\.RequireListenerPort(restPort)' "$FLAT")" "1"
    expect_eq "grpcReflection bound to the gRPC listener"  "$(count_in 'grpcReflection?\.RequireListenerPort(grpcPort)' "$FLAT")" "1"
    local SEP="$T/$TEMPLATE_CONTENT/src/MPCore.Backend.Api/Hosting/TransportPortSeparation.cs"
    if [ -f "$SEP" ]; then
      local SEPCODE; SEPCODE="$(code_only "$SEP" | tr '\n' ' ')"
      expect_eq "separation reads the accepting socket port" "$(count_in 'Connection\.LocalPort' "$SEPCODE")" "1"
      expect_eq "separation never reads the Host header"     "$(count_in 'RequireHost(' "$SEPCODE")" "0"
    else
      bad "TransportPortSeparation.cs is not in the package"
    fi
  else
    bad "packed template host Program.cs not found at $TEMPLATE_CONTENT"
  fi

  head_ "packed template: AI assistant tooling"
  # Codex discovers skills at .agents/skills and Claude Code at .claude/skills, so the package must
  # carry both layouts over one body. Guidance that does not survive packaging does not exist.
  local AI="$T/$TEMPLATE_CONTENT"
  local canon adapters_a adapters_c
  canon="$(find "$AI/.mpcore/skills" -mindepth 1 -maxdepth 1 -type d -exec basename {} \; 2>/dev/null | sort)"
  adapters_a="$(find "$AI/.agents/skills" -mindepth 1 -maxdepth 1 -type d -exec basename {} \; 2>/dev/null | sort)"
  adapters_c="$(find "$AI/.claude/skills" -mindepth 1 -maxdepth 1 -type d -exec basename {} \; 2>/dev/null | sort)"
  expect_eq "canonical skill bodies packed" "$(printf '%s\n' "$canon" | grep -c .)" "10"
  if [ "$canon" = "$adapters_a" ] && [ "$canon" = "$adapters_c" ]; then
    ok "Codex and Claude adapters cover exactly the canonical skills"
  else
    bad "skill adapter drift between .mpcore/skills, .agents/skills and .claude/skills"
  fi
  local sk missing_ptr=0 fat=0 crosswired=0 badfront=0 adapter rel target body_name body_desc
  for sk in $canon; do
    [ -f "$AI/.mpcore/skills/$sk/SKILL.md" ] || { bad "canonical body missing for $sk"; continue; }
    body_name="$(sed -n 's/^name: //p'        "$AI/.mpcore/skills/$sk/SKILL.md" | head -1)"
    body_desc="$(sed -n 's/^description: //p' "$AI/.mpcore/skills/$sk/SKILL.md" | head -1)"
    for adapter in "$AI/.agents/skills/$sk/SKILL.md" "$AI/.claude/skills/$sk/SKILL.md"; do
      [ -f "$adapter" ] || { missing_ptr=1; continue; }
      # The link must resolve from the adapter's own directory, and it must resolve to THIS skill's
      # body. A link that resolves to another skill sends the assistant to the wrong procedure.
      rel="$(grep -o '\.\./[^)`]*SKILL\.md' "$adapter" 2>/dev/null | head -1)"
      if [ -n "$rel" ] && [ -f "$(dirname "$adapter")/$rel" ]; then
        case "$rel" in *"/$sk/SKILL.md") : ;; *) crosswired=1 ;; esac
      else
        missing_ptr=1
      fi
      # Both runtimes key discovery off frontmatter. An adapter without it, or with a description
      # that disagrees with the body, is either invisible or selected for the wrong task.
      head -1 "$adapter" | grep -q '^---$' || badfront=1
      [ "$(sed -n 's/^name: //p'        "$adapter" | head -1)" = "$body_name" ] || badfront=1
      [ "$(sed -n 's/^description: //p' "$adapter" | head -1)" = "$body_desc" ] || badfront=1
      # An adapter that grew a body is a second thing to maintain and the one that gets forgotten.
      [ "$(wc -c < "$adapter")" -lt "$(wc -c < "$AI/.mpcore/skills/$sk/SKILL.md")" ] || fat=1
    done
  done
  expect_eq "every adapter resolves to its own canonical body" "$missing_ptr" "0"
  expect_eq "no adapter is cross-wired to another skill"       "$crosswired" "0"
  expect_eq "every adapter carries frontmatter matching its body" "$badfront" "0"
  expect_eq "no adapter carries a duplicated body"             "$fat" "0"
  for f in AGENTS.md CLAUDE.md README.md docs/getting-started.md docs/development-workflow.md docs/architecture.md docs/capabilities.md docs/ai-skills.md docs/examples/README.md .mpcore/skills/INVENTORY.md .mpcore/skills/BUNDLE.json; do
    [ -f "$AI/$f" ] && ok "packed: $f" || bad "missing from the package: $f"
  done
  # A generated repository consumes MP Core; it must not carry procedures for changing or publishing it.
  for forbidden in mpcore-evolve-package mpcore-release mpcore-scaffold-backend; do
    expect_eq "framework-maintenance skill absent: $forbidden" \
      "$(find "$AI/.mpcore/skills" -maxdepth 1 -name "$forbidden" | grep -c .)" "0"
  done
  # Guidance pointing at one developer's disk, a plugin cache or the governance repository does not travel.
  local leak
  leak="$(grep -rlE '/Users/|C:\\\\|\.codex/plugins|Payment/Governance|READY_FOR_DEV|Ganjineh|SanaCash|IDR' \
    "$AI/.mpcore" "$AI/.agents" "$AI/.claude" "$AI/AGENTS.md" "$AI/CLAUDE.md" 2>/dev/null | grep -c .)"
  expect_eq "machine-specific or product-specific references in shipped guidance" "$leak" "0"
  # Every MPCORE_ token in shipped guidance must be one the template actually substitutes; an unknown
  # token ships unresolved into a generated repository.
  local unknown_tokens
  unknown_tokens="$(grep -rhoE 'MPCORE_[A-Z_]+' "$AI/.mpcore" "$AI/.agents" "$AI/.claude" "$AI/AGENTS.md" "$AI/CLAUDE.md" 2>/dev/null \
    | sort -u | grep -vE '^MPCORE_(ORGANIZATION|COMPONENT|VERSION|TEMPLATE_VERSION|AI_TOOLING|ORGLOWER|COMPONENTLOWER)$' | grep -c .)"
  expect_eq "unknown MPCORE_ tokens in shipped guidance" "$unknown_tokens" "0"
  expect_eq "bundle declares its version and the selected tooling" \
    "$(grep -cE 'MPCORE_TEMPLATE_VERSION|MPCORE_AI_TOOLING' "$AI/.mpcore/skills/BUNDLE.json")" "2"

  head_ "template marker: packaging time vs generation time"
  # At PACKAGING time the marker must still be the unresolved token: dotnet new substitutes it when a
  # project is generated. A packed marker already holding a version would mean the substitution ran at
  # the wrong stage and the CLI gate would compare a baked-in constant instead of the real one.
  if [ -f "$MARKER" ]; then
    expect_eq "packed marker is the unresolved token" "$(tr -d '[:space:]' < "$MARKER")" "MPCORE_TEMPLATE_VERSION"
  else
    bad "template version marker is missing from the package"
  fi
  if [ -f "$TJSON" ]; then
    # Queried as JSON. A sed line window depends on indentation, so reformatting the file - or
    # placing a decoy "isRequired": true in a neighbouring symbol - can widen it past its own
    # closing brace and answer for the wrong symbol.
    tj() { python3 -c 'import json,sys;d=json.load(open(sys.argv[1]))["symbols"];print(eval(sys.argv[2],{},{"s":d}))' "$TJSON" "$1" 2>/dev/null; }
    expect_eq "templateVersion default (what the marker resolves to)" "$(tj 's["templateVersion"]["defaultValue"]')" "$V"
    expect_eq "mpcoreVersion default (runtime pins in generated projects)" "$(tj 's["mpcoreVersion"]["defaultValue"]')" "$V"
    expect_eq "transport is the required choice" "$(tj 's["transport"].get("isRequired") is True')" "True"
    expect_eq "transport has no default" "$(tj '"defaultValue" not in s["transport"]')" "True"
    expect_eq "businessAudit defaults to none (an audit trail is chosen, never implied)" "$(tj 's["businessAudit"]["defaultValue"]')" "none"
    expect_eq "businessAudit choices" "$(tj '[c["choice"] for c in s["businessAudit"]["choices"]]')" "['none', 'postgresql']"
    expect_eq "cache defaults to memory (the only choice without a server)" "$(tj 's["cache"]["defaultValue"]')" "memory"
    expect_eq "cache choices" "$(tj '[c["choice"] for c in s["cache"]["choices"]]')" "['none', 'memory', 'redis', 'hybrid']"
    expect_eq "timeseries defaults to none" "$(tj 's["timeseries"]["defaultValue"]')" "none"
  else
    bad "template.json is not in the package"
  fi

  # ---------------- packed CLI ----------------
  local C; C="$(mktemp -d)"
  unzip -qo "$D/MPCore.Cli.$V.nupkg" -d "$C" 2>/dev/null || bad "MPCore.Cli package is not readable"

  head_ "packed CLI assembly"
  local DLL; DLL="$(find "$C" -name 'MPCore.Cli.dll' | head -1)"
  if [ -n "$DLL" ]; then
    # Assembly string literals are UTF-16, so a plain ASCII grep silently finds nothing and would
    # pass a gutted binary. These are presence assertions over literals: they prove the gate's
    # strings ship, not that the comparison is correct. Gate BEHAVIOUR is proven by generating
    # against a mismatched template and observing exit 4 (see ADR-010).
    local CLI_STRINGS; CLI_STRINGS="$(LC_ALL=C tr -d '\000' < "$DLL")"
    grep_lit() { printf '%s' "$CLI_STRINGS" | LC_ALL=C grep -a -c "$1"; }
    expect_eq "assembly contains cohort version $V"          "$(grep_lit "$V" | awk '{print ($1>0)?"yes":"no"}')" "yes"
    expect_eq "assembly contains the mismatch message"       "$(grep_lit 'Template/CLI version mismatch')" "1"
    expect_eq "assembly contains the marker filename"        "$(grep_lit 'mpcore-template-version' | awk '{print ($1>0)?"yes":"no"}')" "yes"
    # Presence, not a line count: the literal legitimately appears in both the generation gate and
    # configure, and how many "lines" a binary splits into is layout, not meaning.
    expect_eq "assembly contains the reinstall remediation"  "$(grep_lit 'dotnet new uninstall MPCore.Templates' | awk '{print ($1>0)?"yes":"no"}')" "yes"
  else
    bad "MPCore.Cli.dll not found in the package"
  fi

  head_ "packed CLI documentation"
  local RM="$C/README.md"
  if [ -f "$RM" ]; then
    expect_eq "README documents version $V"             "$(grep -cF "$V" "$RM" | awk '{print ($1>0)?"yes":"no"}')" "yes"
    expect_eq "README documents manifest schema $EXPECTED_MANIFEST_SCHEMA" \
      "$(grep -c "\"schemaVersion\": $EXPECTED_MANIFEST_SCHEMA" "$RM")" "1"
    expect_eq "README documents no superseded schema" \
      "$(grep -cE '"schemaVersion": [0-9]+' "$RM" | awk -v n=1 '{print ($1>n)?"extra":"none"}')" "none"
    expect_eq "README documents the exit 4 gate"        "$(grep -c 'exits `4`' "$RM")" "1"
    local stale; stale="$(grep -oE '0\.2\.0-alpha\.[0-9]+' "$RM" | sort -u | grep -v "^$V$" | tr '\n' ' ')"
    expect_eq "README carries no superseded version" "${stale:-none}" "none"
  else
    bad "MPCore.Cli package has no README.md"
  fi

  head_ "API compatibility baseline (build-machine context)"
  # This is the one check that is NOT a property of the frozen artifacts: it inspects the build
  # machine's baseline store, which is git-ignored. On a clean clone it is simply unavailable, and
  # reporting that as a failure would make a correct release unverifiable by a second party. It is
  # therefore advisory when absent and a hard failure only when present-but-incomplete, which is the
  # case that would mean validation really did skip packages.
  local ROOT BASE_V missing=0 present=0
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
  BASE_V="$(sed -n 's/.*<MPCoreBaselineVersion>\(.*\)<\/MPCoreBaselineVersion>.*/\1/p' "$ROOT/src/Directory.Build.props" 2>/dev/null | head -1)"
  if [ -z "$BASE_V" ]; then
    info "no MPCoreBaselineVersion declared in this tree - baseline context not evaluated"
  elif [ ! -d "$ROOT/artifacts/packages" ]; then
    info "baseline store absent (git-ignored): API-compatibility context cannot be judged from a clean clone; verify on the build machine"
  else
    local new=0
    for id in "${RUNTIME_IDS[@]}"; do
      if [ -f "$ROOT/artifacts/packages/$id.$BASE_V.nupkg" ]; then present=$((present+1))
      elif printf '%s\n' "${NEW_IN_COHORT[@]}" | grep -qx "$id"; then info "new in this cohort, no $BASE_V baseline exists: $id"; new=$((new+1))
      else bad "baseline missing: $id.$BASE_V.nupkg"; missing=1; fi
    done
    [ "$missing" -eq 0 ] && ok "$present runtime baselines present at $BASE_V, $new new in this cohort (validation had a baseline wherever one can exist)"
  fi

  rm -rf "$T" "$C"
  printf '\n== result ==\n'
  if [ "$FAILURES" -eq 0 ]; then printf '  VERIFIED: %s in %s\n' "$V" "$D"; return 0; fi
  printf '  REJECTED: %s failed check(s)\n' "$FAILURES"; return 1
}

# Requires rejection AND rejection for the stated reason.
assert_case() {
  local label="$1" dir="$2" V="$3"; shift 3
  local out rc pattern missing=0
  out="$(verify_release "$V" "$dir" 2>&1)"; rc=$?
  if [ "$rc" -eq 0 ]; then printf '  FAIL  %s was ACCEPTED\n' "$label"; return 1; fi
  for pattern in "$@"; do
    # A here-string, not a pipe: under `set -o pipefail`, `grep -q` exits at the first match and the
    # writer dies of SIGPIPE (141), so the pipeline reported "not rejected for this reason" even when
    # the reason was in the output. Observed 2026-09-10 on 0.2.0-alpha.9 with every case failing.
    grep -q -- "$pattern" <<< "$out" || { printf '  FAIL  %s rejected, but not for: %s\n' "$label" "$pattern"; missing=1; }
  done
  [ "$missing" -eq 0 ] && { printf '  PASS  %s rejected by the expected assertion\n' "$label"; return 0; }
  return 1
}

self_test() {
  local V="$1" D="$2" S; S="$(mktemp -d)"; local failed=0
  printf '=== self-test: the checker must reject bad artifacts, for the right reasons ===\n\n'

  # Case 1 mutates one byte IN PLACE. Appending would change the archive length and break unzip
  # first, which would prove unzip robustness rather than hash detection.
  cp -R "$D" "$S/case1"
  printf '\x41' | dd of="$S/case1/MPCore.Templates.$V.nupkg" bs=1 seek=900 count=1 conv=notrunc status=none
  assert_case "case 1 (tampered bytes, manifest not refrozen)" "$S/case1" "$V" "SHA256SUMS mismatch" || failed=1

  # Case 2 refreezes the hashes, so it passes the hash gate and can only be caught by content.
  cp -R "$D" "$S/case2"; local W="$S/work"; mkdir -p "$W"
  ( cd "$W" && unzip -qo "$S/case2/MPCore.Templates.$V.nupkg" )
  sedi \
    -e 's|builder.Services.AddMPCoreAuthorization();|builder.Services.AddMPCoreAuthorization(options =>\n    options.AllowAnonymousHealthEndpoints = allowAnonymousHealthEndpoints);|' \
    -e 's|RequireListenerPort(grpcPort)|RequireHost(grpcHost)|g' \
    "$W/$TEMPLATE_CONTENT/src/MPCore.Backend.Api/Program.cs"
  rm -f "$S/case2/MPCore.Templates.$V.nupkg"
  ( cd "$W" && zip -qr "$S/case2/MPCore.Templates.$V.nupkg" . )
  ( cd "$S/case2" && rm -f SHA256SUMS.txt && for f in *.nupkg *.snupkg; do shasum -a 256 "$f"; done | sort -k2 > SHA256SUMS.txt )
  assert_case "case 2 (stale template content, hashes refrozen)" "$S/case2" "$V" \
    "parameterless AddMPCoreAuthorization" "obsolete RequireHost port enforcement" || failed=1

  # Case 3: a package removed from the set.
  cp -R "$D" "$S/case3"; rm -f "$S/case3/MPCore.Transport.Http.$V.nupkg"
  ( cd "$S/case3" && rm -f SHA256SUMS.txt && for f in *.nupkg *.snupkg; do shasum -a 256 "$f"; done | sort -k2 > SHA256SUMS.txt )
  assert_case "case 3 (package removed)" "$S/case3" "$V" "nupkg count" "package id set differs" || failed=1

  # Case 4: correct identity, wrong payload - the 0.2.0-alpha.2 defect class, on a runtime package.
  cp -R "$D" "$S/case4"; local W4="$S/work4"; mkdir -p "$W4"
  ( cd "$W4" && unzip -qo "$S/case4/MPCore.Security.AspNetCore.$V.nupkg" && rm -rf lib )
  rm -f "$S/case4/MPCore.Security.AspNetCore.$V.nupkg"
  ( cd "$W4" && zip -qr "$S/case4/MPCore.Security.AspNetCore.$V.nupkg" . )
  ( cd "$S/case4" && rm -f SHA256SUMS.txt && for f in *.nupkg *.snupkg; do shasum -a 256 "$f"; done | sort -k2 > SHA256SUMS.txt )
  assert_case "case 4 (correct identity, stripped payload)" "$S/case4" "$V" "is missing lib/" || failed=1

  # Case 5: a dependency left pinned to a superseded cohort.
  cp -R "$D" "$S/case5"; local W5="$S/work5"; mkdir -p "$W5"
  ( cd "$W5" && unzip -qo "$S/case5/MPCore.Hosting.$V.nupkg" )
  sedi "s|<dependency id=\"MPCore.Application\" version=\"$V\"|<dependency id=\"MPCore.Application\" version=\"0.2.0-alpha.2\"|" "$W5/MPCore.Hosting.nuspec"
  rm -f "$S/case5/MPCore.Hosting.$V.nupkg"
  ( cd "$W5" && zip -qr "$S/case5/MPCore.Hosting.$V.nupkg" . )
  ( cd "$S/case5" && rm -f SHA256SUMS.txt && for f in *.nupkg *.snupkg; do shasum -a 256 "$f"; done | sort -k2 > SHA256SUMS.txt )
  assert_case "case 5 (dependency pinned to a superseded cohort)" "$S/case5" "$V" "not pinned to $V" || failed=1

  # Cases 6-9 pin attacks an independent review proved the earlier checker accepted.
  local refreeze='rm -f SHA256SUMS.txt && for f in *.nupkg *.snupkg; do shasum -a 256 "$f"; done | sort -k2 > SHA256SUMS.txt'

  # 6: right id, right filename, different assembly inside.
  cp -R "$D" "$S/case6"; local W6="$S/work6"; mkdir -p "$W6"
  ( cd "$W6" && unzip -qo "$S/case6/MPCore.Security.AspNetCore.$V.nupkg" )
  unzip -p "$D/MPCore.Domain.$V.nupkg" "lib/$TFM/MPCore.Domain.dll" > "$W6/lib/$TFM/MPCore.Security.AspNetCore.dll"
  rm -f "$S/case6/MPCore.Security.AspNetCore.$V.nupkg"
  ( cd "$W6" && zip -qr "$S/case6/MPCore.Security.AspNetCore.$V.nupkg" . )
  ( cd "$S/case6" && eval "$refreeze" )
  assert_case "case 6 (substituted payload assembly)" "$S/case6" "$V" "does not identify itself" || failed=1

  # 7: an extra anonymous endpoint hidden by putting a second call on an existing line.
  cp -R "$D" "$S/case7"; local W7="$S/work7"; mkdir -p "$W7"
  ( cd "$W7" && unzip -qo "$S/case7/MPCore.Templates.$V.nupkg" )
  sedi 's|    livenessEndpoint.AllowAnonymous();|    livenessEndpoint.AllowAnonymous(); backdoorEndpoint.AllowAnonymous();|' \
    "$W7/$TEMPLATE_CONTENT/src/MPCore.Backend.Api/Program.cs"
  rm -f "$S/case7/MPCore.Templates.$V.nupkg"
  ( cd "$W7" && zip -qr "$S/case7/MPCore.Templates.$V.nupkg" . )
  ( cd "$S/case7" && eval "$refreeze" )
  assert_case "case 7 (extra anonymous endpoint on an existing line)" "$S/case7" "$V" "total .AllowAnonymous( calls" || failed=1

  # 8: security registrations commented out - satisfies a naive grep, does nothing at runtime.
  cp -R "$D" "$S/case8"; local W8="$S/work8"; mkdir -p "$W8"
  ( cd "$W8" && unzip -qo "$S/case8/MPCore.Templates.$V.nupkg" )
  sedi -e 's|^app.UseAuthorization();|// app.UseAuthorization();|' \
            -e 's|^app.UseTransportPortSeparation();|// app.UseTransportPortSeparation();|' \
    "$W8/$TEMPLATE_CONTENT/src/MPCore.Backend.Api/Program.cs"
  rm -f "$S/case8/MPCore.Templates.$V.nupkg"
  ( cd "$W8" && zip -qr "$S/case8/MPCore.Templates.$V.nupkg" . )
  ( cd "$S/case8" && eval "$refreeze" )
  assert_case "case 8 (security registrations commented out)" "$S/case8" "$V" "UseAuthorization registered" || failed=1

  # 9: manifest keeps its line count via a duplicate while an artifact goes unlisted and is swapped.
  cp -R "$D" "$S/case9"
  ( cd "$S/case9" && eval "$refreeze" \
      && sedi "s|^.*  MPCore.Observability.$V.snupkg$|$(head -1 SHA256SUMS.txt)|" SHA256SUMS.txt \
      && echo "garbage" > "MPCore.Observability.$V.snupkg" )
  assert_case "case 9 (manifest does not cover the directory)" "$S/case9" "$V" "does not match the directory" || failed=1

  # Cases 10-12 pin the AI-tooling assertions. An independent review proved the first version of that
  # section accepted all three of these.
  local TC="$TEMPLATE_CONTENT"

  # 10: an adapter stripped of its frontmatter is invisible to both runtimes.
  cp -R "$D" "$S/case10"; local W10="$S/work10"; mkdir -p "$W10"
  ( cd "$W10" && unzip -qo "$S/case10/MPCore.Templates.$V.nupkg" )
  sedi '1,/^---$/d' "$W10/$TC/.claude/skills/mpcore-apply-security/SKILL.md"
  rm -f "$S/case10/MPCore.Templates.$V.nupkg"
  ( cd "$W10" && zip -qr "$S/case10/MPCore.Templates.$V.nupkg" . )
  ( cd "$S/case10" && eval "$refreeze" )
  assert_case "case 10 (adapter without frontmatter)" "$S/case10" "$V" "frontmatter matching its body" || failed=1

  # 11: a link that resolves, but to another skill's body.
  cp -R "$D" "$S/case11"; local W11="$S/work11"; mkdir -p "$W11"
  ( cd "$W11" && unzip -qo "$S/case11/MPCore.Templates.$V.nupkg" )
  sedi 's|mpcore-apply-security/SKILL.md|mpcore-apply-observability/SKILL.md|g' \
    "$W11/$TC/.agents/skills/mpcore-apply-security/SKILL.md"
  rm -f "$S/case11/MPCore.Templates.$V.nupkg"
  ( cd "$W11" && zip -qr "$S/case11/MPCore.Templates.$V.nupkg" . )
  ( cd "$S/case11" && eval "$refreeze" )
  assert_case "case 11 (adapter cross-wired to another skill)" "$S/case11" "$V" "cross-wired" || failed=1

  # 12: a .claude adapter that grew a full body - the layout the first version never measured.
  cp -R "$D" "$S/case12"; local W12="$S/work12"; mkdir -p "$W12"
  ( cd "$W12" && unzip -qo "$S/case12/MPCore.Templates.$V.nupkg" )
  cat "$W12/$TC/.mpcore/skills/mpcore-apply-security/SKILL.md" \
    >> "$W12/$TC/.claude/skills/mpcore-apply-security/SKILL.md"
  rm -f "$S/case12/MPCore.Templates.$V.nupkg"
  ( cd "$W12" && zip -qr "$S/case12/MPCore.Templates.$V.nupkg" . )
  ( cd "$S/case12" && eval "$refreeze" )
  assert_case "case 12 (claude adapter grew a duplicated body)" "$S/case12" "$V" "duplicated body" || failed=1

  rm -rf "$S"
  printf '\n'
  [ "$failed" -eq 0 ] && { printf 'self-test PASSED: all twelve bad artifacts rejected by the expected assertions\n'; return 0; }
  printf 'self-test FAILED\n'; return 1
}

MODE=verify
if [ "${1:-}" = "--self-test" ]; then MODE=selftest; shift; fi
V="${1:?usage: verify-release-artifacts.sh [--self-test] <version> [release-dir]}"
D="${2:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/artifacts/release/$V}"
if [ "$MODE" = selftest ]; then self_test "$V" "$D"; else verify_release "$V" "$D"; fi
