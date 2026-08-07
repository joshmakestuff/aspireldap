# OpenLDAP 2.6 on Debian 13 (Trixie)
# Forked from Bitnami OpenLDAP, rewritten to use stock Debian packages.
# SPDX-License-Identifier: Apache-2.0

# Base image pinned by multi-arch index digest (#59): a floating tag meant two clean builds
# of the same released package could start from different bases, while Aspire's local image
# tag — content-addressed from this build context — kept serving a stale image because the
# context never changed. Editing the digest below changes the context, so a base refresh
# both is a reviewable source change AND invalidates every local cache.
#
# Update policy — deliberate, source-controlled:
#   1. docker buildx imagetools inspect debian:trixie-slim    -> copy the top-level Digest
#   2. update the digest below and open a PR; CI's container integration tests plus the
#      slapd 2.6 assertion further down gate the bump.
# Refresh whenever Debian publishes security updates relevant to the installed set (or
# before a release at the latest) — a pin must not become permanent staleness.
#
# apt package versions are intentionally NOT locked: they resolve at build time, so a
# rebuild can pick up Debian point-release security fixes without a source change. For a
# dev-time image that drift is accepted; the digest fixes the base, the slapd assertion
# pins the 2.6 contract, and the integration suite gates behavior.
FROM debian:trixie-slim@sha256:3a39a0592364683e6bab97937b72cad5a8fa6dcbbee90edb3bb48c7f8e94f258

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

# The directory name, image label, and docs all advertise OpenLDAP 2.6 — fail the build if
# the base image ever drifts to a different major.minor (slapd -VV exits nonzero by design).
RUN (/usr/sbin/slapd -VV 2>&1 || true) | grep -E 'slapd 2\.6' \
    || { echo "FATAL: installed slapd is not OpenLDAP 2.6:"; (/usr/sbin/slapd -VV 2>&1 || true); exit 1; }

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
