#!/usr/bin/env python3
# Call storage check for ghostline (gateway pbx: managed). Runs on the Asterisk host as the asterisk
# user, from a timer, and writes <conf-dir>/storage.json: whether Asterisk can write the recordings
# directory and the SQLite CDR, and the free space there. ghostline mounts both read-only, so only
# this side can tell. Without it a wrong owner on a shared directory loses every recording and every
# call in the journal without a single visible error (Asterisk only logs it).
# Nothing is changed: a temp file is created and removed, the CDR gets a write lock and a rollback.
# Usage: gl-storage <conf-dir> <recordings-dir> <cdr-db>
import json, os, sqlite3, sys, tempfile, time


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
    return None


def report(path, error):
    return {"path": path, "ok": error is None, "error": error, "free_mb": free_mb(path)}


def main(conf, rec, db):
    data = {
        "at": iso(time.time()),
        "recordings": report(rec, check_dir(rec)),
        "cdr": report(db, check_cdr(db)),
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
