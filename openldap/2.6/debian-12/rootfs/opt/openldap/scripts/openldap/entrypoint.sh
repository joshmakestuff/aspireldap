#!/bin/bash
# OpenLDAP entrypoint — runs setup before starting the daemon.
# SPDX-License-Identifier: Apache-2.0

set -o errexit
set -o nounset
set -o pipefail

# Source the library for the info helper
. /opt/openldap/scripts/libopenldap.sh

if [[ "$1" = "/opt/openldap/scripts/openldap/run.sh" ]]; then
    info "** Starting LDAP setup **"
    /opt/openldap/scripts/openldap/setup.sh
    info "** LDAP setup finished! **"
fi

echo ""
exec "$@"
