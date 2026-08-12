# Vendored fonts

`DMSans-Variable.woff2` — DM Sans, variable weight axis, latin subset.

> Copyright 2014 The DM Sans Project Authors (https://github.com/googlefonts/dm-fonts)

Licensed under the SIL Open Font License 1.1, which permits redistribution
provided that "each copy contains the above copyright notice and this license"
(OFL 1.1, condition 2). The full license text is therefore vendored alongside
the font as `OFL.txt`, copied verbatim from the upstream repository's
`Sans/OFL.txt`; the copyright notice above is reproduced from its first line.
Neither file may be deleted while `DMSans-Variable.woff2` ships —
`DesignSystemTests.The_vendored_font_ships_its_license_and_copyright_notice`
fails the build if either goes missing. Upstream:
<https://github.com/googlefonts/dm-fonts>.

It is vendored rather than loaded from a CDN because the host binds to loopback
and may run with no outbound network at all.

If this file is missing, the UI still renders: `--font-sans` in
`wwwroot/css/app.css` falls back to `system-ui`, and
`DesignSystemTests.The_font_stack_falls_back_to_system_fonts` pins that
fallback in place. Re-fetch it with the `curl` command in
`docs/superpowers/plans/2026-08-12-web-ui-phase-a-shell.md`, Task 1.
