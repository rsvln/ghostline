#!/usr/bin/env python3
# Call storage check for ghostline (gateway pbx: managed). Runs on the Asterisk host as the asterisk
# user, from a timer, and writes <conf-dir>/storage.json: whether Asterisk can write the recordings
# directory and the SQLite CDR, and the free space there. ghostline mounts both read-only, so only
# this side can tell. Without it a wrong owner on a shared directory loses every recording and every
# call in the journal without a single visible error (Asterisk only logs it).
# A write test from a fresh process is not enough: on 2026-10-30 Asterisk opened the CDR while it was
# not writable, SQLite gave it a read-only handle, and Asterisk kept that handle after the owner was
# fixed. This check passed for three weeks while no call reached the journal. So two more checks look
# at what Asterisk itself does: the mode of its own open handle on the CDR, and whether every mixed
# recording of the last day has its call in the CDR.
# Nothing is changed: a temp file is created and removed, the CDR gets a write lock and a rollback,
# the rest is read-only.
# Usage: gl-storage <conf-dir> <recordings-dir> <cdr-db>
import json, os, re, sqlite3, sys, tempfile, time

# Mixed recordings of the managed dialplan: (in|out)-<a>-<b>-YYYYMMDD-HHMMSS-<uniqueid>.wav; the legs
# (-r/-t) are not matched.
RECORDING = re.compile(r"^(?:in|out)-.+-(\d+\.\d+)\.wav$")
# A call reaches the CDR at hangup, when its recording is closed too; a few minutes of slack.
RECORDING_GRACE = 5 * 60
RECORDING_WINDOW = 24 * 3600


def iso(t):
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))


def free_mb(path):
    try:
        st = os.statvfs(path)
        return st.f_bavail * st.f_frsize // (1024 * 1024)
    except OSError:
        return None


def check_dir(path):
    if not os.path.isdir(path):
        return f"{path} does not exist (is the shared directory mounted?)"
    try:
        fd, tmp = tempfile.mkstemp(dir=path, prefix=".gl-storage.")
        os.close(fd)
        os.remove(tmp)
    except OSError as e:
        return f"{path}: cannot write: {e.strerror}"
    return None


def check_cdr(db):
    # The journal sits next to the database, so the directory must be writable too.
    error = check_dir(os.path.dirname(db))
    if error:
        return error
    if not os.path.exists(db):
        # Asterisk creates it on the first call; nothing is lost yet.
        return None
    try:
        con = sqlite3.connect(db, timeout=5, isolation_level=None)
        try:
            con.execute("BEGIN IMMEDIATE")
            con.execute("ROLLBACK")
        finally:
            con.close()
    except sqlite3.Error as e:
        return f"{db}: cannot write: {e}"
    return asterisk_handle(db)


def asterisk_pids():
    for pid in filter(str.isdigit, os.listdir("/proc")):
        try:
            with open(f"/proc/{pid}/comm") as f:
                if f.read().strip() == "asterisk":
                    yield pid
        except OSError:
            pass


# The handle Asterisk has on the CDR: read-only means every call is lost while the check above passes.
# Asterisk runs as the same user, so its /proc entries are readable. No Asterisk running: nothing to say.
def asterisk_handle(db):
    target = os.path.realpath(db)
    running = False
    for pid in asterisk_pids():
        running = True
        try:
            fds = os.listdir(f"/proc/{pid}/fd")
        except OSError:
            continue
        for fd in fds:
            try:
                if os.readlink(f"/proc/{pid}/fd/{fd}") != target:
                    continue
                with open(f"/proc/{pid}/fdinfo/{fd}") as f:
                    flags = next(int(l.split()[1], 8) for l in f if l.startswith("flags:"))
            except (OSError, StopIteration, ValueError):
                continue
            if flags & 3 == os.O_RDONLY:
                return (f"Asterisk has {db} open read-only and loses every call: "
                        "asterisk -rx 'module unload cdr_sqlite3_custom.so' then 'module load', or restart Asterisk")
            return None
    if running:
        return f"Asterisk does not have {db} open: is cdr_sqlite3_custom loaded?"
    return None


# End to end: every mixed recording that ended in the last day must have its call in the CDR (by the
# uniqueid in the file name). Whatever breaks the CDR, this is what the user would notice.
def unjournaled(rec, db):
    if not os.path.exists(db):
        return None
    now = time.time()
    days = {time.strftime("%Y/%m/%d", f(now - back)) for back in (0, RECORDING_WINDOW) for f in (time.gmtime, time.localtime)}
    found = {}
    for day in days:
        d = os.path.join(rec, day)
        try:
            names = os.listdir(d)
        except OSError:
            continue
        for name in names:
            m = RECORDING.match(name)
            if not m:
                continue
            try:
                age = now - os.path.getmtime(os.path.join(d, name))
            except OSError:
                continue
            if RECORDING_GRACE < age < RECORDING_WINDOW:
                found[m.group(1)] = f"{day}/{name.split('-')[0]}"
    if not found:
        return None
    try:
        con = sqlite3.connect(f"file:{db}?mode=ro", uri=True, timeout=5)
        try:
            marks = ",".join("?" * len(found))
            known = {u for row in con.execute(
                f"select uniqueid, linkedid from cdr where uniqueid in ({marks}) or linkedid in ({marks})",
                list(found) * 2) for u in row}
        finally:
            con.close()
    except sqlite3.Error as e:
        return f"{db}: cannot read: {e}"
    missing = sorted(v for k, v in found.items() if k not in known)
    if not missing:
        return None
    return (f"{len(missing)} call(s) of the last 24 h were recorded but are not in the CDR ({', '.join(missing[:5])}): "
            "Asterisk does not write its CDR, see its log")


def report(path, error):
    return {"path": path, "ok": error is None, "error": error, "free_mb": free_mb(path)}


def main(conf, rec, db):
    data = {
        "at": iso(time.time()),
        "recordings": report(rec, check_dir(rec)),
        "cdr": report(db, check_cdr(db) or unjournaled(rec, db)),
    }
    out = os.path.join(conf, "storage.json")
    fd, tmp = tempfile.mkstemp(dir=conf, prefix=".storage.")
    with os.fdopen(fd, "w") as f:
        json.dump(data, f, indent=1)
    os.chmod(tmp, 0o644)
    os.replace(tmp, out)


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit("usage: gl-storage <conf-dir> <recordings-dir> <cdr-db>")
    main(*sys.argv[1:])
