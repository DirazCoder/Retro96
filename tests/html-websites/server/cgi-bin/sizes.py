#!/usr/bin/env python3
"""
sizes.py - handles "Form 3 -- size extremes" from forms.html (Retro96 QA
suite). Method=GET.

Rewritten without the `cgi` module (removed in 3.13+). Renamed .cgi -> .py
for the same Windows shebang reason as register.py - see that file's
docstring for details.

Run via: python -m http.server --cgi 8000  (from the html-websites folder)
Then load: http://localhost:8000/forms.html  and submit Form 3 from there.
"""
import os
import html
from urllib.parse import parse_qsl

# Field names taken directly from forms.html Form 3.
FIELDS = ["t5", "t10", "t40", "ml3", "bare"]


def main():
    query = os.environ.get("QUERY_STRING", "")
    parsed = dict(parse_qsl(query, keep_blank_values=True))

    print("Content-Type: text/html\n")
    print("<html><head><title>Sizes received - Retro96</title></head><body>")
    print("<h1>Sizes received</h1>")
    print("<table border='1' cellpadding='4'>")
    for name in FIELDS:
        value = parsed.get(name, "(not submitted)")
        print(f"<tr><td>{html.escape(name)}</td><td>{html.escape(value)}</td></tr>")
    print("</table>")
    print("<p><a href='../forms.html'>Back to form</a></p>")
    print("</body></html>")


if __name__ == "__main__":
    main()
