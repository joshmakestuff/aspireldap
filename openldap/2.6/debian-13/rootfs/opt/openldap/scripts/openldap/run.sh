#!/bin/bash
# OpenLDAP run — builds the slapd command line and execs the daemon.
# SPDX-License-Identifier: Apache-2.0

set -o errexit
set -o nounset
set -o pipefail

# Load library
. /opt/openldap/scripts/libopenldap.sh

# Load LDAP environment variables
eval "$(ldap_env)"

command="$(command -v slapd)"

# Reduce maximum number of open file descriptors
ulimit -n "$LDAP_ULIMIT_NOFILES"

declare -a flags
declare -A flags_map

# Drop privileges if we start as root
am_i_root && flags_map["-u"]="${LDAP_DAEMON_USER}"

# Set config dir
flags_map["-F"]="${LDAP_CONF_DIR}/slapd.d"

# Enable debug with desired level
flags_map["-d"]="${LDAP_LOGLEVEL}"

# The LDAP IPC socket is always on
flags_map["-h"]+="${flags_map["-h"]:+" "}ldapi:///"

# Add LDAP URI
flags_map["-h"]+="${flags_map["-h"]:+" "}ldap://:${LDAP_PORT_NUMBER}/"

# Add LDAPS URI when TLS is enabled
if is_boolean_yes "${LDAP_ENABLE_TLS}"; then
    flags_map["-h"]+="${flags_map["-h"]:+" "}ldaps://:${LDAP_LDAPS_PORT_NUMBER}/"
fi

# Build flags list
for flag in "${!flags_map[@]}"; do
    flags+=("${flag}" "${flags_map[${flag}]}")
done

# Allow extra command line flags
flags+=("$@")

info "** Starting slapd **"
debug "Startup cmd: ${command} ${flags[*]}"
exec "${command}" "${flags[@]}"
