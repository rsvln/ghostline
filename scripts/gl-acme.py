#!/usr/bin/env python3
# Let's Encrypt for ghostline's SIP certificate (gateway pbx: managed). Runs as root on the Asterisk host,
# from a timer and whenever ghostline writes one of its files. Uses acme.sh (HTTP-01, standalone).
# Everything goes through <conf-dir>, shared with ghostline:
#   acme_ghostline.json  (from ghostline's settings) enabled, domain, http_port, cert, key; validated here
#                        again, nothing from it reaches a shell
#   acme_request.json    (from ghostline) {"renew": true}: renew now; handled, then removed
#   acme.json            (written here) the domain, the last try and success, the next renewal, the error
# Renewals are done here, not by acme.sh's own cron, so a failure reaches ghostline (and Telegram).
# A certificate acme.sh already has for the domain is adopted, not issued again.
# Usage: gl-acme <conf-dir>
import json, os, re, subprocess, sys, tempfile, time

ACME_HOME = "/root/.acme.sh"
ACME = os.path.join(ACME_HOME, "acme.sh")
DEPLOY = "/usr/local/bin/gl-acme-deploy"
STATE = "/var/lib/gl-acme/state.json"
RETRY_AFTER = 6 * 3600          # after a failure: Let's Encrypt limits failed validations
DOMAIN_OK = re.compile(r"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")
PATH_OK = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_./-]*$")


def iso(t):
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t)) if t else None


def load(path, default):
    try:
        with open(path) as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def settings(conf):
    s = load(os.path.join(conf, "acme_ghostline.json"), {})
    domain = str(s.get("domain") or "").lower()
    port = s.get("http_port")
    cert, key = str(s.get("cert") or ""), str(s.get("key") or "")
    ok = (s.get("enabled") is True and DOMAIN_OK.match(domain)
          and isinstance(port, int) and not isinstance(port, bool) and 1 <= port <= 65535
          and all(PATH_OK.match(p) and ".." not in p.split("/") for p in (cert, key)))
    return {"enabled": bool(ok), "domain": domain, "port": port, "cert": os.path.join(conf, cert), "key": os.path.join(conf, key)}


def domain_conf(domain):
    return os.path.join(ACME_HOME, f"{domain}_ecc", f"{domain}.conf")


def conf_value(domain, name):
    try:
        with open(domain_conf(domain)) as f:
            for line in f:
                if line.startswith(name + "="):
                    return line.split("=", 1)[1].strip().strip("'\"")
    except OSError:
        pass
    return None


def set_http_port(domain, port):
    # A renewal uses the port saved at issue; follow the setting.
    path = domain_conf(domain)
    with open(path) as f:
        text = f.read()
    new = re.sub(r"^Le_HTTPPort=.*$", f"Le_HTTPPort='{port}'", text, flags=re.M)
    if new != text:
        with open(path, "w") as f:
            f.write(new)


def acme(*args):
    r = subprocess.run([ACME, "--home", ACME_HOME, *args], capture_output=True, text=True, timeout=600)
    out = re.sub(r"\x1b\[[0-9;]*m", "", (r.stdout or "") + (r.stderr or ""))
    return r.returncode, out


def failure(out):
    """The lines of acme.sh's output that say what went wrong, short."""
    lines = [l.split("]", 1)[-1].strip() for l in out.splitlines() if re.search(r"error|fail|invalid|timeout", l, re.I)]
    last = out.strip().splitlines()[-1:] or ["acme.sh failed"]
    return (" / ".join(dict.fromkeys(l for l in lines if l)) or last[0])[:400]


def install(s):
    # Copies the certificate into conf_dir and makes gl-acme-deploy the reload hook of every renewal.
    code, out = acme("--install-cert", "-d", s["domain"], "--ecc",
                     "--fullchain-file", s["cert"], "--key-file", s["key"],
                     "--reloadcmd", f"{DEPLOY} {s['cert']} {s['key']}")
    if code:
        raise RuntimeError(failure(out))


def run(conf):
    s = settings(conf)
    state = load(STATE, {})
    request = os.path.join(conf, "acme_request.json")
    forced = os.path.exists(request)
    if forced:
        os.remove(request)
    report = {"at": iso(time.time()), "enabled": s["enabled"], "domain": s["domain"]}
    if s["enabled"]:
        now = time.time()
        have = os.path.exists(domain_conf(s["domain"]))
        installed = [s["domain"], s["cert"], s["key"]]
        next_renew = int(conf_value(s["domain"], "Le_NextRenewTime") or 0) if have else 0
        due = not have or forced or (next_renew and now >= next_renew)
        waiting = state.get("error_at") and now - state["error_at"] < RETRY_AFTER and not forced
        try:
            if have:
                set_http_port(s["domain"], s["port"])
            if due and not waiting:
                state["last_try"] = now
                if have:
                    code, out = acme("--renew", "-d", s["domain"], "--ecc", *(["--force"] if forced else []))
                else:
                    code, out = acme("--issue", "--standalone", "--httpport", str(s["port"]), "-d", s["domain"],
                                     "--keylength", "ec-256", "--server", "letsencrypt")
                if code not in (0, 2):          # 2: not due yet (acme.sh's own check)
                    raise RuntimeError(failure(out))
                if code == 0:
                    state["last_ok"] = now
                state.pop("error", None)
                state.pop("error_at", None)
                if not have:
                    state.pop("installed", None)
            if os.path.exists(domain_conf(s["domain"])) and state.get("installed") != installed:
                install(s)
                state["installed"] = installed
                state.pop("error", None)
                state.pop("error_at", None)
        except Exception as e:
            # The same error again keeps its time: ghostline sends one message per error.
            if state.get("error") != str(e):
                state["error"], state["error_at"] = str(e), now
        report["next_renew"] = iso(int(conf_value(s["domain"], "Le_NextRenewTime") or 0))
    for k in ("last_try", "last_ok", "error_at"):
        report[k] = iso(state.get(k))
    report["error"] = state.get("error") if s["enabled"] else None
    os.makedirs(os.path.dirname(STATE), exist_ok=True)
    with open(STATE, "w") as f:
        json.dump(state, f)
    fd, tmp = tempfile.mkstemp(dir=conf, prefix=".acme.", suffix=".tmp")
    with os.fdopen(fd, "w") as f:
        json.dump(report, f, indent=1)
    os.chmod(tmp, 0o644)
    os.replace(tmp, os.path.join(conf, "acme.json"))


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: gl-acme <conf-dir>")
    if not os.path.exists(ACME):
        sys.exit(f"{ACME} is not installed")
    run(sys.argv[1])


if __name__ == "__main__":
    main()
