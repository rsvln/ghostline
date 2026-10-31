#!/usr/bin/env python3
# fail2ban for ghostline (gateway pbx: managed). Runs as root on the Asterisk host, from a timer and
# whenever ghostline writes one of its files. Everything goes through <conf-dir>, shared with ghostline:
#   fail2ban_ghostline.json  (from ghostline's settings) numbers and addresses only; the jail is
#                            rendered here, so nothing from that file reaches fail2ban as raw config
#   fail2ban_request.json    (from ghostline) addresses to unban; handled, then removed
#   fail2ban.json            (written here) the rule in force, the banned addresses and until when
# Usage: gl-fail2ban <conf-dir> [--keep <address or network>...]
# --keep: addresses that are never banned whatever the settings say (ghostline's own, from the install).
import ipaddress, json, os, sqlite3, subprocess, sys, tempfile, time

JAIL = "asterisk"
JAIL_FILE = "/etc/fail2ban/jail.d/asterisk-ghostline.local"
# Ban counts must outlive the longest repeated ban, or a returning address starts from the first one again.
PURGE_FILE = "/etc/fail2ban/fail2ban.d/ghostline.local"
PURGE_TEXT = "# Written by gl-fail2ban: remember bans for a month, for the growing ban time.\n[Definition]\ndbpurgeage = 30d\n"
DB = "/var/lib/fail2ban/fail2ban.sqlite3"


def client(*args):
    r = subprocess.run(["fail2ban-client", *args], capture_output=True, text=True, timeout=60)
    if r.returncode:
        raise RuntimeError((r.stderr or r.stdout).strip() or f"fail2ban-client {' '.join(args)} failed")
    return r.stdout.strip()


def network(s):
    """An address or a network, normalized; ValueError for anything else."""
    return str(ipaddress.ip_network(str(s).strip(), strict=False))


def number(v, lo, hi, default):
    return v if isinstance(v, int) and not isinstance(v, bool) and lo <= v <= hi else default


def render(settings, keep):
    retry = number(settings.get("max_retry"), 1, 100, 5)
    find = number(settings.get("find_minutes"), 1, 1440, 10)
    ban = number(settings.get("ban_minutes"), 1, 525600, 60)
    ignore = ["127.0.0.1/8", "::1"]
    for x in keep + list(settings.get("ignore_ip") or []):
        try:
            n = network(x)
        except ValueError:
            continue
        if n not in ignore:
            ignore.append(n)
    lines = [
        "# Written by gl-fail2ban from ghostline's settings (gateways[].managed.fail2ban); edit it there.",
        "# Bans addresses that fail SIP authentication, from the Asterisk security log, on all ports.",
        f"[{JAIL}]",
        "enabled = true",
        "ignoreip = " + " ".join(ignore),
        "backend = polling",
        "logpath = /var/log/asterisk/security",
        "banaction = %(banaction_allports)s",
        f"maxretry = {retry}",
        f"findtime = {find}m",
        f"bantime = {ban}m",
    ]
    if settings.get("ban_growth") is True:
        # The same address again: the ban time times 24, then times 168 (1 h -> 1 day -> 1 week).
        lines += ["bantime.increment = true", "bantime.multipliers = 1 24 168"]
    return "\n".join(lines) + "\n"


def write_if_changed(path, text):
    try:
        with open(path) as f:
            if f.read() == text:
                return False, None
    except OSError:
        pass
    old = None
    if os.path.exists(path):
        with open(path) as f:
            old = f.read()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)
    return True, old


def apply_settings(conf, keep):
    path = os.path.join(conf, "fail2ban_ghostline.json")
    if not os.path.exists(path):
        return
    with open(path) as f:
        settings = json.load(f)
    purge_changed, _ = write_if_changed(PURGE_FILE, PURGE_TEXT)
    changed, old = write_if_changed(JAIL_FILE, render(settings, keep))
    if not (changed or purge_changed):
        return
    try:
        # The full reload re-reads fail2ban.d too; bans stay.
        client("reload") if purge_changed else client("reload", JAIL)
    except Exception:
        if changed and old is not None:
            with open(JAIL_FILE, "w") as f:
                f.write(old)
            client("reload", JAIL)
        raise


def handle_requests(conf):
    path = os.path.join(conf, "fail2ban_request.json")
    if not os.path.exists(path):
        return []
    errors = []
    try:
        with open(path) as f:
            req = json.load(f)
        for ip in req.get("unban") or []:
            try:
                ip = str(ipaddress.ip_address(str(ip).strip()))
                client("set", JAIL, "unbanip", ip)
            except Exception as e:
                errors.append(f"unban {ip}: {e}")
    finally:
        os.remove(path)
    return errors


def iso(t):
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))


def status(errors):
    st = {"at": iso(time.time()), "errors": errors}
    try:
        lines = client("status", JAIL).splitlines()
        def field(name):
            for l in lines:
                if name in l:
                    return l.split(":", 1)[1].strip()
            return ""
        banned = field("Banned IP list").split()
        st["failed_now"] = int(field("Currently failed") or 0)
        st["total_failed"] = int(field("Total failed") or 0)
        st["total_banned"] = int(field("Total banned") or 0)
        st["max_retry"] = int(client("get", JAIL, "maxretry"))
        st["find_seconds"] = int(client("get", JAIL, "findtime"))
        st["ban_seconds"] = int(client("get", JAIL, "bantime"))
        try:
            st["ban_growth"] = client("get", JAIL, "bantime.increment").strip().lower() == "true"
        except Exception:
            st["ban_growth"] = False
        times = {}
        try:
            db = sqlite3.connect(f"file:{DB}?mode=ro", uri=True, timeout=5)
            for ip, at, secs, count in db.execute(
                    "SELECT ip, timeofban, bantime, bancount FROM bips WHERE jail = ?", (JAIL,)):
                times[ip] = (at, secs, count)
            db.close()
        except Exception as e:
            st["errors"].append(f"ban times: {e}")
        st["banned"] = []
        for ip in banned:
            at, secs, count = times.get(ip, (None, None, None))
            st["banned"].append({
                "ip": ip,
                "since": iso(at) if at else None,
                "until": iso(at + secs) if at and secs is not None and secs >= 0 else None,
                "count": count,
            })
    except Exception as e:
        st["errors"].append(str(e))
    return st


def main():
    if len(sys.argv) < 2:
        sys.exit("usage: gl-fail2ban <conf-dir> [--keep <address>...]")
    conf = sys.argv[1]
    keep = sys.argv[sys.argv.index("--keep") + 1:] if "--keep" in sys.argv else []
    errors = handle_requests(conf)
    try:
        apply_settings(conf, keep)
    except Exception as e:
        errors.append(f"settings not applied: {e}")
    st = status(errors)
    fd, tmp = tempfile.mkstemp(dir=conf, prefix=".fail2ban.", suffix=".tmp")
    with os.fdopen(fd, "w") as f:
        json.dump(st, f, indent=1)
    os.chmod(tmp, 0o644)
    os.replace(tmp, os.path.join(conf, "fail2ban.json"))


if __name__ == "__main__":
    main()
