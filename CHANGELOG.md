# Changelog


## Unreleased

- `content.transform` now requires manifest-declared URL and MIME scopes; only matching response bodies are delivered and returned HTML is sanitized before normal parsing.
- `content.transform` is a Sensitive permission and its confirmation uses the page-read/change warning text.
