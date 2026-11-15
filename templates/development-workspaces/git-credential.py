#!/usr/bin/env python3
"""Exact-repository, short-lived HTTPS credential helper. Never logs input or secrets."""
import json
import os
from pathlib import Path
import re
import stat
import sys
import time
from urllib.parse import urlsplit


CREDENTIAL = Path("/run/minicloud-git/credential.json")


def selected_credential(fields, credential, now):
    if set(credential) != {"origin", "username", "password", "expiresAt"}:
        return None
    origin, username, password, expiry = (credential[key] for key in
                                          ("origin", "username", "password", "expiresAt"))
    if not all(isinstance(value, str) for value in (origin, username, password)):
        return None
    if username != "x-access-token" or not password or any(ord(c) < 33 or ord(c) > 126 for c in password):
        return None
    if type(expiry) not in (int, float) or not now < expiry <= now + 3600:
        return None
    parsed = urlsplit(origin)
    if (parsed.scheme != "https" or not parsed.hostname or
            not re.fullmatch(r"[a-zA-Z0-9.-]+(?::[0-9]+)?", parsed.netloc) or
            parsed.username or parsed.password or
            parsed.query or parsed.fragment or not parsed.path.startswith("/") or
            not re.fullmatch(r"/[a-zA-Z0-9._/-]+", parsed.path) or
            not parsed.path.endswith(".git") or "//" in parsed.path or
            any(part in ("", ".", "..") for part in parsed.path[1:].split("/")) or
            origin != "https://" + parsed.netloc + parsed.path):
        return None
    if (fields.get("protocol") != "https" or fields.get("host") != parsed.netloc or
            fields.get("path") != parsed.path[1:] or
            fields.get("username", username) != username):
        return None
    return username, password


def load_credential(path=CREDENTIAL):
    directory = path.parent
    parent = directory.stat()
    if directory.is_symlink() or parent.st_uid != 0 or parent.st_mode & 0o022:
        return None
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        info = os.fstat(fd)
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != 2000 or
                stat.S_IMODE(info.st_mode) != 0o400 or info.st_nlink != 1 or info.st_size > 16384):
            return None
        data = os.read(fd, 16385)
        after = os.fstat(fd)
        if (info.st_ino, info.st_size, info.st_mtime_ns, info.st_mode) != (
                after.st_ino, after.st_size, after.st_mtime_ns, after.st_mode):
            return None
        return json.loads(data)
    finally:
        os.close(fd)


def main():
    if sys.argv[1:] != ["get"] or sys.stdout.isatty():
        return
    try:
        fields = {}
        total = 0
        while True:
            line = sys.stdin.readline(8193)
            total += len(line)
            if total > 16384 or not line.endswith("\n"):
                return
            if line == "\n":
                break
            key, separator, value = line[:-1].partition("=")
            if not separator or any(c in value for c in "\r\0"):
                return
            if key in {"protocol", "host", "path", "username"}:
                if key in fields:
                    return
                fields[key] = value
        credential = load_credential()
        if not isinstance(credential, dict):
            return
        result = selected_credential(fields, credential, time.time())
        if result:
            sys.stdout.write("username=" + result[0] + "\npassword=" + result[1] + "\n\n")
    except Exception:
        # Git must fail authentication, never fall back to a prompt or print sensitive diagnostics.
        return


if __name__ == "__main__":
    main()
