#!/bin/sh
# Installs or updates ghostline from a release archive on any Linux with systemd:
# an LXC container, a VM or a bare-metal host. No .NET is needed, the binary is self-contained.
#
#   tar xzf ghostline-linux-x64.tar.gz
#   sudo ./ghostline/install.sh
#
# First install: creates the ghostline user, /etc/ghostline (with an example config),
# /var/log/ghostline and the systemd unit, then asks you to edit the config and start the service.
# Update: replaces /opt/ghostline/app, keeps the previous version in /opt/ghostline/app.old
# and rolls back automatically if the new version does not start.
set -eu

SRC=$(cd "$(dirname "$0")" && pwd)
APP=/opt/ghostline/app
CONF=/etc/ghostline
UNIT=/etc/systemd/system/ghostline.service

if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root (sudo $0)" >&2
  exit 1
fi
if [ ! -x "$SRC/ghostline" ]; then
  echo "No ghostline binary next to this script; run it from the unpacked release archive" >&2
  exit 1
fi

if ! id ghostline >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin ghostline
fi
mkdir -p /opt/ghostline "$CONF" /var/log/ghostline
chown ghostline: "$CONF" /var/log/ghostline

first=0
[ -f "$CONF/ghostline.yaml" ] || [ -f "$CONF/ghostline.json" ] || first=1
if [ "$first" = 1 ]; then
  install -m 640 -o ghostline -g ghostline "$SRC/ghostline.example.yaml" "$CONF/ghostline.yaml"
fi

# New version next to the running one, then switch over.
rm -rf "$APP.new"
mkdir -p "$APP.new"
for f in "$SRC"/*; do
  case "$(basename "$f")" in install.sh|ghostline.service|ghostline.example.yaml) ;; *) cp -r "$f" "$APP.new/" ;; esac
done
chmod -R a+rX "$APP.new"
chmod 755 "$APP.new/ghostline"

if ! cmp -s "$SRC/ghostline.service" "$UNIT" 2>/dev/null; then
  install -m 644 "$SRC/ghostline.service" "$UNIT"
  systemctl daemon-reload
fi

running=0
systemctl is-active --quiet ghostline && running=1
[ "$running" = 1 ] && systemctl stop ghostline
rm -rf "$APP.old"
[ -d "$APP" ] && mv "$APP" "$APP.old"
mv "$APP.new" "$APP"
systemctl enable --quiet ghostline

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "Note: ffmpeg is not installed; mode: full needs it (apt install ffmpeg)."
fi

if [ "$first" = 1 ]; then
  echo "Installed $(cat "$APP/VERSION" 2>/dev/null || true) to $APP"
  echo "Edit $CONF/ghostline.yaml, then: systemctl start ghostline"
  echo "Web interface: http://<host>:8889 (the port is web.port in the config)"
  exit 0
fi

systemctl start ghostline
sleep 8
if ! systemctl is-active --quiet ghostline; then
  echo "The new version failed to start, rolling back" >&2
  journalctl -u ghostline --no-pager -n 15 -o cat >&2 || true
  systemctl stop ghostline || true
  rm -rf "$APP.failed"
  mv "$APP" "$APP.failed"
  [ -d "$APP.old" ] && mv "$APP.old" "$APP"
  systemctl start ghostline
  exit 1
fi
echo "Updated, ghostline is $(systemctl is-active ghostline)"
