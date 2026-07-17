#!/bin/bash
# OpenLDAP setup — validates environment, initializes the server, runs init scripts.
# SPDX-License-Identifier: Apache-2.0

set -o errexit
set -o nounset
set -o pipefail

# Load library
. /opt/openldap/scripts/libopenldap.sh

# Load LDAP environment variables
eval "$(ldap_env)"

# Validate settings
ldap_validate

# Ensure OpenLDAP is stopped when this script ends
trap "ldap_stop" EXIT

# Initialize OpenLDAP
ldap_initialize

# Run custom initialization scripts
ldap_custom_init_scripts

# Every init step succeeded (set -e would have aborted otherwise) — mark the data dir
# complete so restarts may serve it. Without this marker, existing data is refused as the
# remnant of a failed initialization.
touch "$LDAP_INIT_COMPLETE_MARKER"
