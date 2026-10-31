#!/bin/sh
# Reload hook of acme.sh for ghostline's SIP certificate, set by gl-acme: runs after every issue or
# renewal. The files live in the directory shared with ghostline (it shows the expiry); PJSIP is
# reloaded to pick up the new key.
# Usage: gl-acme-deploy <cert> <key>
set -e
CRT=$1
KEY=$2
[ -f "$CRT" ] && [ -f "$KEY" ] || { echo "usage: gl-acme-deploy <cert> <key>" >&2; exit 1; }

chown asterisk:asterisk "$CRT" "$KEY"
chmod 644 "$CRT"
chmod 640 "$KEY"

asterisk -rx "module reload res_pjsip.so" >/dev/null 2>&1 || true

logger -t gl-acme "SIP certificate deployed, res_pjsip reloaded"
echo "deployed: $(openssl x509 -in "$CRT" -noout -enddate 2>/dev/null)"
