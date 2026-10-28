#!/usr/bin/env bash
# Installs a plain Asterisk for ghostline's managed PBX mode (pbx.mode: managed)
# on Debian 13: Asterisk 22 from source, chan_quectel and chan_dongle with the
# ghostline patches, fail2ban on the Asterisk security log, and the base configs
# that include the files ghostline generates.
#
# Run as root. Safe to run again: existing configs are never overwritten, and
# Asterisk is rebuilt only when the version differs (or with --rebuild).
# See docs/asterisk-setup.md.
set -euo pipefail
export LC_ALL=C.UTF-8 DEBIAN_FRONTEND=noninteractive

ASTERISK_VERSION=22.11.0
QUECTEL_REPO=https://github.com/IchthysMaranatha/asterisk-chan-quectel.git
QUECTEL_COMMIT=3d45c7f
DONGLE_REPO=https://github.com/wdoekes/asterisk-chan-dongle.git
DONGLE_COMMIT=7db8e75
GHOSTLINE_RAW=https://raw.githubusercontent.com/rsvln/ghostline

AMI_PERMIT=
CONF_DIR=/etc/asterisk/ghostline
CDR_DIR=/var/spool/asterisk/gl-cdr
REC_DIR=/var/spool/asterisk/monitor
SIP_NAME=$(hostname -f 2>/dev/null || hostname)
WITH_QUECTEL=1
WITH_DONGLE=1
REBUILD=0
GHOSTLINE_REF=master
SRC=/usr/src/ghostline-asterisk
JOBS=$(nproc)

usage() {
  cat <<EOF
Usage: $0 --ami-permit <ghostline address>[/bits] [options]

  --ami-permit ADDR     address or network ghostline connects to AMI from (required)
  --version V           Asterisk version (default $ASTERISK_VERSION)
  --conf-dir DIR        where ghostline writes its files, as Asterisk sees it
                        (default $CONF_DIR; pbx.asterisk_dir in ghostline.yaml)
  --cdr-dir DIR         directory of the SQLite CDR file (default $CDR_DIR)
  --sip-name NAME       name in the self-signed certificate (default $SIP_NAME)
  --no-quectel          do not build chan_quectel
  --no-dongle           do not build chan_dongle
  --rebuild             rebuild Asterisk and the channel drivers
  --ghostline-ref REF   git ref to fetch the patches from when this script
                        runs outside a ghostline checkout (default $GHOSTLINE_REF)
EOF
  exit "${1:-0}"
}

while [ $# -gt 0 ]; do
  case "$1" in
    --ami-permit) AMI_PERMIT=$2; shift ;;
    --version) ASTERISK_VERSION=$2; shift ;;
    --conf-dir) CONF_DIR=$2; shift ;;
    --cdr-dir) CDR_DIR=$2; shift ;;
    --sip-name) SIP_NAME=$2; shift ;;
    --no-quectel) WITH_QUECTEL=0 ;;
    --no-dongle) WITH_DONGLE=0 ;;
    --rebuild) REBUILD=1 ;;
    --ghostline-ref) GHOSTLINE_REF=$2; shift ;;
    -h|--help) usage ;;
    *) echo "Unknown option: $1" >&2; usage 1 ;;
  esac
  shift
done

log() { printf '\n==> %s\n' "$*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

[ "$(id -u)" = 0 ] || die "run as root"
[ -n "$AMI_PERMIT" ] || die "--ami-permit is required (the address ghostline connects from)"
[[ "$AMI_PERMIT" =~ ^[0-9a-fA-F.:]+(/[0-9]{1,3})?$ ]] || die "--ami-permit: not an address: $AMI_PERMIT"
for d in "$CONF_DIR" "$CDR_DIR"; do
  [[ "$d" =~ ^/[A-Za-z0-9._/-]+$ ]] || die "not a plain absolute path: $d"
done
. /etc/os-release
[ "${ID:-}" = debian ] || echo "WARNING: written for Debian 13, this is ${PRETTY_NAME:-unknown}"

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
MODDIR=/usr/lib/asterisk/modules

# Writes stdin to a config file unless it already exists.
put_conf() {
  local f=$1 mode=${2:-640}
  if [ -e "$f" ]; then
    echo "  keep $f (exists)"
    cat >/dev/null
    return
  fi
  cat >"$f"
  chown asterisk:asterisk "$f"
  chmod "$mode" "$f"
  echo "  wrote $f"
}

# Prints the path of a ghostline patch, from the checkout or downloaded.
patch_file() {
  local name=$1
  if [ -f "$SCRIPT_DIR/../docs/$name" ]; then
    echo "$SCRIPT_DIR/../docs/$name"
  else
    curl -fsSL "$GHOSTLINE_RAW/$GHOSTLINE_REF/docs/$name" -o "$SRC/$name"
    echo "$SRC/$name"
  fi
}

# Prints the path of a file from scripts/, from the checkout or downloaded.
script_file() {
  local name=$1
  if [ -f "$SCRIPT_DIR/$name" ]; then
    echo "$SCRIPT_DIR/$name"
  else
    curl -fsSL "$GHOSTLINE_RAW/$GHOSTLINE_REF/scripts/$name" -o "$SRC/$name"
    echo "$SRC/$name"
  fi
}

log "Packages"
apt-get update -q
apt-get install -y -q --no-install-recommends \
  build-essential pkg-config autoconf automake libtool git curl ca-certificates \
  bzip2 patch openssl libedit-dev libjansson-dev libxml2-dev uuid-dev \
  libsqlite3-dev libssl-dev libsrtp2-dev libasound2-dev libncurses-dev \
  sqlite3 fail2ban nftables python3-systemd logrotate

log "User"
if ! id asterisk >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/asterisk --shell /usr/sbin/nologin asterisk
fi
# Serial ports and the UAC sound cards of the modems.
usermod -aG dialout,audio asterisk

mkdir -p "$SRC"

log "Asterisk $ASTERISK_VERSION"
current=$( (asterisk -V 2>/dev/null || true) | awk '{print $2}')
if [ "$current" = "$ASTERISK_VERSION" ] && [ "$REBUILD" = 0 ]; then
  echo "  already installed"
else
  cd "$SRC"
  tarball=asterisk-$ASTERISK_VERSION.tar.gz
  base=https://downloads.asterisk.org/pub/telephony/asterisk/releases
  curl -fsSL "$base/$tarball" -o "$tarball"
  curl -fsSL "$base/asterisk-$ASTERISK_VERSION.sha256" -o "$tarball.sha256"
  sha256sum -c "$tarball.sha256"
  rm -rf "asterisk-$ASTERISK_VERSION"
  tar xzf "$tarball"
  cd "asterisk-$ASTERISK_VERSION"
  # libdir pinned: configure picks /usr/lib64 when it exists, and the channel
  # drivers below install into $MODDIR.
  ./configure --prefix=/usr --libdir=/usr/lib --sysconfdir=/etc --localstatedir=/var \
    --with-pjproject-bundled >/dev/null
  make menuselect.makeopts >/dev/null
  # Portable binary; no CSV CDR and no unused channel drivers.
  menuselect/menuselect --disable BUILD_NATIVE \
    --enable cdr_sqlite3_custom --enable res_srtp \
    --disable cdr_csv --disable cdr_custom --disable cdr_manager \
    --disable cel_custom --disable cel_manager --disable cel_sqlite3_custom \
    --disable chan_iax2 --disable chan_unistim \
    --disable res_phoneprov --disable res_pjsip_phoneprov_provider --disable pbx_dundi \
    menuselect.makeopts
  make -j"$JOBS" >/dev/null
  # Headers too: the channel drivers are built against them.
  make install install-headers >/dev/null
  ldconfig
fi
asterisk -V

build_driver() {
  local name=$1 repo=$2 commit=$3; shift 3
  local dir=$SRC/$name
  if [ -f "$MODDIR/$name.so" ] && [ "$REBUILD" = 0 ] && [ "$current" = "$ASTERISK_VERSION" ]; then
    echo "  $name.so already installed"
    return
  fi
  [ -d "$dir/.git" ] || git clone -q "$repo" "$dir"
  cd "$dir"
  git fetch -q origin
  git reset -q --hard "$commit"
  git clean -qfdx
  local p
  for p in "$@"; do
    git apply "$(patch_file "$p")"
    echo "  applied $p"
  done
  # Old code with many compiler warnings: the output goes to a log.
  local blog=$SRC/$name.build.log
  # set -e does not apply left of ||, hence the && chain.
  { ./bootstrap &&
    ./configure --with-asterisk=/usr/include --with-astversion="$ASTERISK_VERSION" \
      DESTDIR="$MODDIR" &&
    make -j"$JOBS" &&
    make install
  } >"$blog" 2>&1 || { tail -30 "$blog"; die "$name build failed, see $blog"; }
  echo "  installed $MODDIR/$name.so"
}

if [ "$WITH_QUECTEL" = 1 ]; then
  log "chan_quectel"
  build_driver chan_quectel "$QUECTEL_REPO" "$QUECTEL_COMMIT" \
    chan_quectel-noqpcmv.patch chan_quectel-line-monitoring.patch
fi
if [ "$WITH_DONGLE" = 1 ]; then
  log "chan_dongle"
  build_driver chan_dongle "$DONGLE_REPO" "$DONGLE_COMMIT" chan_dongle-sysinfo.patch
fi

log "Directories"
mkdir -p /etc/asterisk "$CONF_DIR/keys" "$CDR_DIR" "$REC_DIR" \
  /var/lib/asterisk /var/log/asterisk /var/spool/asterisk
chown -R asterisk:asterisk /etc/asterisk /var/lib/asterisk /var/log/asterisk /var/spool/asterisk \
  "$CONF_DIR" "$CDR_DIR"

log "Configs"
cd /etc/asterisk

put_conf asterisk.conf <<'EOF'
[directories]
astetcdir => /etc/asterisk
astmoddir => /usr/lib/asterisk/modules
astvarlibdir => /var/lib/asterisk
astdbdir => /var/lib/asterisk
astkeydir => /var/lib/asterisk
astdatadir => /var/lib/asterisk
astagidir => /var/lib/asterisk/agi-bin
astspooldir => /var/spool/asterisk
astrundir => /var/run/asterisk
astlogdir => /var/log/asterisk
astsbindir => /usr/sbin

[options]
EOF

put_conf modules.conf <<'EOF'
[modules]
autoload=yes
; Not used here. CDR goes to SQLite only (cdr_sqlite3_custom.conf).
noload => cdr_csv.so
noload => cdr_custom.so
noload => cdr_manager.so
noload => cel_custom.so
noload => cel_manager.so
noload => cel_sqlite3_custom.so
noload => chan_iax2.so
noload => chan_unistim.so
noload => pbx_dundi.so
noload => res_phoneprov.so
noload => res_pjsip_phoneprov_provider.so
noload => res_pjsip_config_wizard.so
noload => app_voicemail.so
noload => app_minivm.so
noload => app_followme.so
noload => app_festival.so
noload => app_adsiprog.so
noload => app_getcpeid.so
noload => res_config_sqlite3.so
noload => res_stun_monitor.so
noload => res_prometheus.so
noload => pbx_ael.so
EOF

put_conf logger.conf <<'EOF'
[general]

[logfiles]
console => notice,warning,error
messages => notice,warning,error
; Failed SIP attempts; fail2ban reads this file.
security => security
EOF

# unanswered: without it a call to a phone that is not registered (Dial fails
# at once) leaves no CDR, and the missed call is lost.
put_conf cdr.conf <<'EOF'
[general]
enable=yes
unanswered=yes
congestion=yes
EOF

put_conf cdr_sqlite3_custom.conf <<EOF
; The call journal of ghostline (calls.cdr_sqlite). See docs/cdr-sqlite.md.
[$CDR_DIR/cdr.db]
table => cdr
columns => calldate, uniqueid, linkedid, src, dst, did, dcontext, channel, dstchannel, lastapp, disposition, duration, billsec, recordingfile
values => '\${CDR(start)}','\${CDR(uniqueid)}','\${CDR(linkedid)}','\${CDR(src)}','\${CDR(dst)}','\${CDR(did)}','\${CDR(dcontext)}','\${CDR(channel)}','\${CDR(dstchannel)}','\${CDR(lastapp)}','\${CDR(disposition)}','\${CDR(duration)}','\${CDR(billsec)}','\${CDR(recordingfile)}'
busy_timeout => 5000
EOF

put_conf rtp.conf <<'EOF'
[general]
rtpstart=10000
rtpend=20000
strictrtp=yes
EOF

put_conf pjsip.conf <<EOF
; Phones and transports are generated by ghostline (PBX tab, Apply).
; Do not add other endpoints here: requests that match no phone are rejected.
[global]
type=global
user_agent=Asterisk
#include $CONF_DIR/pjsip_ghostline.conf
EOF

put_conf extensions.conf <<EOF
; The dialplan is generated by ghostline (PBX tab, Apply).
[general]
static=yes
writeprotect=yes

[globals]

#include $CONF_DIR/extensions_ghostline.conf
EOF

ami_new=0
if [ ! -e manager.conf ]; then
  ami_secret=$(openssl rand -hex 16)
  ami_new=1
fi
put_conf manager.conf 600 <<EOF
[general]
enabled = yes
port = 5038
bindaddr = 0.0.0.0

[ghostline]
secret = ${ami_secret:-}
deny = 0.0.0.0/0.0.0.0
permit = $AMI_PERMIT
read = system,call,user,message,reporting,security
write = command,originate,message,reporting
writetimeout = 5000
EOF

# The modems are set up on ghostline's PBX tab and written into the included files.
put_conf quectel.conf <<EOF
; chan_quectel. The devices are added on ghostline's PBX tab (Modems) and
; written into the included file. Ports by path only: never discovery by
; IMEI/IMSI, it sends AT commands to every ttyUSB on the host.
[general]
interval=15

#include $CONF_DIR/quectel_ghostline.conf
EOF

put_conf dongle.conf <<EOF
; chan_dongle (Huawei sticks). The devices are added on ghostline's PBX tab
; (Modems) and written into the included file.
[general]
interval=15

[defaults]
group=3
rxgain=0
txgain=0
autodeletesms=yes
resetdongle=no
u2diag=-1
disablesms=no
smsaspdu=yes
initstate=start
dtmf=relax

#include $CONF_DIR/dongle_ghostline.conf
EOF

# Placeholders until the first Apply; ghostline replaces them.
cd "$CONF_DIR"
put_conf pjsip_ghostline.conf 664 <<'EOF'
; Written by ghostline on Apply.
EOF
put_conf extensions_ghostline.conf 664 <<'EOF'
; Written by ghostline on Apply.
EOF
put_conf quectel_ghostline.conf 664 <<'EOF'
; Written by ghostline on Apply.
EOF
put_conf dongle_ghostline.conf 664 <<'EOF'
; Written by ghostline on Apply.
EOF

log "TLS certificate"
if [ -e keys/sip.crt ]; then
  echo "  keep $CONF_DIR/keys/sip.crt (exists)"
else
  # Self-signed, so TLS starts; replace with a real one (pbx.tls.cert/key).
  openssl req -x509 -newkey rsa:2048 -nodes -days 365 -subj "/CN=$SIP_NAME" \
    -keyout keys/sip.key -out keys/sip.crt 2>/dev/null
  chown asterisk:asterisk keys/sip.key keys/sip.crt
  chmod 640 keys/sip.key
  chmod 644 keys/sip.crt
  echo "  wrote self-signed $CONF_DIR/keys/sip.crt for $SIP_NAME"
fi

log "USB modem list"
# The PBX tab offers modems from <conf-dir>/usb.json. The script reads sysfs only.
install -m 755 "$(script_file gl-usb-inventory.py)" /usr/local/bin/gl-usb-inventory
cat >/etc/systemd/system/gl-usb-inventory.service <<EOF
[Unit]
Description=USB modem list for ghostline

[Service]
Type=oneshot
User=asterisk
Group=asterisk
SupplementaryGroups=dialout audio
ExecStart=/usr/local/bin/gl-usb-inventory $CONF_DIR/usb.json
EOF
cat >/etc/systemd/system/gl-usb-inventory.timer <<'EOF'
[Unit]
Description=USB modem list for ghostline, every 30 s

[Timer]
OnBootSec=10
OnUnitActiveSec=30
AccuracySec=5

[Install]
WantedBy=timers.target
EOF

log "systemd"
cat >/etc/systemd/system/asterisk.service <<'EOF'
[Unit]
Description=Asterisk PBX
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=asterisk
Group=asterisk
SupplementaryGroups=dialout audio
RuntimeDirectory=asterisk
ExecStart=/usr/sbin/asterisk -f -C /etc/asterisk/asterisk.conf
ExecReload=/usr/sbin/asterisk -rx "core reload"
ExecStop=/usr/sbin/asterisk -rx "core stop gracefully"
Restart=on-failure
RestartSec=5
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF

cat >/etc/logrotate.d/asterisk <<'EOF'
/var/log/asterisk/messages /var/log/asterisk/security {
    weekly
    rotate 8
    missingok
    notifempty
    compress
    delaycompress
    create 640 asterisk asterisk
    sharedscripts
    postrotate
        /usr/sbin/asterisk -rx "logger reload" >/dev/null 2>&1 || true
    endscript
}
EOF

systemctl daemon-reload
systemctl enable asterisk >/dev/null 2>&1
systemctl enable --now gl-usb-inventory.timer >/dev/null 2>&1
systemctl restart asterisk

log "fail2ban"
cat >/etc/fail2ban/jail.d/asterisk-ghostline.local <<'EOF'
# Bans addresses that fail SIP authentication, from the Asterisk security log.
# The ban covers all ports: TLS, UDP (pbx.udp_port) and RTP.
[asterisk]
enabled = true
backend = polling
logpath = /var/log/asterisk/security
banaction = %(banaction_allports)s
maxretry = 5
findtime = 10m
bantime = 1h
EOF
touch /var/log/asterisk/security
chown asterisk:asterisk /var/log/asterisk/security
systemctl enable fail2ban >/dev/null 2>&1
systemctl restart fail2ban

log "Check"
for i in $(seq 1 20); do
  asterisk -rx "core show version" >/dev/null 2>&1 && break
  sleep 1
done
asterisk -rx "core show version"
for m in res_pjsip.so res_srtp.so cdr_sqlite3_custom.so res_security_log.so app_mixmonitor.so \
         chan_quectel.so chan_dongle.so; do
  if asterisk -rx "module show like $m" | grep -q "^$m"; then st=loaded; else st="NOT LOADED"; fi
  printf '  %-24s %s
' "$m" "$st"
done
for i in $(seq 1 20); do
  fail2ban-client ping >/dev/null 2>&1 && break
  sleep 1
done
fail2ban-client status asterisk | sed 's/^/  /'

log "Done"
if [ "$ami_new" = 1 ]; then
  echo "AMI user 'ghostline', password (put it into ghostline.yaml, gateways[].password):"
  echo "  $ami_secret"
fi
cat <<EOF

Next (docs/asterisk-setup.md):
  - share $CONF_DIR with ghostline (pbx.conf_dir) and $CDR_DIR (calls.cdr_sqlite),
    and $REC_DIR for the recordings (calls.recordings_dir);
  - pass the modems' USB ports into this container if it is one;
  - set pbx.mode: managed in ghostline; add modems, lines and phones on the PBX tab.
EOF
