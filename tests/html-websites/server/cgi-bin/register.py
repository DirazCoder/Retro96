#!/usr/bin/env python3
"""
register.py - handles "Form 2 -- method=POST registration" from forms.html
(Retro96 QA suite).

Rewritten without the `cgi` module (removed in Python 3.13+) so it works
on 3.13/3.14. Renamed from .cgi to .py because Windows can't execute a
shebang line - Python's CGIHTTPRequestHandler on Windows runs the file
directly as if it were a native binary, which fails with WinError 193.
Naming it .py lets the server's own interpreter run it correctly.

Run via: python -m http.server --cgi 8000  (from the html-websites folder)
Then load: http://localhost:8000/forms.html  -- and submit Form 2 from
there, NOT from a file:// copy (the form's action is a relative path,
so it resolves against whatever URL the page itself was loaded from).
"""
import os
import sys
import html
from urllib.parse import parse_qsl


def read_post_body():
    length = int(os.environ.get("CONTENT_LENGTH", 0) or 0)
    return sys.stdin.buffer.read(length) if length > 0 else b""


def parse_multipart(raw_bytes, content_type):
    """Minimal multipart/form-data parser: text fields + file input filename."""
    marker = "boundary="
    idx = content_type.find(marker)
    if idx == -1:
        return []
    boundary = content_type[idx + len(marker):].strip().strip('"')
    boundary_bytes = ("--" + boundary).encode("utf-8")

    pairs = []
    for part in raw_bytes.split(boundary_bytes):
        part = part.strip(b"\r\n")
        if not part or part == b"--":
            continue
        if b"\r\n\r\n" not in part:
            continue
        headers_blob, _, body = part.partition(b"\r\n\r\n")
        body = body.rstrip(b"\r\n")
        headers_text = headers_blob.decode("utf-8", errors="replace")

        name, filename = None, None
        for line in headers_text.split("\r\n"):
            if line.lower().startswith("content-disposition:"):
                for piece in line.split(";"):
                    piece = piece.strip()
                    if piece.startswith("name="):
                        name = piece.split("=", 1)[1].strip('"')
                    elif piece.startswith("filename="):
                        filename = piece.split("=", 1)[1].strip('"')
        if name is None:
            continue
        value = (filename or "(no file chosen)") if filename is not None \
            else body.decode("utf-8", errors="replace")
        pairs.append((name, value))
    return pairs


def parse_fields():
    """Returns list of (name, value) pairs, preserving repeats for MULTIPLE selects."""
    content_type = os.environ.get("CONTENT_TYPE", "")
    raw = read_post_body()
    if "multipart/form-data" in content_type:
        return parse_multipart(raw, content_type)
    text = raw.decode("utf-8", errors="replace")
    return parse_qsl(text, keep_blank_values=True)


def main():
    pairs = parse_fields()
    single = {}       # last value wins, for normal single-value fields
    for name, value in pairs:
        single[name] = value
    interests = [v for k, v in pairs if k == "interests"]

    login = single.get("login", "")
    password = single.get("pass", "")
    speed = single.get("speed", "")
    agreed = "yes" if "tos" in single else "no"
    interests_str = ", ".join(interests) if interests else "(none selected)"

    print("Content-Type: text/html\n")
    print("<html><head><title>Registered - Retro96</title></head><body>")
    print("<h1>Registration received</h1>")
    print("<table border='1' cellpadding='4'>")
    print(f"<tr><td>Login</td><td>{html.escape(login)}</td></tr>")
    print(f"<tr><td>Password length</td><td>{len(password)} chars</td></tr>")
    print(f"<tr><td>Connection speed</td><td>{html.escape(speed)}</td></tr>")
    print(f"<tr><td>Interests</td><td>{html.escape(interests_str)}</td></tr>")
    print(f"<tr><td>Agreed to policy</td><td>{agreed}</td></tr>")
    print("</table>")
    print("<p><a href='../forms.html'>Back to form</a></p>")
    print("</body></html>")


if __name__ == "__main__":
    main()
