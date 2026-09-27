#!/usr/bin/env python3
"""
register.cgi - handles the "Form 2 -- method=POST registration" test form
from forms-controls-extended.html (Retro96 QA suite).

Requires Python 3.12 or earlier - cgi.FieldStorage was removed in 3.13.
Run via: python -m http.server --cgi 8000  (from the html-websites folder)
Then load: http://localhost:8000/cgi-bin/register.cgi
"""
import cgi
import html

print("Content-Type: text/html\n")

form = cgi.FieldStorage()


def get(name, default=""):
    value = form.getvalue(name, default)
    # Checkboxes and multi-selects can come back as lists
    if isinstance(value, list):
        return ", ".join(value)
    return value


login = get("login")
password = get("pass")
speed = get("speed")
interests = get("interests")
agreed = "yes" if form.getvalue("tos") else "no"

print("<html><head><title>Registered - Retro96</title></head><body>")
print("<h1>Registration received</h1>")
print("<table border='1' cellpadding='4'>")
print(f"<tr><td>Login</td><td>{html.escape(login)}</td></tr>")
print(f"<tr><td>Password length</td><td>{len(password)} chars</td></tr>")
print(f"<tr><td>Connection speed</td><td>{html.escape(speed)}</td></tr>")
print(f"<tr><td>Interests</td><td>{html.escape(interests)}</td></tr>")
print(f"<tr><td>Agreed to policy</td><td>{agreed}</td></tr>")
print("</table>")
print("<p><a href='../forms-controls-extended.html'>Back to form</a></p>")
print("</body></html>")
