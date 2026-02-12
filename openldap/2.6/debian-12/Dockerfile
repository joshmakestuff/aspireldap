# OpenLDAP 2.6 on Debian 12 (Bookworm)
# Forked from Bitnami OpenLDAP, rewritten to use stock Debian packages.
# SPDX-License-Identifier: Apache-2.0

FROM debian:bookworm-slim

LABEL org.opencontainers.image.title="openldap" \
      org.opencontainers.image.description="OpenLDAP server based on stock Debian packages" \
      org.opencontainers.image.version="2.6"

ENV DEBIAN_FRONTEND=noninteractive

SHELL ["/bin/bash", "-o", "errexit", "-o", "nounset", "-o", "pipefail", "-c"]

# Install OpenLDAP and utilities from Debian repos.
# Pre-seed debconf to avoid interactive prompts from slapd.
RUN echo 'slapd slapd/no_configuration boolean true' | debconf-set-selections && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
        slapd \
        ldap-utils \
        libsasl2-modules \
        ca-certificates \
        procps \
        psmisc && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Create data and config directories on the volume mount path
RUN mkdir -p /data/openldap/data \
             /data/openldap/slapd.d \
             /tmp/ldap-setup \
             /ldifs \
             /schemas \
             /docker-entrypoint-initdb.d \
             /var/run/slapd && \
    # Symlink the OLC config into /etc/ldap for tools that look there
    ln -sf /data/openldap/slapd.d /etc/ldap/slapd.d && \
    # Ensure the openldap user (created by apt) owns the data dirs
    chown -R openldap:openldap /data/openldap /tmp/ldap-setup /var/run/slapd /ldifs /schemas /docker-entrypoint-initdb.d && \
    chmod -R g+rwX /data/openldap /tmp/ldap-setup /var/run/slapd

# Copy entrypoint scripts
COPY rootfs/ /

# Fix Windows CRLF line endings if building from a Windows git checkout
RUN find /opt/openldap/scripts -type f -name "*.sh" -exec sed -i 's/\r$//' {} + && \
    chmod +x /opt/openldap/scripts/*.sh /opt/openldap/scripts/openldap/*.sh

EXPOSE 1389 1636

USER openldap

ENTRYPOINT ["/opt/openldap/scripts/openldap/entrypoint.sh"]
CMD ["/opt/openldap/scripts/openldap/run.sh"]
