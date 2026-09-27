#!/usr/bin/env python3
"""
sizes.cgi - handles "Form 3 -- size extremes" from
forms-controls-extended.html (Retro96 QA suite). Method=GET.

Requires Python 3.12 or earlier - cgi.FieldStorage was removed in 3.13.
Run via: python -m http.server --cgi 8000  (from the html-websites folder)
Then load: http://localhost:8000/forms-controls-extended.html
"""
import cgi
import html

print("Content-Type: text/html\n")

form = cgi.FieldStorage()

# Field names taken directly from forms.html Form 3:
fields = ["t5", "t10", "t40", "ml3", "bare"]

print("<html><head><title>Sizes received - Retro96</title></head><body>")
print("<h1>Sizes received</h1>")
print("<table border='1' cellpadding='4'>")
for name in fields:
    value = form.getvalue(name, "(not submitted)")
    print(f"<tr><td>{html.escape(name)}</td><td>{html.escape(str(value))}</td></tr>")
print("</table>")
print("<p><a href='../forms-controls-extended.html'>Back to form</a></p>")
print("</body></html>")
