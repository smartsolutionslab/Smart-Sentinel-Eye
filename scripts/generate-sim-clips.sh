#!/usr/bin/env bash
# Generates the dev-only Scenario Simulator clips (ADR-0111, spec 044): one ~20 s
# 1280x720 H.264 excerpt per scenario asset, written to
# src/AppHost/Resources/clips/. camera-sim loops whichever one an asset names.
#
# Supersedes generate-sim-loop.sh, which produced the single shared clip every
# camera used to play. sim-loop.mp4 is still generated here — it remains the
# default for any camera that belongs to no scenario asset.
#
# Every source is Wikimedia Commons. Licences are recorded per clip in
# src/AppHost/Resources/clips/<name>.ATTRIBUTION.txt — read the file *page* for
# the licence, not the extmetadata API, which disagrees with it.
#
# ffmpeg is absent on the host, so it runs from the pinned MediaMTX image the
# `docker run` below names. Re-run to regenerate, then commit the results:
#   bash scripts/generate-sim-clips.sh            # all clips
#   bash scripts/generate-sim-clips.sh paper-     # only names matching a prefix
set -euo pipefail

FILTER="${1:-}"

# name | source URL | start offset (s)
#
# **Take these URLs from the API, never by pattern.** The two-level hash prefix
# (/5/50/) is an MD5 of the file name, not something guessable — seven of eight
# constructed by hand here were wrong and would have 404'd. Get them with:
#
#   curl -sG https://commons.wikimedia.org/w/api.php \
#     --data-urlencode 'action=query' --data-urlencode 'prop=imageinfo' \
#     --data-urlencode 'iiprop=url' --data-urlencode 'format=json' \
#     --data-urlencode 'titles=File:<exact title>.webm'
#
# Offsets skip each clip's opening seconds, which are typically a static
# establishing shot. They were chosen by extracting frames and looking: a 20 s
# excerpt of a motionless pipe satisfies every automated check in spec 044 and
# still leaves an operator unable to tell one tile from another, which is the
# failure this feature exists to prevent.
#
# The four mill-* clips come from ONE 477 s source at four offsets. That is not a
# shortcut: a rolling mill filmed at four points in its own process is four
# different stations, which is exactly what the wall is claiming to show. One
# source also means one licence and one attribution to keep correct.
CLIPS=$(cat <<'TABLE'
sim-loop|https://upload.wikimedia.org/wikipedia/commons/b/b9/Proizvodnja_vro%C4%8De_valjane_debele_plo%C4%8Devine_v_Acroni_SIJ_Jesenice.ogv|250
mill-roughing|https://upload.wikimedia.org/wikipedia/commons/b/b9/Proizvodnja_vro%C4%8De_valjane_debele_plo%C4%8Devine_v_Acroni_SIJ_Jesenice.ogv|60
mill-finishing|https://upload.wikimedia.org/wikipedia/commons/b/b9/Proizvodnja_vro%C4%8De_valjane_debele_plo%C4%8Devine_v_Acroni_SIJ_Jesenice.ogv|150
mill-cooling|https://upload.wikimedia.org/wikipedia/commons/b/b9/Proizvodnja_vro%C4%8De_valjane_debele_plo%C4%8Devine_v_Acroni_SIJ_Jesenice.ogv|360
mill-coiler|https://upload.wikimedia.org/wikipedia/commons/b/b9/Proizvodnja_vro%C4%8De_valjane_debele_plo%C4%8Devine_v_Acroni_SIJ_Jesenice.ogv|320
paper-pressure-screener|https://upload.wikimedia.org/wikipedia/commons/c/c5/The_Gori%C4%8Dane_company_-_pressure_screener.webm|6
paper-press-group|https://upload.wikimedia.org/wikipedia/commons/1/12/The_Gori%C4%8Dane_company_-_press_group.webm|8
paper-after-drying|https://upload.wikimedia.org/wikipedia/commons/7/7b/The_Gori%C4%8Dane_company_-_After-drying_group.webm|10
paper-packaging|https://upload.wikimedia.org/wikipedia/commons/3/39/Packaging_-_manual_and_machine.webm|6
electronics-moulding|https://upload.wikimedia.org/wikipedia/commons/9/9f/Gigaset_Cordless_Telephone_Production_II_-_Engel_Injection_Moulding_Machine.webm|6
electronics-smd-line|https://upload.wikimedia.org/wikipedia/commons/3/34/Gigaset_Cordless_Telephone_Production_V_ASM_Siplace_SMD_Production_Line.webm|2
electronics-conveyor|https://upload.wikimedia.org/wikipedia/commons/3/36/Gigaset_Cordless_Telephone_Production_VII_-_Pneumatic_Conveyor_Belt.webm|2
electronics-inspection|https://upload.wikimedia.org/wikipedia/commons/a/af/Gigaset_Smartphone_Production_IV_Quality_Inspection.webm|4
TABLE
)

SEGMENT_LEN=20

# `pwd -W` yields a Windows path on Git Bash (which Docker Desktop needs for the
# bind mount); it falls back to a normal POSIX `pwd` on Linux/macOS.
RES_ROOT="$(cd "$(dirname "$0")/../src/AppHost/Resources" && pwd)"
mkdir -p "$RES_ROOT/clips"
OUT_DIR="$(cd "$RES_ROOT/clips" && { pwd -W 2>/dev/null || pwd; })"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
WORK_DIR="$(cd "$WORK" && { pwd -W 2>/dev/null || pwd; })"

while IFS='|' read -r NAME URL START; do
  [ -z "$NAME" ] && continue
  if [ -n "$FILTER" ] && [[ "$NAME" != "$FILTER"* ]]; then continue; fi

  echo "==> $NAME (from ${START}s)"
  SRC="$WORK/${NAME}.src"

  # Wikimedia's policy requires a descriptive User-Agent and rate-limits without
  # one: a full regeneration got HTTP 429 partway through and left the directory
  # half at one quality and half at another. --retry rides out a transient limit;
  # the sleep keeps a 13-clip run from provoking it again.
  curl -fsSL --max-time 900 \
    -A "smart-sentinel-eye-dev/1.0 (scenario simulator clip generation; ADR-0111)" \
    --retry 3 --retry-delay 20 --retry-all-errors \
    -o "$SRC" "$URL"
  sleep 2

  # MSYS_NO_PATHCONV stops Git Bash on Windows rewriting the container paths
  # (`/work`, `/out`) into `C:/Program Files/Git/...`; harmless elsewhere.
  # Everything is normalised to 1280x720/25fps H.264 baseline so camera-sim can
  # stream-copy it: sources vary between 1920x1080 and 1280x720.
  #
  # -crf 28 rather than x264's default 23. These are simulation tiles on a dev
  # wall, not footage anyone inspects, and the default put the directory at 67 MB
  # against the ~40 MB the plan committed to. Quality at tile size is
  # indistinguishable; the repo carries half the weight, permanently.
  MSYS_NO_PATHCONV=1 docker run --rm -v "${WORK_DIR}:/work" -v "${OUT_DIR}:/out" \
    --entrypoint ffmpeg bluenviron/mediamtx:1.21.0-ffmpeg \
    -ss "${START}" -i "/work/${NAME}.src" -t "${SEGMENT_LEN}" \
    -vf "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:-1:-1,setsar=1,fps=25" \
    -c:v libx264 -profile:v baseline -level:v 3.1 -pix_fmt yuv420p -g 25 -an -crf 28 \
    -movflags +faststart -y "/out/${NAME}.mp4"

  rm -f "$SRC"
done <<< "$CLIPS"

echo
echo "Wrote clips to ${RES_ROOT}/clips"
echo "Every clip needs a matching .ATTRIBUTION.txt in the same commit."
