#!/bin/bash
#
# On-screen smoke test of the Mac host (Plan C).
#
#   Tools/macOS/ui-smoke.sh            every flow
#   Tools/macOS/ui-smoke.sh all        the same
#   Tools/macOS/ui-smoke.sh members    one flow (members-restore runs members first, on the same folder)
#   Tools/macOS/ui-smoke.sh --list     the flows and what each covers
#
# Builds Tests/EBMC.UiSmoke in Debug (a Release build swallows exceptions thrown on the UI thread) and runs each
# flow in its own process on a fresh, empty data folder (EBMC_DATA_DIR, so the real settings and members list are
# never opened). A window opens on the screen while a flow runs; it ignores the real mouse and keyboard, and uses
# its own clipboard and file dialogs. Screenshots go to out/ui-smoke/<timestamp>/<flow>/.
# Prints a pass/fail table; exits non-zero if any flow failed. Scratch folders of failed flows are kept.

set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
	echo "This script runs the macOS host and has to run on a Mac." >&2
	exit 1
fi

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO/Tests/EBMC.UiSmoke/EBMC.UiSmoke.csproj"
DLL="$REPO/Tests/EBMC.UiSmoke/bin/Debug/net10.0/EBMC.UiSmoke.dll"
ALL_FLOWS=(start members members-restore modes slice laser-export)

echo "Building (Debug)..."
dotnet build "$PROJECT" -c Debug -nologo -v quiet | grep -E "error|Build succeeded" || true

if [[ "${1:-}" == "--list" ]]; then
	dotnet "$DLL" --list
	exit 0
fi

REQUESTED=("${@:-all}")
if [[ "${REQUESTED[0]}" == "all" ]]; then
	REQUESTED=("${ALL_FLOWS[@]}")
fi

for flow in "${REQUESTED[@]}"; do
	if [[ ! " ${ALL_FLOWS[*]} " == *" $flow "* ]]; then
		echo "Unknown flow: $flow (known: ${ALL_FLOWS[*]})" >&2
		exit 2
	fi
done

STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$REPO/out/ui-smoke/$STAMP"
TEMP_ROOT="${TMPDIR:-/tmp}"
mkdir -p "$OUT"

RESULTS=()
FAILED=0
MEMBERS_DATA=""
MEMBERS_FAILED=0

run_flow() {
	local flow="$1" data="$2"
	local log="$OUT/$flow.log" start end code
	start=$(date +%s)
	set +e
	EBMC_DATA_DIR="$data" dotnet "$DLL" --flow "$flow" --out "$OUT/$flow" 2>&1 | tee "$log"
	code=${PIPESTATUS[0]}
	set -e
	end=$(date +%s)
	local reason
	reason="$(grep -E '^UI SMOKE (PASSED|FAILED|REFUSED)' "$log" | tail -1 || true)"
	if [[ $code -eq 0 ]]; then
		RESULTS+=("$(printf '%-16s PASS  %4ss' "$flow" $((end - start)))")
	else
		FAILED=1
		RESULTS+=("$(printf '%-16s FAIL  %4ss  %s' "$flow" $((end - start)) "${reason#UI SMOKE }")")
		echo "Scratch data folder kept: $data"
	fi
	return $code
}

for flow in "${REQUESTED[@]}"; do
	echo
	echo "=== $flow ==="
	if [[ "$flow" == "members-restore" ]]; then
		if [[ "$MEMBERS_FAILED" == 1 ]]; then
			RESULTS+=("$(printf '%-16s SKIP  (members failed)' "$flow")")
			continue
		fi

		if [[ -z "$MEMBERS_DATA" ]]; then
			MEMBERS_DATA="$(mktemp -d "${TEMP_ROOT%/}/ebmc-ui-smoke.XXXXXX")"
			echo "(members first, to make the files members-restore damages)"
			if ! run_flow members "$MEMBERS_DATA"; then
				RESULTS+=("$(printf '%-16s SKIP  (members failed)' "$flow")")
				continue
			fi
		fi

		run_flow members-restore "$MEMBERS_DATA" && rm -rf "$MEMBERS_DATA" || true
		MEMBERS_DATA=""
		continue
	fi

	DATA="$(mktemp -d "${TEMP_ROOT%/}/ebmc-ui-smoke.XXXXXX")"
	if ! run_flow "$flow" "$DATA"; then
		[[ "$flow" == "members" ]] && MEMBERS_FAILED=1
	else
		if [[ "$flow" == "members" && " ${REQUESTED[*]} " == *" members-restore "* ]]; then
			MEMBERS_DATA="$DATA"
		else
			rm -rf "$DATA"
		fi
	fi
done

echo
echo "Results:"
for line in "${RESULTS[@]}"; do
	echo "  $line"
done
echo "Screenshots and logs: $OUT"

exit $FAILED
