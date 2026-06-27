#!/bin/bash
# OpenLDAP library — core functions for container initialization and management.
# Forked from Bitnami OpenLDAP, rewritten for stock Debian slapd packages.
# SPDX-License-Identifier: Apache-2.0

# shellcheck disable=SC2119,SC2120

########################
# Minimal logging helpers
########################
info()  { echo "[INFO]  $*"; }
warn()  { echo "[WARN]  $*" >&2; }
error() { echo "[ERROR] $*" >&2; }
debug() { [[ "${BITNAMI_DEBUG:-false}" == "true" ]] && echo "[DEBUG] $*" || true; }

# Run a command, suppressing stdout/stderr unless debug mode is on.
debug_execute() {
    if [[ "${BITNAMI_DEBUG:-false}" == "true" ]]; then
        "$@"
    else
        "$@" >/dev/null 2>&1
    fi
}

########################
# Retry a command up to N times with a sleep between attempts.
# Arguments:
#   $1 - command (function name or simple command)
#   $2 - max retries (default 12)
#   $3 - sleep seconds between retries (default 1)
########################
retry_while() {
    local cmd="${1:?cmd is required}"
    local retries="${2:-12}"
    local sleep_time="${3:-1}"
    local attempt=0
    while ! eval "$cmd"; do
        attempt=$((attempt + 1))
        if [[ $attempt -ge $retries ]]; then
            return 1
        fi
        sleep "$sleep_time"
    done
}

########################
# Check if a value is yes/true
########################
is_boolean_yes() {
    local -r val="${1:-}"
    case "$val" in
        yes|Yes|YES|true|True|TRUE|1) return 0 ;;
        *) return 1 ;;
    esac
}

########################
# Check if a value is a valid yes/no
########################
is_yes_no_value() {
    local -r val="${1:-}"
    case "$val" in
        yes|Yes|YES|no|No|NO) return 0 ;;
        *) return 1 ;;
    esac
}

########################
# Check if a value is a positive integer
########################
is_positive_int() {
    local -r val="${1:-}"
    [[ "$val" =~ ^[1-9][0-9]*$ ]]
}

########################
# Ensure a directory exists (mkdir -p)
########################
ensure_dir_exists() {
    mkdir -p "$1"
}

########################
# Check if a directory is empty
########################
is_dir_empty() {
    local dir="${1:?dir is required}"
    [[ -d "$dir" ]] && [[ -z "$(ls -A "$dir" 2>/dev/null)" ]]
}

########################
# Check if running as root
########################
am_i_root() {
    [[ "$(id -u)" -eq 0 ]]
}

########################
# Get PID from file
########################
get_pid_from_file() {
    local pid_file="${1:?pid_file is required}"
    if [[ -f "$pid_file" ]]; then
        cat "$pid_file"
    fi
}

########################
# Stop a service by PID file
########################
stop_service_using_pid() {
    local pid_file="${1:?pid_file is required}"
    local pid
    pid="$(get_pid_from_file "$pid_file")"
    if [[ -n "$pid" ]]; then
        kill "$pid" 2>/dev/null || true
    fi
}

########################
# Replace text in a file (portable sed wrapper)
########################
replace_in_file() {
    local file="${1:?file is required}"
    local pattern="${2:?pattern is required}"
    local replacement="${3:?replacement is required}"
    sed -i "s|${pattern}|${replacement}|g" "$file"
}

########################
# Load global variables used on OpenLDAP configuration
# Globals:
#   LDAP_*
# Arguments:
#   None
# Returns:
#   Series of exports to be used as 'eval' arguments
#########################
ldap_env() {
    cat << "EOF"
# Paths — stock Debian layout
export LDAP_CONF_DIR="/etc/ldap"
export LDAP_SHARE_DIR="/tmp/ldap-setup"
export LDAP_VOLUME_DIR="/data/openldap"
export LDAP_DATA_DIR="${LDAP_VOLUME_DIR}/data"
export LDAP_ACCESSLOG_DATA_DIR="${LDAP_DATA_DIR}/accesslog"
export LDAP_ONLINE_CONF_DIR="${LDAP_VOLUME_DIR}/slapd.d"
export LDAP_PID_FILE="/var/run/slapd/slapd.pid"
export LDAP_MODULE_PATH="/usr/lib/ldap"
export LDAP_CUSTOM_LDIF_DIR="${LDAP_CUSTOM_LDIF_DIR:-/ldifs}"
export LDAP_CUSTOM_LDIF_CONTINUE_ON_ERROR="${LDAP_CUSTOM_LDIF_CONTINUE_ON_ERROR:-no}"
export LDAP_CUSTOM_SCHEMA_FILE="${LDAP_CUSTOM_SCHEMA_FILE:-/schema/custom.ldif}"
export LDAP_CUSTOM_SCHEMA_DIR="${LDAP_CUSTOM_SCHEMA_DIR:-/schemas}"
export LDAP_TLS_CERT_FILE="${LDAP_TLS_CERT_FILE:-}"
export LDAP_TLS_KEY_FILE="${LDAP_TLS_KEY_FILE:-}"
export LDAP_TLS_CA_FILE="${LDAP_TLS_CA_FILE:-}"
export LDAP_TLS_VERIFY_CLIENTS="${LDAP_TLS_VERIFY_CLIENTS:-never}"
export LDAP_TLS_DH_PARAMS_FILE="${LDAP_TLS_DH_PARAMS_FILE:-}"
# Users
export LDAP_DAEMON_USER="openldap"
export LDAP_DAEMON_GROUP="openldap"
# Settings
export LDAP_PORT_NUMBER="${LDAP_PORT_NUMBER:-1389}"
export LDAP_LDAPS_PORT_NUMBER="${LDAP_LDAPS_PORT_NUMBER:-1636}"
export LDAP_ROOT="${LDAP_ROOT:-dc=example,dc=org}"
export LDAP_SUFFIX="$(if [ -z "${LDAP_SUFFIX+x}" ]; then echo "${LDAP_ROOT}"; else echo "${LDAP_SUFFIX}"; fi)"
export LDAP_ADMIN_USERNAME="${LDAP_ADMIN_USERNAME:-admin}"
export LDAP_ADMIN_DN="${LDAP_ADMIN_USERNAME/#/cn=},${LDAP_ROOT}"
export LDAP_ADMIN_PASSWORD="${LDAP_ADMIN_PASSWORD:-adminpassword}"
export LDAP_CONFIG_ADMIN_ENABLED="${LDAP_CONFIG_ADMIN_ENABLED:-no}"
export LDAP_CONFIG_ADMIN_USERNAME="${LDAP_CONFIG_ADMIN_USERNAME:-admin}"
export LDAP_CONFIG_ADMIN_DN="${LDAP_CONFIG_ADMIN_USERNAME/#/cn=},cn=config"
export LDAP_CONFIG_ADMIN_PASSWORD="${LDAP_CONFIG_ADMIN_PASSWORD:-configpassword}"
export LDAP_ADD_SCHEMAS="${LDAP_ADD_SCHEMAS:-yes}"
export LDAP_EXTRA_SCHEMAS="${LDAP_EXTRA_SCHEMAS:-cosine,inetorgperson,nis}"
export LDAP_SKIP_DEFAULT_TREE="${LDAP_SKIP_DEFAULT_TREE:-no}"
export LDAP_USERS="${LDAP_USERS:-user01,user02}"
export LDAP_PASSWORDS="${LDAP_PASSWORDS:-bitnami1,bitnami2}"
export LDAP_USER_OU="${LDAP_USER_OU:-users}"
export LDAP_GROUP_OU="${LDAP_GROUP_OU:-groups}"
export LDAP_GROUP="${LDAP_GROUP:-readers}"
export LDAP_ENABLE_TLS="${LDAP_ENABLE_TLS:-no}"
export LDAP_REQUIRE_TLS="${LDAP_REQUIRE_TLS:-no}"
export LDAP_ULIMIT_NOFILES="${LDAP_ULIMIT_NOFILES:-1024}"
export LDAP_ALLOW_ANON_BINDING="${LDAP_ALLOW_ANON_BINDING:-yes}"
export LDAP_LOGLEVEL="${LDAP_LOGLEVEL:-256}"
export LDAP_PASSWORD_HASH="${LDAP_PASSWORD_HASH:-{SSHA\}}"
export LDAP_CONFIGURE_PPOLICY="${LDAP_CONFIGURE_PPOLICY:-no}"
export LDAP_PPOLICY_USE_LOCKOUT="${LDAP_PPOLICY_USE_LOCKOUT:-no}"
export LDAP_PPOLICY_HASH_CLEARTEXT="${LDAP_PPOLICY_HASH_CLEARTEXT:-no}"
export LDAP_ENABLE_ACCESSLOG="${LDAP_ENABLE_ACCESSLOG:-no}"
export LDAP_ACCESSLOG_DB="${LDAP_ACCESSLOG_DB:-cn=accesslog}"
export LDAP_ACCESSLOG_LOGOPS="${LDAP_ACCESSLOG_LOGOPS:-writes}"
export LDAP_ACCESSLOG_LOGSUCCESS="${LDAP_ACCESSLOG_LOGSUCCESS:-TRUE}"
export LDAP_ACCESSLOG_LOGPURGE="${LDAP_ACCESSLOG_LOGPURGE:-07+00:00 01+00:00}"
export LDAP_ACCESSLOG_LOGOLD="${LDAP_ACCESSLOG_LOGOLD:-(objectClass=*)}"
export LDAP_ACCESSLOG_LOGOLDATTR="${LDAP_ACCESSLOG_LOGOLDATTR:-objectClass}"
export LDAP_ACCESSLOG_ADMIN_USERNAME="${LDAP_ACCESSLOG_ADMIN_USERNAME:-admin}"
export LDAP_ACCESSLOG_ADMIN_DN="${LDAP_ACCESSLOG_ADMIN_USERNAME/#/cn=},${LDAP_ACCESSLOG_DB:-cn=accesslog}"
export LDAP_ACCESSLOG_ADMIN_PASSWORD="${LDAP_ACCESSLOG_PASSWORD:-accesspassword}"
export LDAP_ENABLE_SYNCPROV="${LDAP_ENABLE_SYNCPROV:-no}"
export LDAP_SYNCPROV_CHECKPPOINT="${LDAP_SYNCPROV_CHECKPPOINT:-100 10}"
export LDAP_SYNCPROV_SESSIONLOG="${LDAP_SYNCPROV_SESSIONLOG:-100}"

# Docker secrets support: *_FILE env vars override the plain-text equivalents
ldap_env_vars=(
    LDAP_ADMIN_PASSWORD
    LDAP_CONFIG_ADMIN_PASSWORD
    LDAP_ACCESSLOG_ADMIN_PASSWORD
)
for env_var in "${ldap_env_vars[@]}"; do
    file_env_var="${env_var}_FILE"
    if [[ -n "${!file_env_var:-}" ]]; then
        if [[ -r "${!file_env_var:-}" ]]; then
            export "${env_var}=$(< "${!file_env_var}")"
            unset "${file_env_var}"
        else
            warn "Skipping export of '${env_var}'. '${!file_env_var:-}' is not readable."
        fi
    fi
done
unset ldap_env_vars

# Pre-hash passwords using slappasswd
export LDAP_ENCRYPTED_ADMIN_PASSWORD="$(echo -n $LDAP_ADMIN_PASSWORD | slappasswd -n -T /dev/stdin)"
export LDAP_ENCRYPTED_CONFIG_ADMIN_PASSWORD="$(echo -n $LDAP_CONFIG_ADMIN_PASSWORD | slappasswd -n -T /dev/stdin)"
export LDAP_ENCRYPTED_ACCESSLOG_ADMIN_PASSWORD="$(echo -n $LDAP_ACCESSLOG_ADMIN_PASSWORD | slappasswd -n -T /dev/stdin)"
EOF
}

########################
# Validate settings in LDAP_* environment variables
########################
ldap_validate() {
    info "Validating settings in LDAP_* env vars"
    local error_code=0

    print_validation_error() {
        error "$1"
        error_code=1
    }

    for var in LDAP_SKIP_DEFAULT_TREE LDAP_ENABLE_TLS; do
        if ! is_yes_no_value "${!var}"; then
            print_validation_error "The allowed values for $var are: yes or no"
        fi
    done

    if is_boolean_yes "$LDAP_ENABLE_TLS"; then
        if [[ -z "$LDAP_TLS_CERT_FILE" ]]; then
            print_validation_error "You must provide a X.509 certificate in order to use TLS"
        elif [[ ! -f "$LDAP_TLS_CERT_FILE" ]]; then
            print_validation_error "The X.509 certificate file in the specified path ${LDAP_TLS_CERT_FILE} does not exist"
        fi
        if [[ -z "$LDAP_TLS_KEY_FILE" ]]; then
            print_validation_error "You must provide a private key in order to use TLS"
        elif [[ ! -f "$LDAP_TLS_KEY_FILE" ]]; then
            print_validation_error "The private key file in the specified path ${LDAP_TLS_KEY_FILE} does not exist"
        fi
        if [[ -z "$LDAP_TLS_CA_FILE" ]]; then
            print_validation_error "You must provide a CA X.509 certificate in order to use TLS"
        elif [[ ! -f "$LDAP_TLS_CA_FILE" ]]; then
            print_validation_error "The CA X.509 certificate file in the specified path ${LDAP_TLS_CA_FILE} does not exist"
        fi
    fi

    read -r -a users <<< "$(tr ',;' ' ' <<< "${LDAP_USERS}")"
    read -r -a passwords <<< "$(tr ',;' ' ' <<< "${LDAP_PASSWORDS}")"
    if [[ "${#users[@]}" -ne "${#passwords[@]}" ]]; then
        print_validation_error "Specify the same number of passwords on LDAP_PASSWORDS as the number of users on LDAP_USERS!"
    fi

    for var in LDAP_PORT_NUMBER LDAP_LDAPS_PORT_NUMBER; do
        if ! is_positive_int "${!var}"; then
            print_validation_error "The value for $var must be a positive integer!"
        fi
    done

    if [[ -n "$LDAP_PORT_NUMBER" ]] && [[ -n "$LDAP_LDAPS_PORT_NUMBER" ]]; then
        if [[ "$LDAP_PORT_NUMBER" -eq "$LDAP_LDAPS_PORT_NUMBER" ]]; then
            print_validation_error "LDAP_PORT_NUMBER and LDAP_LDAPS_PORT_NUMBER are bound to the same port!"
        fi
    fi

    [[ "$error_code" -eq 0 ]] || exit "$error_code"
}

########################
# Check if OpenLDAP is running
########################
is_ldap_running() {
    local pid
    pid="$(get_pid_from_file "${LDAP_PID_FILE}")"
    if [[ -n "${pid}" ]]; then
        kill -0 "$pid" 2>/dev/null
    else
        false
    fi
}

is_ldap_not_running() {
    ! is_ldap_running
}

########################
# Check if OpenLDAP is ready for queries
########################
is_ldap_ready() {
    debug_execute ldapsearch -Y EXTERNAL -H "ldapi:///" -b "cn=config" -s base "(objectClass=*)" dn
}

########################
# Start OpenLDAP server in background
########################
ldap_start_bg() {
    local -r retries="${1:-12}"
    local -r sleep_time="${2:-1}"
    # Bind the init daemon to the IPC socket ONLY — never the public TCP port. All
    # initialization (schemas, overlays, admin creds, the seed load) talks over ldapi:///,
    # so the public port is unnecessary here. Keeping it closed until the foreground daemon
    # starts (run.sh, after setup.sh has fully completed the seed) means a client / health
    # check cannot connect to a partially-seeded directory. See GitHub issue #3.
    local -a flags=("-h" "ldapi:///" "-F" "${LDAP_CONF_DIR}/slapd.d" "-d" "$LDAP_LOGLEVEL")

    if is_ldap_not_running; then
        info "Starting OpenLDAP server in background"
        ulimit -n "$LDAP_ULIMIT_NOFILES"
        am_i_root && flags=("-u" "$LDAP_DAEMON_USER" "${flags[@]}")
        debug_execute slapd "${flags[@]}" &
        if ! retry_while is_ldap_ready "$retries" "$sleep_time"; then
            error "OpenLDAP failed to start"
            return 1
        fi
    fi
}

########################
# Stop OpenLDAP server
########################
ldap_stop() {
    local -r retries="${1:-12}"
    local -r sleep_time="${2:-1}"

    are_db_files_locked() {
        local return_value=0
        read -r -a db_files <<< "$(find "$LDAP_DATA_DIR" -type f -print0 2>/dev/null | xargs -0 2>/dev/null)"
        for f in "${db_files[@]}"; do
            [[ -n "$f" ]] && fuser "$f" >/dev/null 2>&1 && return_value=1
        done
        return "$return_value"
    }

    is_ldap_not_running && return

    stop_service_using_pid "$LDAP_PID_FILE"
    if ! retry_while are_db_files_locked "$retries" "$sleep_time"; then
        error "OpenLDAP failed to stop"
        return 1
    fi
}

########################
# Create slapd.ldif — the initial OLC bootstrap config
########################
ldap_create_slapd_file() {
    info "Creating slapd.ldif"
    cat > "${LDAP_SHARE_DIR}/slapd.ldif" << EOF
dn: cn=config
objectClass: olcGlobal
cn: config
olcArgsFile: /var/run/slapd/slapd.args
olcPidFile: /var/run/slapd/slapd.pid

dn: cn=module,cn=config
cn: module
objectClass: olcModuleList
olcModulePath: /usr/lib/ldap
olcModuleLoad: pw-sha2.so
olcModuleLoad: back_mdb.so

dn: cn=schema,cn=config
objectClass: olcSchemaConfig
cn: schema

include: file:///etc/ldap/schema/core.ldif

dn: olcDatabase=frontend,cn=config
objectClass: olcDatabaseConfig
objectClass: olcFrontendConfig
olcDatabase: frontend

dn: olcDatabase=config,cn=config
objectClass: olcDatabaseConfig
olcDatabase: config
olcAccess: to * by dn.base="gidNumber=0+uidNumber=0,cn=peercred,cn=external,cn=auth" manage by * none

dn: olcDatabase=monitor,cn=config
objectClass: olcDatabaseConfig
olcDatabase: monitor
olcAccess: to * by dn.base="gidNumber=0+uidNumber=0,cn=peercred,cn=external,cn=auth" read by dn.base="cn=Manager,dc=my-domain,dc=com" read by * none

dn: olcDatabase=mdb,cn=config
objectClass: olcDatabaseConfig
objectClass: olcMdbConfig
olcDatabase: mdb
olcDbMaxSize: 1073741824
olcSuffix: dc=my-domain,dc=com
olcRootDN: cn=Manager,dc=my-domain,dc=com
olcMonitoring: FALSE
olcDbDirectory: /data/openldap/data
olcDbIndex: objectClass eq,pres
olcDbIndex: ou,cn,mail,surname,givenname eq,pres,sub
EOF
}

########################
# Create LDAP online configuration via slapadd
########################
ldap_create_online_configuration() {
    info "Creating LDAP online configuration"

    ldap_create_slapd_file

    # Adjust peercred UID and GID if running as non-root
    if ! am_i_root; then
        replace_in_file "${LDAP_SHARE_DIR}/slapd.ldif" "uidNumber=0" "uidNumber=$(id -u)"
        replace_in_file "${LDAP_SHARE_DIR}/slapd.ldif" "gidNumber=0" "gidNumber=$(id -g)"
    fi

    local -a flags=(-F "$LDAP_ONLINE_CONF_DIR" -n 0 -l "${LDAP_SHARE_DIR}/slapd.ldif")
    if am_i_root; then
        debug_execute su -s /bin/bash "$LDAP_DAEMON_USER" -c "slapadd ${flags[*]}"
    else
        debug_execute slapadd "${flags[@]}"
    fi
}

########################
# Configure LDAP credentials for admin user
########################
ldap_admin_credentials() {
    info "Configuring LDAP admin credentials"
    cat > "${LDAP_SHARE_DIR}/admin.ldif" << EOF
dn: olcDatabase={2}mdb,cn=config
changetype: modify
replace: olcSuffix
olcSuffix: $LDAP_SUFFIX

dn: olcDatabase={2}mdb,cn=config
changetype: modify
replace: olcRootDN
olcRootDN: $LDAP_ADMIN_DN

dn: olcDatabase={2}mdb,cn=config
changeType: modify
add: olcRootPW
olcRootPW: $LDAP_ENCRYPTED_ADMIN_PASSWORD

dn: olcDatabase={1}monitor,cn=config
changetype: modify
replace: olcAccess
olcAccess: {0}to * by dn.base="gidNumber=0+uidNumber=0,cn=peercred,cn=external, cn=auth" read by dn.base="${LDAP_ADMIN_DN}" read by * none
EOF
    if is_boolean_yes "$LDAP_CONFIG_ADMIN_ENABLED"; then
        cat >> "${LDAP_SHARE_DIR}/admin.ldif" << EOF

dn: olcDatabase={0}config,cn=config
changetype: modify
add: olcRootDN
olcRootDN: $LDAP_CONFIG_ADMIN_DN

dn: olcDatabase={0}config,cn=config
changetype: modify
add: olcRootPW
olcRootPW: $LDAP_ENCRYPTED_CONFIG_ADMIN_PASSWORD
EOF
    fi
    debug_execute ldapmodify -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/admin.ldif"
}

########################
# Disable LDAP anonymous bindings
########################
ldap_disable_anon_binding() {
    info "Disabling LDAP anonymous binding"
    cat > "${LDAP_SHARE_DIR}/disable_anon_bind.ldif" << EOF
dn: cn=config
changetype: modify
add: olcDisallows
olcDisallows: bind_anon

dn: cn=config
changetype: modify
add: olcRequires
olcRequires: authc
EOF
    debug_execute ldapmodify -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/disable_anon_bind.ldif"
}

########################
# Add LDAP schemas
########################
ldap_add_schemas() {
    info "Adding LDAP extra schemas"
    read -r -a schemas <<< "$(tr ',;' ' ' <<< "${LDAP_EXTRA_SCHEMAS}")"
    for schema in "${schemas[@]}"; do
        debug_execute ldapadd -Y EXTERNAL -H "ldapi:///" -f "${LDAP_CONF_DIR}/schema/${schema}.ldif"
    done
}

########################
# Add custom schema from file
########################
ldap_add_custom_schema() {
    info "Adding custom schema: $LDAP_CUSTOM_SCHEMA_FILE"
    debug_execute slapadd -F "$LDAP_ONLINE_CONF_DIR" -n 0 -l "$LDAP_CUSTOM_SCHEMA_FILE"
    ldap_stop
    while is_ldap_running; do sleep 1; done
    ldap_start_bg
}

########################
# Add custom schemas from directory
########################
ldap_add_custom_schemas() {
    info "Adding custom schemas from: $LDAP_CUSTOM_SCHEMA_DIR"
    find "$LDAP_CUSTOM_SCHEMA_DIR" -maxdepth 1 \( -type f -o -type l \) -iname '*.ldif' -print0 | sort -z | while IFS= read -r -d '' schema_file; do
        debug_execute slapadd -F "$LDAP_ONLINE_CONF_DIR" -n 0 -l "$schema_file"
    done
    ldap_stop
    while is_ldap_running; do sleep 1; done
    ldap_start_bg
}

########################
# Add opt-in overlays (module loads + olcOverlay entries) generated by the host
# WithOverlay(...) API, mounted at /overlays.ldif. Applied ONLINE here — after the
# custom schemas are loaded (so referenced objectClasses/attributes like umGroupOfNames
# and memberOf exist) and before the data load (so e.g. memberof populates as the seed
# loads). Mirrors how ppolicy/syncprov overlays are configured.
########################
ldap_add_overlays() {
    [[ -s /overlays.ldif ]] || return 0
    info "Adding LDAP overlays from /overlays.ldif"
    debug_execute ldapadd -Y EXTERNAL -H "ldapi:///" -f /overlays.ldif
}

########################
# Create LDAP default tree (root entry, OUs, users, group)
########################
ldap_create_tree() {
    info "Creating LDAP default tree"
    local dc=""
    local o="example"
    read -r -a root <<< "$(tr ',;' ' ' <<< "${LDAP_ROOT}")"
    for attr in "${root[@]}"; do
        if [[ $attr = dc=* ]] && [[ -z "$dc" ]]; then
            dc="${attr:3}"
        elif [[ $attr = o=* ]] && [[ $o = "example" ]]; then
            o="${attr:2}"
        fi
    done
    cat > "${LDAP_SHARE_DIR}/tree.ldif" << EOF
# Root creation
dn: $LDAP_ROOT
objectClass: dcObject
objectClass: organization
dc: $dc
o: $o

dn: ${LDAP_USER_OU/#/ou=},${LDAP_ROOT}
objectClass: organizationalUnit
ou: ${LDAP_USER_OU}

dn: ${LDAP_GROUP_OU/#/ou=},${LDAP_ROOT}
objectClass: organizationalUnit
ou: ${LDAP_GROUP_OU}

EOF
    read -r -a users <<< "$(tr ',;' ' ' <<< "${LDAP_USERS}")"
    read -r -a passwords <<< "$(tr ',;' ' ' <<< "${LDAP_PASSWORDS}")"
    local index=0
    for user in "${users[@]}"; do
        cat >> "${LDAP_SHARE_DIR}/tree.ldif" << EOF
# User $user creation
dn: ${user/#/cn=},${LDAP_USER_OU/#/ou=},${LDAP_ROOT}
cn: User$((index + 1))
sn: Bar$((index + 1))
objectClass: inetOrgPerson
objectClass: posixAccount
objectClass: shadowAccount
userPassword: ${passwords[$index]}
uid: $user
uidNumber: $((index + 1000))
gidNumber: $((index + 1000))
homeDirectory: /home/${user}

EOF
        index=$((index + 1))
    done
    cat >> "${LDAP_SHARE_DIR}/tree.ldif" << EOF
# Group creation
dn: ${LDAP_GROUP/#/cn=},${LDAP_GROUP_OU/#/ou=},${LDAP_ROOT}
cn: $LDAP_GROUP
objectClass: groupOfNames
# User group membership
EOF
    for user in "${users[@]}"; do
        cat >> "${LDAP_SHARE_DIR}/tree.ldif" << EOF
member: ${user/#/cn=},${LDAP_USER_OU/#/ou=},${LDAP_ROOT}
EOF
    done

    debug_execute ldapadd -f "${LDAP_SHARE_DIR}/tree.ldif" -H "ldapi:///" -D "$LDAP_ADMIN_DN" -w "$LDAP_ADMIN_PASSWORD"
}

########################
# Add custom LDIF files from LDAP_CUSTOM_LDIF_DIR
########################
ldap_add_custom_ldifs() {
    info "Loading custom LDIF files..."
    warn "Ignoring LDAP_USERS, LDAP_PASSWORDS, LDAP_USER_OU, LDAP_GROUP_OU and LDAP_GROUP environment variables..."

    # By default a single rejected entry (bad DN, missing parent, schema violation) aborts
    # the whole load — fail-loud is the least-surprising default for a bad seed. Opt into
    # continue-on-error with LDAP_CUSTOM_LDIF_CONTINUE_ON_ERROR=yes (ldapadd -c), which skips
    # past individual failures. In that mode ldapadd runs WITHOUT debug_execute so rejected
    # entries are visible in the container log rather than silently dropped. See issue #3.
    find "$LDAP_CUSTOM_LDIF_DIR" -maxdepth 1 \( -type f -o -type l \) -iname '*.ldif' -print0 | sort -z | while IFS= read -r -d '' ldif_file; do
        if is_boolean_yes "$LDAP_CUSTOM_LDIF_CONTINUE_ON_ERROR"; then
            if ! ldapadd -c -f "$ldif_file" -H "ldapi:///" -D "$LDAP_ADMIN_DN" -w "$LDAP_ADMIN_PASSWORD"; then
                warn "ldapadd rejected one or more entries while loading '$ldif_file' (LDAP_CUSTOM_LDIF_CONTINUE_ON_ERROR=yes); continuing past failures."
            fi
        else
            debug_execute ldapadd -f "$ldif_file" -H "ldapi:///" -D "$LDAP_ADMIN_DN" -w "$LDAP_ADMIN_PASSWORD"
        fi
    done
}

########################
# Configure directory permissions
########################
ldap_configure_permissions() {
    debug "Ensuring expected directories/files exist..."
    for dir in "$LDAP_SHARE_DIR" "$LDAP_DATA_DIR" "$LDAP_ONLINE_CONF_DIR" "/var/run/slapd"; do
        ensure_dir_exists "$dir"
        if am_i_root; then
            chown -R "$LDAP_DAEMON_USER:$LDAP_DAEMON_GROUP" "$dir"
        fi
    done
}

########################
# Initialize OpenLDAP server
########################
ldap_initialize() {
    info "Initializing OpenLDAP..."

    ldap_configure_permissions
    if ! is_dir_empty "$LDAP_DATA_DIR"; then
        info "Using persisted data"
    else
        # Create OpenLDAP online configuration
        ldap_create_online_configuration
        ldap_start_bg
        ldap_admin_credentials
        if ! is_boolean_yes "$LDAP_ALLOW_ANON_BINDING"; then
            ldap_disable_anon_binding
        fi
        # Initialize with schemas
        if is_boolean_yes "$LDAP_ADD_SCHEMAS"; then
            ldap_add_schemas
        fi
        if [[ -f "$LDAP_CUSTOM_SCHEMA_FILE" ]]; then
            ldap_add_custom_schema
        fi
        if [[ -d "$LDAP_CUSTOM_SCHEMA_DIR" ]] && ! is_dir_empty "$LDAP_CUSTOM_SCHEMA_DIR"; then
            ldap_add_custom_schemas
        fi
        # Overlays (e.g. memberof) — after schemas exist, before data loads.
        ldap_add_overlays
        # Additional configuration
        if [[ ! "$LDAP_PASSWORD_HASH" == "{SSHA}" ]]; then
            ldap_configure_password_hash
        fi
        if is_boolean_yes "$LDAP_CONFIGURE_PPOLICY"; then
            ldap_configure_ppolicy
        fi
        if is_boolean_yes "$LDAP_ENABLE_ACCESSLOG"; then
            ldap_enable_accesslog
        fi
        if is_boolean_yes "$LDAP_ENABLE_SYNCPROV"; then
            ldap_enable_syncprov
        fi
        # Load custom LDIFs or default tree
        if [[ -d "$LDAP_CUSTOM_LDIF_DIR" ]] && ! is_dir_empty "$LDAP_CUSTOM_LDIF_DIR"; then
            ldap_add_custom_ldifs
        elif ! is_boolean_yes "$LDAP_SKIP_DEFAULT_TREE"; then
            ldap_create_tree
        else
            info "Skipping default schemas/tree structure"
        fi
        # Enable TLS
        if is_boolean_yes "$LDAP_ENABLE_TLS"; then
            ldap_configure_tls
            if is_boolean_yes "$LDAP_REQUIRE_TLS"; then
                ldap_configure_tls_required
            fi
        fi
        ldap_stop
    fi
}

########################
# Run custom initialization scripts from /docker-entrypoint-initdb.d/
########################
ldap_custom_init_scripts() {
    if [[ -n $(find /docker-entrypoint-initdb.d/ -type f -regex ".*\.\(sh\)" 2>/dev/null) ]] && [[ ! -f "$LDAP_DATA_DIR/.user_scripts_initialized" ]]; then
        info "Loading user's custom files from /docker-entrypoint-initdb.d"
        for f in /docker-entrypoint-initdb.d/*; do
            debug "Executing $f"
            case "$f" in
                *.sh)
                    if [[ -x "$f" ]]; then
                        if ! "$f"; then
                            error "Failed executing $f"
                            return 1
                        fi
                    else
                        warn "Sourcing $f as it is not executable by the current user"
                        . "$f"
                    fi
                    ;;
                *)
                    warn "Skipping $f, supported formats are: .sh"
                    ;;
            esac
        done
        touch "$LDAP_DATA_DIR/.user_scripts_initialized"
    fi
}

########################
# Configure TLS
########################
ldap_configure_tls() {
    info "Configuring TLS"
    cat > "${LDAP_SHARE_DIR}/certs.ldif" << EOF
dn: cn=config
changetype: modify
replace: olcTLSCACertificateFile
olcTLSCACertificateFile: $LDAP_TLS_CA_FILE
-
replace: olcTLSCertificateFile
olcTLSCertificateFile: $LDAP_TLS_CERT_FILE
-
replace: olcTLSCertificateKeyFile
olcTLSCertificateKeyFile: $LDAP_TLS_KEY_FILE
-
replace: olcTLSVerifyClient
olcTLSVerifyClient: $LDAP_TLS_VERIFY_CLIENTS
EOF
    if [[ -f "$LDAP_TLS_DH_PARAMS_FILE" ]]; then
        cat >> "${LDAP_SHARE_DIR}/certs.ldif" << EOF
-
replace: olcTLSDHParamFile
olcTLSDHParamFile: $LDAP_TLS_DH_PARAMS_FILE
EOF
    fi
    debug_execute ldapmodify -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/certs.ldif"
}

########################
# Require TLS for connections
########################
ldap_configure_tls_required() {
    info "Configuring LDAP connections to require TLS"
    cat > "${LDAP_SHARE_DIR}/tls_required.ldif" << EOF
dn: cn=config
changetype: modify
add: olcSecurity
olcSecurity: tls=1
EOF
    debug_execute ldapmodify -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/tls_required.ldif"
}

########################
# Load an overlay module
########################
ldap_load_module() {
    local module_path="${1:?module path required}"
    local module_file="${2:?module file required}"
    info "Loading LDAP module $module_file from $module_path"
    cat > "${LDAP_SHARE_DIR}/enable_module_${module_file}.ldif" << EOF
dn: cn=module,cn=config
cn: module
objectClass: olcModuleList
olcModulePath: $module_path
olcModuleLoad: $module_file
EOF
    debug_execute ldapadd -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/enable_module_${module_file}.ldif"
}

########################
# Configure ppolicy overlay
########################
ldap_configure_ppolicy() {
    info "Configuring LDAP ppolicy"
    ldap_load_module "/usr/lib/ldap" "ppolicy.so"
    cat > "${LDAP_SHARE_DIR}/ppolicy_create_configuration.ldif" << EOF
dn: olcOverlay={0}ppolicy,olcDatabase={2}mdb,cn=config
objectClass: olcOverlayConfig
objectClass: olcPPolicyConfig
olcOverlay: {0}ppolicy
EOF
    debug_execute ldapadd -Q -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/ppolicy_create_configuration.ldif"

    if is_boolean_yes "$LDAP_PPOLICY_HASH_CLEARTEXT"; then
        info "Enabling ppolicy_hash_cleartext"
        cat > "${LDAP_SHARE_DIR}/ppolicy_hash_cleartext.ldif" << EOF
dn: olcOverlay={0}ppolicy,olcDatabase={2}mdb,cn=config
changetype: modify
add: olcPPolicyHashCleartext
olcPPolicyHashCleartext: TRUE
EOF
        debug_execute ldapmodify -Q -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/ppolicy_hash_cleartext.ldif"
    fi

    if is_boolean_yes "$LDAP_PPOLICY_USE_LOCKOUT"; then
        info "Enabling ppolicy_use_lockout"
        cat > "${LDAP_SHARE_DIR}/ppolicy_use_lockout.ldif" << EOF
dn: olcOverlay={0}ppolicy,olcDatabase={2}mdb,cn=config
changetype: modify
add: olcPPolicyUseLockout
olcPPolicyUseLockout: TRUE
EOF
        debug_execute ldapmodify -Q -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/ppolicy_use_lockout.ldif"
    fi
}

########################
# Configure olcPasswordHash
########################
ldap_configure_password_hash() {
    info "Configuring LDAP olcPasswordHash"
    cat > "${LDAP_SHARE_DIR}/password_hash.ldif" << EOF
dn: olcDatabase={-1}frontend,cn=config
changetype: modify
add: olcPasswordHash
olcPasswordHash: $LDAP_PASSWORD_HASH
EOF
    debug_execute ldapmodify -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/password_hash.ldif"
}

########################
# Enable access logging overlay
########################
ldap_enable_accesslog() {
    info "Configuring Access Logging"
    cat > "${LDAP_SHARE_DIR}/accesslog_add_indexes.ldif" << EOF
dn: olcDatabase={2}mdb,cn=config
changetype: modify
add: olcDbIndex
olcDbIndex: entryCSN eq
-
add: olcDbIndex
olcDbIndex: entryUUID eq
EOF
    debug_execute ldapmodify -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/accesslog_add_indexes.ldif"

    ldap_load_module "/usr/lib/ldap" "accesslog.so"

    cat > "${LDAP_SHARE_DIR}/accesslog_create_db.ldif" << EOF
dn: olcDatabase={3}mdb,cn=config
objectClass: olcDatabaseConfig
objectClass: olcMdbConfig
olcDatabase: {3}mdb
olcDbDirectory: $LDAP_ACCESSLOG_DATA_DIR
olcSuffix: $LDAP_ACCESSLOG_DB
olcRootDN: $LDAP_ACCESSLOG_ADMIN_DN
olcRootPW: $LDAP_ENCRYPTED_ACCESSLOG_ADMIN_PASSWORD
olcDbIndex: default eq
olcDbIndex: entryCSN,objectClass,reqEnd,reqResult,reqStart
EOF
    mkdir -p "$LDAP_ACCESSLOG_DATA_DIR"
    debug_execute ldapadd -Q -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/accesslog_create_db.ldif"

    cat > "${LDAP_SHARE_DIR}/accesslog_create_overlay.ldif" << EOF
dn: olcOverlay=accesslog,olcDatabase={2}mdb,cn=config
objectClass: olcOverlayConfig
objectClass: olcAccessLogConfig
olcOverlay: accesslog
olcAccessLogDB: $LDAP_ACCESSLOG_DB
olcAccessLogOps: $LDAP_ACCESSLOG_LOGOPS
olcAccessLogSuccess: $LDAP_ACCESSLOG_LOGSUCCESS
olcAccessLogPurge: $LDAP_ACCESSLOG_LOGPURGE
olcAccessLogOld: $LDAP_ACCESSLOG_LOGOLD
olcAccessLogOldAttr: $LDAP_ACCESSLOG_LOGOLDATTR
EOF
    debug_execute ldapadd -Q -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/accesslog_create_overlay.ldif"
}

########################
# Enable sync provider overlay
########################
ldap_enable_syncprov() {
    info "Configuring Sync Provider"
    ldap_load_module "/usr/lib/ldap" "syncprov.so"
    cat > "${LDAP_SHARE_DIR}/syncprov_create_overlay.ldif" << EOF
dn: olcOverlay=syncprov,olcDatabase={2}mdb,cn=config
objectClass: olcOverlayConfig
objectClass: olcSyncProvConfig
olcOverlay: syncprov
olcSpCheckpoint: $LDAP_SYNCPROV_CHECKPPOINT
olcSpSessionLog: $LDAP_SYNCPROV_SESSIONLOG
EOF
    debug_execute ldapadd -Q -Y EXTERNAL -H "ldapi:///" -f "${LDAP_SHARE_DIR}/syncprov_create_overlay.ldif"
}
