#!/usr/bin/env bash
# tools/check-log.sh <logfile> — offline checker for the GF2 runtime log contract
# (SPEC §4: the 6 pinned "[GF2]" prefixes, verbatim; verified across 14 harness
#  subcommands; 2026-11-14 架構反轉: grip/fp arms/fp visibility 釘點退役, scale: 新入)。
#
# NOTE: the REAL session check runs in F3 against LogOutput.log with the
# mtime-newer-than-DLL staleness rule. This script only validates a supplied log
# offline; the committed fixtures under tools/fixtures/ keep it testable without
# a game session.
#
# Exit 0: all 6 pinned prefixes found and none of the failure markers
#         (contract skip: / implausible / missing / unresolved, on a
#         "[GF2]" line) appear.
# Exit 1: one named failure reason per missing prefix or failure class.
set -u

log="${1:-}"
if [[ -z "${log}" || ! -f "${log}" ]]; then
  echo "usage: bash tools/check-log.sh <logfile>" >&2
  echo "FAIL: no log file given or file not found: '${log}'" >&2
  exit 1
fi

# Each entry: <SPEC item no> <named reason> <grep -E pattern>
req=(
  "1|contract:|\[GF2\] contract: "
  "2|rig:|\[GF2\] rig: bones="
  "3|body replaced|\[GF2\] body replaced on '"
  "4|preload:|\[GF2\] preload: "
  "5|cache: evicted|\[GF2\] cache: evicted "
  "6|scale:|\[GF2\] scale: "
)
# Failure markers only count on a "[GF2]" producer line (SPEC §4 + todo 29).
fail=(
  "contract skip:|\[GF2\].*contract skip:"
  "implausible|\[GF2\].*implausible"
  "missing|\[GF2\].*missing"
  "unresolved|\[GF2\].*unresolved"
)

rc=0
for spec in "${req[@]}"; do
  IFS='|' read -r item name pat <<<"${spec}"
  if ! grep -qE "${pat}" "${log}"; then
    echo "FAIL: missing [GF2] prefix '${name}' (SPEC §4 item ${item})" >&2
    rc=1
  fi
done

for spec in "${fail[@]}"; do
  IFS='|' read -r name pat <<<"${spec}"
  if grep -qE "${pat}" "${log}"; then
    first="$(grep -nE "${pat}" "${log}" | head -1)"
    echo "FAIL: failure marker '${name}' (line ${first%%:*})" >&2
    rc=1
  fi
done

if [[ ${rc} -eq 0 ]]; then
  echo "OK: all 6 pinned [GF2] prefixes present, no failure markers"
fi
exit "${rc}"
