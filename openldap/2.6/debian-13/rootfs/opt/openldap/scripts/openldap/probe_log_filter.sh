#!/bin/bash
# Filters the Aspire health-check probe's connection blocks out of slapd's stats log.
# SPDX-License-Identifier: Apache-2.0
#
# The AppHost health check marks its root-DSE search twice: it requests a sentinel
# attribute ("aspire-healthcheck"), which slapd logs verbatim on the "SRCH attr=" line,
# and it carries a no-op "(cn=aspire-healthcheck)" branch in its search filter, which
# slapd logs on the "SRCH base=" line. Either marker classifies the connection. Reads
# slapd's stderr on stdin; run.sh routes the output back to the container's stderr.
#
# Fail-open contract: a connection's lines are discarded ONLY once the connection has
# completed as a wholly-successful probe — every line matched a probe shape (results all
# err=0), the sentinel was present, and the conn closed cleanly. Until then lines are
# withheld, and ANY surprise — a non-probe line shape, a nonzero err, too many lines, a
# 2-second stall, slapd exiting — flushes everything withheld verbatim. Lines without a
# conn= id pass through untouched.

set -o nounset

# Probe shapes before a sentinel line: ACCEPT, TLS handshake, admin bind, root-DSE search.
readonly PREFIX_RE='conn=[0-9]+ (fd=[0-9]+ (ACCEPT|TLS established)|op=0 BIND |op=0 RESULT tag=97 err=0( |$)|op=[0-9]+ SRCH base="" scope=0 )'
# The sentinels, either of which classifies a pending conn as a probe:
# - an attribute list containing the marker, logged verbatim by slapd;
# - the marker branch of the probe's search filter, on a root-DSE search only (slapd
#   case-normalizes assertion values, so the token must stay lowercase).
readonly SENTINEL_ATTR_RE='conn=[0-9]+ op=[0-9]+ SRCH attr=.*aspire-healthcheck'
readonly SENTINEL_FILTER_RE='conn=[0-9]+ op=[0-9]+ SRCH base="" scope=0 deref=[0-9]+ filter="\(\|\(objectClass=\*\)\(cn=aspire-healthcheck\)\)"'
# Probe shapes after the classifying line: the probe's own attribute list (present when the
# filter sentinel classified first), successful search result, unbind.
readonly TAIL_RE='conn=[0-9]+ op=[0-9]+ (SRCH attr=|SEARCH RESULT tag=101 err=0( |$)|UNBIND)'
# Clean close ("closed (connection lost)" etc. deliberately does not match).
readonly CLOSED_RE='conn=[0-9]+ fd=[0-9]+ closed$'

# A full probe block is 9-10 lines (10 with TLS); anything longer is not a probe.
readonly MAX_PENDING_LINES=12
readonly MAX_PENDING_SECONDS=2

declare -A conn_state    # id -> probe (sentinel seen, still withholding) | open (passthrough)
declare -A conn_buffer   # id -> withheld lines (each newline-terminated)
declare -A conn_lines    # id -> count of withheld lines
declare -A conn_since    # id -> $SECONDS when withholding started

flush_conn() {
    local id="$1"
    printf '%s' "${conn_buffer[$id]:-}"
    conn_state[$id]="open"
    unset "conn_buffer[$id]" "conn_lines[$id]" "conn_since[$id]"
}

forget_conn() {
    local id="$1"
    unset "conn_state[$id]" "conn_buffer[$id]" "conn_lines[$id]" "conn_since[$id]"
}

withhold() {
    local id="$1" line="$2"
    conn_buffer[$id]+="$line"$'\n'
    conn_lines[$id]=$(( ${conn_lines[$id]:-0} + 1 ))
    : "${conn_since[$id]:=$SECONDS}"
    if (( conn_lines[$id] >= MAX_PENDING_LINES )); then
        flush_conn "$id"
    fi
}

flush_stale() {
    local id
    for id in "${!conn_since[@]}"; do
        if (( SECONDS - conn_since[$id] >= MAX_PENDING_SECONDS )); then
            flush_conn "$id"
        fi
    done
}

partial=""
while true; do
    if IFS= read -r -t 1 line; then
        line="${partial}${line}"
        partial=""
    else
        rc=$?
        if (( rc > 128 )); then
            # Timed out; read(1) stores any partial line it consumed into $line.
            partial+="$line"
            flush_stale
            continue
        fi
        break # EOF: slapd exited.
    fi

    if [[ "$line" =~ conn=([0-9]+) ]]; then
        id="${BASH_REMATCH[1]}"
        case "${conn_state[$id]:-pending}" in
            open)
                # Any close (clean or lost) ends the conn; drop its bookkeeping entry.
                [[ "$line" == *" closed"* ]] && forget_conn "$id"
                ;;
            pending)
                if [[ "$line" =~ $SENTINEL_ATTR_RE ]] || [[ "$line" =~ $SENTINEL_FILTER_RE ]]; then
                    conn_state[$id]="probe"
                    withhold "$id" "$line"
                    continue
                fi
                if [[ "$line" =~ $PREFIX_RE ]]; then
                    withhold "$id" "$line"
                    continue
                fi
                # Not probe-shaped: release the withheld lines and stop touching this conn.
                flush_conn "$id"
                [[ "$line" == *" closed"* ]] && forget_conn "$id"
                ;;
            probe)
                if [[ "$line" =~ $CLOSED_RE ]]; then
                    # The one and only drop point: a sentinel-marked, fully-successful,
                    # cleanly-closed probe. Discard the whole block.
                    forget_conn "$id"
                    continue
                fi
                if [[ "$line" =~ $TAIL_RE ]]; then
                    withhold "$id" "$line"
                    continue
                fi
                # Surprise after the sentinel (nonzero err, extra op, lost connection):
                # this is not a clean probe — release everything.
                flush_conn "$id"
                [[ "$line" == *" closed"* ]] && forget_conn "$id"
                ;;
        esac
    fi
    printf '%s\n' "$line"
done

# slapd exited: emit whatever was still withheld, plus any unterminated final line.
for id in "${!conn_buffer[@]}"; do
    printf '%s' "${conn_buffer[$id]}"
done
if [[ -n "$partial" ]]; then
    printf '%s\n' "$partial"
fi
