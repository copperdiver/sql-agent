# Vendored fonts

`DMSans-Variable.woff2` — DM Sans, variable weight axis, latin subset.
Licensed under the SIL Open Font License 1.1, which permits redistribution.
Upstream: <https://github.com/googlefonts/dm-fonts>.

It is vendored rather than loaded from a CDN because the host binds to loopback
and may run with no outbound network at all.

If this file is missing, the UI still renders: `--font-sans` in
`wwwroot/css/app.css` falls back to `system-ui`, and
`DesignSystemTests.The_font_stack_falls_back_to_system_fonts` pins that
fallback in place. Re-fetch it with the `curl` command in
`docs/superpowers/plans/2026-08-12-web-ui-phase-a-shell.md`, Task 1.
