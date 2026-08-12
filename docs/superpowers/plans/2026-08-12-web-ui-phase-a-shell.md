# Web UI Phase A — Design System and Application Shell

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Blazor web UI a real design system — token-driven light/dark/system themes, vendored typography, reusable UI primitives — and an application shell with a sidebar, user card, and settings page, without changing any existing behavior or route.

**Architecture:** Hand-written CSS. `wwwroot/css/app.css` carries tokens, reset, and base type; every component's own rules live in a colocated `.razor.css` file bundled by Blazor's CSS isolation. Themes are CSS custom properties swapped by a class on `<html>`, applied by a synchronous `<head>` script before the Blazor circuit connects so nothing flashes. All existing pages stay reachable and keep their behavior; they are restyled in place.

**Tech Stack:** .NET 10, Blazor Server (Interactive Server render mode on `<Routes>`), xUnit + bUnit, `WebApplicationFactory` for integration tests. No Node, no npm, no CDN.

**Spec:** `docs/superpowers/specs/2026-08-12-web-ui-overhaul-design.md` (Phase A section)

## Global Constraints

- **Target framework `net10.0`.** Nullable enabled, implicit usings enabled.
- **No Node toolchain, no npm, no build step beyond `dotnet`.** CI runs only `dotnet restore/build/test` against `SqlAgent.slnx`.
- **No CDN, no external network at runtime.** The host is loopback-only and may be offline. Every asset ships in `wwwroot`.
- **Token values are copied verbatim from the spec.** Components consume `var(--token)` only — never a literal color.
- **No behavior or route changes in this phase.** `/` stays `Workspace`, `/connections` stays `Connections`. The schema rail keeps working.
- **Provider exception text is never rendered to the user** (it can echo a connection string). Log it, show a stable message.
- **Every existing test must stay green.** Several assert on rendered markup (`ResultGridTests` finds buttons by `TextContent.Contains("CSV")`, `SchemaRailTests` asserts table names appear). Restyling must not change button text or remove text nodes.
- **Test conventions:** xUnit, `Bunit.TestContext`, sentence-style test names with underscores, one class per component under test in `tests/SqlAgent.Tests/`. Comments explain *why* a test exists, not what it does.
- **Commit messages** end with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- **Verification command** for the whole suite:
  `dotnet test SqlAgent.slnx --configuration Release`

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/SqlAgent.Host/wwwroot/css/app.css` | Tokens (light/dark/system), reset, base typography, layout primitives, utility classes | 1 |
| `src/SqlAgent.Host/wwwroot/js/theme.js` | Pre-paint theme + sidebar class application; `window.sqlAgentUi` accessors | 1 |
| `src/SqlAgent.Host/wwwroot/fonts/README.md` | Font provenance and license note | 1 |
| `src/SqlAgent.Host/Components/App.razor` | Links stylesheets and `theme.js` in `<head>` | 1 |
| `tests/SqlAgent.Tests/DesignSystemTests.cs` | Asserts assets are served and the two dark blocks cannot drift | 1 |
| `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor` | Inline-SVG icon set, one `<svg>` per name, `currentColor` | 2 |
| `src/SqlAgent.Host/Components/Shared/Ui/Badge.razor` | Small status/count pill | 2 |
| `src/SqlAgent.Host/Components/Shared/Ui/Spinner.razor` | Indeterminate activity indicator | 2 |
| `src/SqlAgent.Host/Components/Shared/Ui/EmptyState.razor` | Icon + title + hint for empty lists | 2 |
| `src/SqlAgent.Host/Components/Shared/Ui/Menu.razor` | Popover menu with backdrop and Escape close | 3 |
| `src/SqlAgent.Host/Components/Shared/Ui/MenuItem.razor` | One menu row (icon, label, optional trailing) | 3 |
| `src/SqlAgent.Host/Components/Shared/Ui/Segmented.razor` | Segmented single-choice control | 3 |
| `src/SqlAgent.Host/Components/Shared/Ui/Modal.razor` | Centered dialog with scrim and Escape close | 3 |
| `src/SqlAgent.Host/Components/Shared/Ui/ThemeToggle.razor` | Segmented system/light/dark bound to `sqlAgentUi` | 4 |
| `src/SqlAgent.Host/Components/Layout/MainLayout.razor` | Shell: sidebar + inset main card, keeps `WorkArea` | 5 |
| `src/SqlAgent.Host/Components/Layout/Sidebar.razor` | Composes header, nav, and user card; owns drawer state | 5 |
| `src/SqlAgent.Host/Components/Layout/SidebarHeader.razor` | Product mark + collapse toggle | 5 |
| `src/SqlAgent.Host/Components/Layout/SidebarNav.razor` | Primary nav rows (Workspace, Connections in this phase) | 5 |
| `src/SqlAgent.Host/Web/HostInfo.cs` | Version, store path, bind URL, port, LLM status for About/Settings | 6 |
| `src/SqlAgent.Host/Components/Layout/UserCard.razor` | OS account + menu (Settings, Theme, About) | 6 |
| `src/SqlAgent.Host/Components/Pages/Settings.razor` | `/settings`: theme, LLM provider status, environment | 7 |
| `src/SqlAgent.Core/Llm/LlmGateway.cs` | Adds `ILlmSqlGateway.IsConfigured` default member | 7 |
| `*.razor.css` beside each restyled existing component | Visual rules for Connections, Workspace, SchemaRail, ResultGrid, ChatOutcome, OutcomeMessage | 8 |
| `docs/web-ui.md`, `README.md` | Shell documentation and expanded manual checklist | 9 |

---

### Task 1: CSS foundation, tokens, and pre-paint theme script

**Files:**
- Create: `src/SqlAgent.Host/wwwroot/css/app.css`
- Create: `src/SqlAgent.Host/wwwroot/js/theme.js`
- Create: `src/SqlAgent.Host/wwwroot/fonts/README.md`
- Modify: `src/SqlAgent.Host/Components/App.razor:1-31`
- Test: `tests/SqlAgent.Tests/DesignSystemTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - CSS custom properties on `:root`, listed in the code below. Every later task uses these names.
  - `window.sqlAgentUi.getTheme(): string` returning `"system" | "light" | "dark"`.
  - `window.sqlAgentUi.setTheme(value: string): void`.
  - `window.sqlAgentUi.getSidebar(): string` returning `"expanded" | "collapsed"`.
  - `window.sqlAgentUi.setSidebar(value: string): void`.
  - `SqlAgent.Tests.RepoPaths.Find(string relativePath): string` — test helper resolving a repo-relative path from the test assembly location.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/DesignSystemTests.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;

namespace SqlAgent.Tests;

/// <summary>Resolves repo-relative paths from the test assembly's location, so tests can assert on
/// source files (CSS, JS) that are not compiled into the assembly.</summary>
public static class RepoPaths
{
    public static string Find(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not find '{relativePath}' in any ancestor of {AppContext.BaseDirectory}.");
    }
}

public class DesignSystemTests : IClassFixture<WebTestHost>
{
    private readonly WebTestHost _host;
    public DesignSystemTests(WebTestHost host) => _host = host;

    private static string Css() => File.ReadAllText(RepoPaths.Find("src/SqlAgent.Host/wwwroot/css/app.css"));

    [Fact]
    public async Task The_stylesheet_is_served()
    {
        // Mirrors Framework_assets_are_reachable_without_a_token: asserting only "not 401" would let a
        // 404 pass, and a stylesheet that 404s is exactly how this UI shipped unstyled before.
        var client = _host.NewClient();
        await client.GetAsync($"/?token={WebTestHost.Token}");

        var r = await client.GetAsync("/css/app.css");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotEmpty(await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_theme_script_is_served()
    {
        var client = _host.NewClient();
        await client.GetAsync($"/?token={WebTestHost.Token}");

        var r = await client.GetAsync("/js/theme.js");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotEmpty(await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_document_links_the_stylesheet_and_loads_the_theme_script_in_head()
    {
        // The script must be in <head> and synchronous: loaded from <body>, or deferred, the browser
        // paints the light theme first and the user sees a flash before the dark class lands.
        var client = _host.NewClient();
        var html = await client.GetStringAsync($"/?token={WebTestHost.Token}");

        var head = html[..html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase)];

        Assert.Contains("css/app.css", head);
        Assert.Contains("SqlAgent.Host.styles.css", head);
        Assert.Contains("js/theme.js", head);
        Assert.DoesNotContain("defer", head);
        Assert.DoesNotContain("async", head);
    }

    [Fact]
    public void Every_token_redefined_for_dark_mode_is_also_redefined_for_the_system_preference()
    {
        // The dark palette is written twice — once for :root.dark (explicit choice) and once inside
        // @media (prefers-color-scheme: dark) for the "system" setting, which sets no class. Two copies
        // can drift, and drift shows up as one or two stray light-mode colors on a dark page, which is
        // easy to miss by eye. This pins them to the same property set.
        var css = Css();

        var explicitDark = Properties(Block(css, ":root.dark"));
        var systemDark = Properties(Block(css, ":root:not(.light):not(.dark)"));

        Assert.NotEmpty(explicitDark);
        Assert.Equal(explicitDark, systemDark);
    }

    [Fact]
    public void The_font_stack_falls_back_to_system_fonts()
    {
        // DM Sans is vendored, but a deployment that loses the woff2 (or a build that never fetched it)
        // must still render in a sane sans-serif rather than the browser's serif default.
        var css = Css();

        var match = Regex.Match(css, @"--font-sans:\s*([^;]+);");

        Assert.True(match.Success, "app.css must define --font-sans");
        Assert.Contains("system-ui", match.Groups[1].Value);
    }

    /// <summary>Returns the body of the first declaration block for <paramref name="selector"/>.</summary>
    private static string Block(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"app.css must contain a '{selector}' block");
        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        return css[(open + 1)..close];
    }

    /// <summary>Custom-property names declared in a block body, sorted, so two blocks compare by set.</summary>
    private static List<string> Properties(string block) =>
        Regex.Matches(block, @"(--[a-z0-9-]+)\s*:")
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter DesignSystemTests`
Expected: FAIL — `RepoPaths.Find` throws `FileNotFoundException` for `app.css`, and the served-asset tests return 404.

- [ ] **Step 3: Write `app.css`**

Create `src/SqlAgent.Host/wwwroot/css/app.css`. The dark palette appears twice on purpose (explicit `.dark`, and the system preference which sets no class); the test above pins them to the same property set.

```css
/* ============================================================================
   Design tokens. Values are taken from the reference AI-chat interface's own
   stylesheet (see docs/superpowers/specs/2026-08-12-web-ui-overhaul-design.md).
   Components must consume var(--token) and never a literal color, so the two
   themes cannot drift apart component by component.
   ========================================================================== */
:root {
  color-scheme: light;

  --background-50: #fff;
  --background-100: #fff;
  --background-soft-50: #f9fafb;
  --background-soft-100: #f3f4f6;
  --background-soft-200: #f3f4f6;
  --background-soft-400: #e5e7eb;

  --title-50: #1f2937;
  --text-50: #374151;
  --text-100: #6b7280;
  --text-200: #4b5563;

  --base-50: #f3f4f6;
  --base-100: #e5e7eb;
  --base-200: #d1d5db;

  --primary-50: #eff3ff;
  --primary-300: #91aeff;
  --primary-400: #5e84fc;
  --primary-500: #3758f9;
  --primary-600: #2237ee;
  --primary-text: #fff;

  --danger-500: #dc2626;
  --danger-600: #b91c1c;
  --danger-surface: #fef2f2;
  --success-500: #16a34a;
  --success-surface: #f0fdf4;
  --warning-500: #f59e0b;
  --warning-text: #b45309;
  --warning-surface: #fffbeb;

  --dropdown-background: #fff;
  --dropdown-hover-background: #f3f4f6;
  --input-background: #fff;
  --input-placeholder: #9ca3af;
  --scrim: rgb(17 24 39 / .45);

  /* Non-color tokens are theme-independent and defined once. */
  --font-sans: "DM Sans", system-ui, -apple-system, "Segoe UI", sans-serif;
  --font-mono: ui-monospace, "Cascadia Mono", Consolas, monospace;

  --text-xs: .75rem;
  --text-sm: .875rem;
  --text-base: 1rem;
  --text-lg: 1.125rem;
  --text-xl: 1.25rem;
  --text-2xl: 1.5rem;
  --text-3xl: 1.875rem;

  --radius-control: 8px;
  --radius-card: 12px;
  --radius-pill: 16px;
  --radius-round: 999px;

  --space-1: 4px;
  --space-2: 8px;
  --space-3: 12px;
  --space-4: 16px;
  --space-5: 20px;
  --space-6: 24px;
  --space-8: 32px;

  --sidebar-width: 300px;
  --sidebar-collapsed-width: 72px;
  --shadow-menu: 0 10px 30px rgb(17 24 39 / .12);
}

:root.dark {
  color-scheme: dark;
  --background-50: #030712;
  --background-100: #111827;
  --background-soft-50: #111827;
  --background-soft-100: #111827;
  --background-soft-200: #1f2937;
  --background-soft-400: #1f2937;
  --title-50: rgb(255 255 255 / .8);
  --text-50: #9ca3af;
  --text-100: #9ca3af;
  --text-200: #6b7280;
  --base-50: #1f2937;
  --base-100: #111827;
  --base-200: #374151;
  --primary-50: rgb(55 88 249 / .15);
  --danger-surface: rgb(239 68 68 / .09);
  --success-surface: rgb(34 197 94 / .09);
  --warning-surface: rgb(245 158 11 / .09);
  --warning-text: #f59e0b;
  --dropdown-background: #1f2937;
  --dropdown-hover-background: #374151;
  --input-background: rgb(255 255 255 / .05);
  --input-placeholder: #6b7280;
  --scrim: rgb(3 7 18 / .6);
  --shadow-menu: 0 10px 30px rgb(0 0 0 / .45);
}

/* The "system" theme setting deliberately puts no class on <html>, so the OS preference is the only
   signal available here. :not(.light):not(.dark) keeps an explicit choice authoritative in both
   directions rather than letting the OS override a user who picked light on a dark machine. */
@media (prefers-color-scheme: dark) {
  :root:not(.light):not(.dark) {
    color-scheme: dark;
    --background-50: #030712;
    --background-100: #111827;
    --background-soft-50: #111827;
    --background-soft-100: #111827;
    --background-soft-200: #1f2937;
    --background-soft-400: #1f2937;
    --title-50: rgb(255 255 255 / .8);
    --text-50: #9ca3af;
    --text-100: #9ca3af;
    --text-200: #6b7280;
    --base-50: #1f2937;
    --base-100: #111827;
    --base-200: #374151;
    --primary-50: rgb(55 88 249 / .15);
    --danger-surface: rgb(239 68 68 / .09);
    --success-surface: rgb(34 197 94 / .09);
    --warning-surface: rgb(245 158 11 / .09);
    --warning-text: #f59e0b;
    --dropdown-background: #1f2937;
    --dropdown-hover-background: #374151;
    --input-background: rgb(255 255 255 / .05);
    --input-placeholder: #6b7280;
    --scrim: rgb(3 7 18 / .6);
    --shadow-menu: 0 10px 30px rgb(0 0 0 / .45);
  }
}

/* ============================================================================
   Typography. DM Sans is vendored (see wwwroot/fonts/README.md). font-display:
   swap plus the system-ui fallback in --font-sans means a missing file degrades
   to a sane sans-serif instead of the browser's serif default.
   ========================================================================== */
@font-face {
  font-family: "DM Sans";
  src: url("../fonts/DMSans-Variable.woff2") format("woff2");
  font-weight: 100 1000;
  font-style: normal;
  font-display: swap;
}

/* ============================================================================
   Reset
   ========================================================================== */
*, *::before, *::after { box-sizing: border-box; }
* { margin: 0; }

html, body { height: 100%; }

body {
  background: var(--background-soft-100);
  color: var(--text-50);
  font-family: var(--font-sans);
  font-size: var(--text-sm);
  line-height: 1.5;
  -webkit-font-smoothing: antialiased;
}

h1, h2, h3, h4 { color: var(--title-50); font-weight: 600; line-height: 1.25; }
h1 { font-size: var(--text-2xl); }
h2 { font-size: var(--text-xl); }
h3 { font-size: var(--text-lg); }

a { color: var(--primary-500); text-decoration: none; }
a:hover { text-decoration: underline; }

code, pre, .mono { font-family: var(--font-mono); font-size: var(--text-xs); }

:focus-visible { outline: 2px solid var(--primary-400); outline-offset: 2px; }

/* ============================================================================
   Base controls. Restyled pages consume these directly, so a plain <button> or
   <input> in an existing component looks right without touching its markup.
   ========================================================================== */
button {
  font: inherit;
  cursor: pointer;
  border: 1px solid var(--base-200);
  border-radius: var(--radius-control);
  background: var(--background-100);
  color: var(--title-50);
  padding: var(--space-2) var(--space-3);
  transition: background-color .15s, border-color .15s, color .15s;
}
button:hover:not(:disabled) { background: var(--background-soft-100); }
button:disabled { background: var(--base-50); color: var(--input-placeholder); cursor: not-allowed; }

button.primary {
  background: var(--primary-500);
  border-color: var(--primary-500);
  color: var(--primary-text);
}
button.primary:hover:not(:disabled) { background: var(--primary-600); border-color: var(--primary-600); }

button.danger { background: var(--danger-500); border-color: var(--danger-500); color: #fff; }
button.danger:hover:not(:disabled) { background: var(--danger-600); border-color: var(--danger-600); }

button.ghost { background: none; border-color: transparent; color: var(--text-100); }
button.ghost:hover:not(:disabled) { background: var(--background-soft-100); color: var(--title-50); }

input, select, textarea {
  font: inherit;
  color: var(--title-50);
  background: var(--input-background);
  border: 1px solid var(--base-200);
  border-radius: var(--radius-control);
  padding: var(--space-2) var(--space-3);
}
input::placeholder, textarea::placeholder { color: var(--input-placeholder); }
input:focus, select:focus, textarea:focus { border-color: var(--primary-300); outline: none; }

label { color: var(--text-50); font-size: var(--text-sm); }

table { border-collapse: collapse; width: 100%; }
th, td {
  text-align: left;
  padding: var(--space-2) var(--space-3);
  border-bottom: 1px solid var(--base-100);
  white-space: nowrap;
}
th { color: var(--text-100); font-weight: 500; font-size: var(--text-xs); text-transform: uppercase; letter-spacing: .04em; }

/* ============================================================================
   Shell layout. The sidebar-collapsed class is set on <html> by theme.js before
   first paint, so a collapsed sidebar does not render wide and then snap.
   ========================================================================== */
.app { display: flex; height: 100vh; overflow: hidden; }

.app-main { flex: 1; min-width: 0; padding: var(--space-2); display: flex; }

.app-card {
  flex: 1;
  min-width: 0;
  background: var(--background-100);
  border: 1px solid var(--base-100);
  border-radius: var(--radius-card);
  overflow: auto;
  padding: var(--space-6);
}

/* ============================================================================
   Shared surfaces used by existing components
   ========================================================================== */
.outcome {
  border: 1px solid var(--base-100);
  border-radius: var(--radius-control);
  background: var(--background-soft-50);
  padding: var(--space-3) var(--space-4);
  margin: var(--space-3) 0;
}
.outcome-code {
  display: inline-block;
  margin-top: var(--space-2);
  padding: 2px var(--space-2);
  border-radius: var(--radius-control);
  background: var(--background-soft-200);
  color: var(--text-100);
}
.meta { color: var(--text-100); font-size: var(--text-xs); }
.actions { display: flex; gap: var(--space-2); flex-wrap: wrap; margin: var(--space-3) 0; }
.grid-scroll { overflow-x: auto; border: 1px solid var(--base-100); border-radius: var(--radius-control); }

/* Utilities, deliberately few: anything reused more than twice becomes a token or a component rule. */
.row { display: flex; align-items: center; gap: var(--space-2); }
.spread { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); }
.stack { display: flex; flex-direction: column; gap: var(--space-3); }
.muted { color: var(--text-100); }
.truncate { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sr-only {
  position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px;
  overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; border: 0;
}

/* Scrollbars: the default light-on-dark scrollbar is jarring in the dark theme. */
.custom-scroll { scrollbar-width: thin; scrollbar-color: var(--base-200) transparent; }
.custom-scroll::-webkit-scrollbar { width: 8px; height: 8px; }
.custom-scroll::-webkit-scrollbar-thumb { background: var(--base-200); border-radius: var(--radius-round); }
.custom-scroll::-webkit-scrollbar-track { background: transparent; }

@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { transition-duration: .01ms !important; animation-duration: .01ms !important; }
}
```

- [ ] **Step 4: Write `theme.js`**

Create `src/SqlAgent.Host/wwwroot/js/theme.js`:

```js
// Loaded synchronously from <head>, before Blazor connects. A Blazor round trip would paint the
// light theme first and flash; a deferred script would do the same. Everything here must therefore
// be cheap and must not touch the DOM beyond <html>'s class list, which exists at this point.
(function () {
  var THEME_KEY = 'sqlagent.theme';
  var SIDEBAR_KEY = 'sqlagent.sidebar';

  // localStorage throws in some private-browsing modes rather than returning null, and a theme
  // preference is not worth breaking the page over.
  function read(key, fallback) {
    try {
      return window.localStorage.getItem(key) || fallback;
    } catch (e) {
      return fallback;
    }
  }

  function write(key, value) {
    try {
      window.localStorage.setItem(key, value);
    } catch (e) {
      /* ignore: the class is still applied for this page's lifetime */
    }
  }

  function applyTheme(theme) {
    var classes = document.documentElement.classList;
    classes.remove('light', 'dark');
    // 'system' intentionally adds nothing: app.css keys the OS preference off the absence of both.
    if (theme === 'light' || theme === 'dark') classes.add(theme);
  }

  function applySidebar(state) {
    document.documentElement.classList.toggle('sidebar-collapsed', state === 'collapsed');
  }

  applyTheme(read(THEME_KEY, 'system'));
  applySidebar(read(SIDEBAR_KEY, 'expanded'));

  window.sqlAgentUi = {
    getTheme: function () { return read(THEME_KEY, 'system'); },
    setTheme: function (theme) { write(THEME_KEY, theme); applyTheme(theme); },
    getSidebar: function () { return read(SIDEBAR_KEY, 'expanded'); },
    setSidebar: function (state) { write(SIDEBAR_KEY, state); applySidebar(state); }
  };
})();
```

- [ ] **Step 5: Vendor the font, or record that it is absent**

Attempt the download (the URL is Google's own font CDN; the file is SIL OFL 1.1 licensed and redistributable):

```bash
mkdir -p src/SqlAgent.Host/wwwroot/fonts
curl -fsSL -o src/SqlAgent.Host/wwwroot/fonts/DMSans-Variable.woff2 \
  "https://fonts.gstatic.com/s/dmsans/v15/rP2Hp2ywxg089UriCZOIHTWEBlwu8Q.woff2"
```

Then create `src/SqlAgent.Host/wwwroot/fonts/README.md`:

```markdown
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
```

If `curl` fails (offline, or the URL has moved), do **not** block: commit the `README.md` and the
`@font-face` rule without the binary, and note it in the commit message. The fallback stack is
designed for exactly this case, and Step 6's tests do not depend on the file existing.

- [ ] **Step 6: Wire the assets into `App.razor`**

Replace the `<head>` contents and keep the existing `<body>` comment and scripts. The full file
becomes:

```razor
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>SQL Agent</title>
    <base href="/" />
    @*
        Order matters. CodeMirror ships its own colors, so it comes first and app.css overrides it.
        SqlAgent.Host.styles.css is the Blazor CSS-isolation bundle (every *.razor.css in this
        project); it comes last so a component's own rules beat the shared sheet.

        theme.js is in <head> and NOT deferred on purpose: it reads localStorage and sets the theme
        class on <html> before the first paint. Moved to <body>, or given defer/async, the browser
        paints the light theme and the user sees a flash before the dark class arrives.
    *@
    <link rel="stylesheet" href="lib/codemirror/codemirror.min.css" />
    <link rel="stylesheet" href="css/app.css" />
    <link rel="stylesheet" href="SqlAgent.Host.styles.css" />
    <script src="js/theme.js"></script>
    <HeadOutlet @rendermode="InteractiveServer" />
</head>
<body>
    @*
        The render mode belongs here, on the router, not on the individual @page components. A render
        mode declared on a page does NOT propagate up to its layout: it would make each page its own
        interactive island rooted at the page component, leaving Routes, MainLayout, SchemaRail and
        WorkArea in the static SSR pass. Statically rendered, WorkArea's ErrorBoundary would not even
        wrap the pages (they would be separate root components in the circuit), its Retry @onclick
        would never be wired up in the browser, and the static NavigationManager never raises
        LocationChanged — so both of the boundary's recovery paths were dead. Putting it on <Routes>
        makes the whole component tree one interactive circuit, which is also what makes AppState
        (scoped to the circuit) shared between the rail and the pages rather than one instance per
        island. Note this switches navigation from enhanced navigation to circuit routing.
    *@
    <Routes @rendermode="InteractiveServer" />
    <script src="js/download.js"></script>
    <script src="lib/codemirror/codemirror.min.js"></script>
    <script src="lib/codemirror/sql.min.js"></script>
    <script src="js/sql-editor.js"></script>
    <script src="_framework/blazor.web.js"></script>
</body>
</html>
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter DesignSystemTests`
Expected: PASS, 5 tests.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS. No existing test asserts on `<head>` contents, so nothing should regress.

- [ ] **Step 9: Commit**

```bash
git add src/SqlAgent.Host/wwwroot/css/app.css src/SqlAgent.Host/wwwroot/js/theme.js \
        src/SqlAgent.Host/wwwroot/fonts src/SqlAgent.Host/Components/App.razor \
        tests/SqlAgent.Tests/DesignSystemTests.cs
git commit -m "$(cat <<'EOF'
Add the design-token stylesheet and pre-paint theme script

The web UI had no application CSS at all: App.razor linked only CodeMirror's
stylesheet, so every screen rendered as unstyled browser-default HTML.

This adds app.css (tokens for light, dark, and the system preference; reset;
base controls; shell layout) and theme.js, which applies the stored theme to
<html> from <head> before Blazor connects so the page never flashes the wrong
theme. The dark palette is written twice — once for an explicit choice, once for
the OS preference — and a test pins the two blocks to the same property set so
they cannot drift.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Presentational UI primitives

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/Badge.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/Badge.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/Spinner.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/Spinner.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/EmptyState.razor`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/EmptyState.razor.css`
- Modify: `src/SqlAgent.Host/Components/_Imports.razor`
- Test: `tests/SqlAgent.Tests/UiPrimitiveTests.cs`

**Interfaces:**
- Consumes: tokens from Task 1.
- Produces:
  - `<Icon Name="string" Size="int?" Class="string?" />` — `Size` defaults to 20. Unknown names render nothing and log nothing (a missing glyph must not break a page).
  - `Icon.Names` — `IReadOnlySet<string>` of available icon names, for tests.
  - `<Badge Tone="BadgeTone" >child</Badge>` where `BadgeTone` is `Neutral | Primary | Success | Warning | Danger`.
  - `<Spinner Size="int?" Label="string?" />` — `Label` defaults to `"Working"`, rendered for screen readers.
  - `<EmptyState Icon="string" Title="string" Hint="string?" >optional actions</EmptyState>`.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/UiPrimitiveTests.cs`:

```csharp
using Bunit;
using SqlAgent.Host.Components.Shared.Ui;

namespace SqlAgent.Tests;

public class UiPrimitiveTests
{
    [Fact]
    public void An_icon_renders_an_svg_with_the_requested_size()
    {
        using var ctx = new Bunit.TestContext();

        var icon = ctx.RenderComponent<Icon>(p => p.Add(i => i.Name, "database").Add(i => i.Size, 16));

        var svg = icon.Find("svg");
        Assert.Equal("16", svg.GetAttribute("width"));
        Assert.Equal("16", svg.GetAttribute("height"));
        Assert.NotEmpty(icon.FindAll("svg path"));
    }

    [Fact]
    public void An_icon_inherits_the_surrounding_text_color()
    {
        // Icons sit inside buttons and menu rows whose color changes on hover and between themes.
        // A hard-coded stroke would strand them at one color in one theme.
        using var ctx = new Bunit.TestContext();

        var icon = ctx.RenderComponent<Icon>(p => p.Add(i => i.Name, "database"));

        Assert.Equal("currentColor", icon.Find("svg").GetAttribute("stroke"));
    }

    [Fact]
    public void An_unknown_icon_name_renders_nothing_rather_than_throwing()
    {
        // A typo'd icon name must degrade to a blank space, not take out the whole page through
        // WorkArea's error boundary.
        using var ctx = new Bunit.TestContext();

        var icon = ctx.RenderComponent<Icon>(p => p.Add(i => i.Name, "definitely-not-an-icon"));

        Assert.Empty(icon.FindAll("svg"));
    }

    [Theory]
    [InlineData("panel-left")]
    [InlineData("menu")]
    [InlineData("sun")]
    [InlineData("moon")]
    [InlineData("monitor")]
    [InlineData("settings")]
    [InlineData("info")]
    [InlineData("database")]
    [InlineData("message-square")]
    [InlineData("chevron-down")]
    [InlineData("x")]
    public void The_icons_the_shell_needs_all_exist(string name)
    {
        // The shell references these by string, so a missing one is invisible until someone opens the
        // page it is on. Enumerating them here turns that into a build-time failure.
        Assert.Contains(name, Icon.Names);
    }

    [Fact]
    public void No_icon_ships_that_nothing_renders()
    {
        // Phase A ships only the glyphs Phase A draws. Phases B-D add theirs alongside the components
        // that render them, so an unused glyph never sits in the set waiting for a caller that a later
        // phase might rename or never write.
        var rendered = new[]
        {
            "panel-left", "menu", "sun", "moon", "monitor", "settings",
            "info", "database", "message-square", "chevron-down", "x",
        };

        Assert.Equal(rendered.OrderBy(n => n, StringComparer.Ordinal), Icon.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_badge_renders_its_content_and_carries_its_tone_as_a_class()
    {
        using var ctx = new Bunit.TestContext();

        var badge = ctx.RenderComponent<Badge>(p => p
            .Add(b => b.Tone, BadgeTone.Success)
            .AddChildContent("connected"));

        Assert.Contains("connected", badge.Markup);
        Assert.Contains("success", badge.Find("span").ClassName);
    }

    [Fact]
    public void A_spinner_announces_itself_to_assistive_technology()
    {
        using var ctx = new Bunit.TestContext();

        var spinner = ctx.RenderComponent<Spinner>(p => p.Add(s => s.Label, "Running query"));

        Assert.Equal("status", spinner.Find("[role]").GetAttribute("role"));
        Assert.Contains("Running query", spinner.Markup);
    }

    [Fact]
    public void An_empty_state_renders_its_title_hint_and_actions()
    {
        using var ctx = new Bunit.TestContext();

        var empty = ctx.RenderComponent<EmptyState>(p => p
            .Add(e => e.Icon, "database")
            .Add(e => e.Title, "No databases yet")
            .Add(e => e.Hint, "Add one to get started")
            .AddChildContent("<button>Add database</button>"));

        Assert.Contains("No databases yet", empty.Markup);
        Assert.Contains("Add one to get started", empty.Markup);
        Assert.Contains("Add database", empty.Find("button").TextContent);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter UiPrimitiveTests`
Expected: FAIL — compile error, `SqlAgent.Host.Components.Shared.Ui` does not exist.

- [ ] **Step 3: Write `Icon.razor`**

Every glyph is stroke-only paths on a 24×24 grid with round caps, so one rendering path covers all
of them and nothing needs a per-icon fill flag.

```razor
@if (Paths.TryGetValue(Name, out var paths))
{
    <svg class="icon @Class" width="@Size" height="@Size" viewBox="0 0 24 24"
         fill="none" stroke="currentColor" stroke-width="1.75"
         stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
        @foreach (var d in paths)
        {
            <path d="@d" />
        }
    </svg>
}

@code {
    [Parameter, EditorRequired] public string Name { get; set; } = "";
    [Parameter] public int Size { get; set; } = 20;
    [Parameter] public string? Class { get; set; }

    /// <summary>
    /// Icon geometry, authored here rather than pulled from an icon package: this project ships no npm
    /// toolchain, and an icon font would be another binary asset to vendor for eighteen glyphs. Every
    /// glyph is stroke-only on a 24x24 grid, so the single render path above covers all of them.
    /// </summary>
    private static readonly Dictionary<string, string[]> Paths = new(StringComparer.Ordinal)
    {
        // Phase A ships only what Phase A renders. Later phases add their glyphs beside the components
        // that draw them — see UiPrimitiveTests.No_icon_ships_that_nothing_renders.
        ["panel-left"] = ["M5 4 H19 A2 2 0 0 1 21 6 V18 A2 2 0 0 1 19 20 H5 A2 2 0 0 1 3 18 V6 A2 2 0 0 1 5 4 Z", "M9.5 4 V20"],
        ["menu"] = ["M4 7 H20", "M4 12 H20", "M4 17 H20"],
        ["sun"] = [
            "M16 12 A4 4 0 1 1 8 12 A4 4 0 1 1 16 12",
            "M12 2 V4.5", "M12 19.5 V22", "M2 12 H4.5", "M19.5 12 H22",
            "M4.9 4.9 L6.7 6.7", "M17.3 17.3 L19.1 19.1", "M19.1 4.9 L17.3 6.7", "M4.9 19.1 L6.7 17.3"],
        ["moon"] = ["M21 13.2 A9 9 0 1 1 10.8 3 A7 7 0 0 0 21 13.2 Z"],
        ["monitor"] = ["M4 4 H20 A1 1 0 0 1 21 5 V15 A1 1 0 0 1 20 16 H4 A1 1 0 0 1 3 15 V5 A1 1 0 0 1 4 4 Z", "M9 20 H15", "M12 16 V20"],
        ["settings"] = [
            "M4 7 H20", "M4 12 H20", "M4 17 H20",
            "M11 7 A2 2 0 1 1 7 7 A2 2 0 1 1 11 7",
            "M17 12 A2 2 0 1 1 13 12 A2 2 0 1 1 17 12",
            "M11 17 A2 2 0 1 1 7 17 A2 2 0 1 1 11 17"],
        ["info"] = ["M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12", "M12 11.5 V16.5", "M12 7.6 V7.7"],
        ["database"] = [
            "M20 6 A8 3 0 1 1 4 6 A8 3 0 1 1 20 6",
            "M4 6 V18 C4 19.66 7.58 21 12 21 C16.42 21 20 19.66 20 18 V6",
            "M4 12 C4 13.66 7.58 15 12 15 C16.42 15 20 13.66 20 12"],
        ["message-square"] = ["M20 4 H4 A1 1 0 0 0 3 5 V15 A1 1 0 0 0 4 16 H7 V20 L12.5 16 H20 A1 1 0 0 0 21 15 V5 A1 1 0 0 0 20 4 Z"],
        ["chevron-down"] = ["M6 9.5 L12 15.5 L18 9.5"],
        ["x"] = ["M6 6 L18 18", "M18 6 L6 18"],
    };

    /// <summary>Available icon names. Exposed so a test can fail the build on a missing glyph rather
    /// than leaving a blank space to be discovered by eye.</summary>
    public static IReadOnlySet<string> Names { get; } = Paths.Keys.ToHashSet(StringComparer.Ordinal);
}
```

Create `src/SqlAgent.Host/Components/Shared/Ui/Icon.razor.css`:

```css
.icon { flex: 0 0 auto; display: block; }
```

- [ ] **Step 4: Write `Badge.razor`**

```razor
<span class="badge @ToneClass">@ChildContent</span>

@code {
    [Parameter] public BadgeTone Tone { get; set; } = BadgeTone.Neutral;
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private string ToneClass => Tone switch
    {
        BadgeTone.Primary => "primary",
        BadgeTone.Success => "success",
        BadgeTone.Warning => "warning",
        BadgeTone.Danger => "danger",
        _ => "neutral",
    };
}
```

Create `src/SqlAgent.Host/Components/Shared/Ui/BadgeTone.cs` — a shared enum rather than a nested
type, so other components can take it as a parameter:

```csharp
namespace SqlAgent.Host.Components.Shared.Ui;

/// <summary>Visual weight of a <c>Badge</c>. Maps to token-driven surface/text pairs, never to
/// literal colors, so both themes stay consistent.</summary>
public enum BadgeTone
{
    Neutral,
    Primary,
    Success,
    Warning,
    Danger,
}
```

Create `src/SqlAgent.Host/Components/Shared/Ui/Badge.razor.css`:

```css
.badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: var(--radius-round);
  font-size: var(--text-xs);
  font-weight: 500;
  white-space: nowrap;
}
.badge.neutral { background: var(--background-soft-200); color: var(--text-50); }
.badge.primary { background: var(--primary-50); color: var(--primary-500); }
.badge.success { background: var(--success-surface); color: var(--success-500); }
.badge.warning { background: var(--warning-surface); color: var(--warning-text); }
.badge.danger { background: var(--danger-surface); color: var(--danger-500); }
```

- [ ] **Step 5: Write `Spinner.razor`**

```razor
<span class="spinner-wrap" role="status">
    <span class="spinner" style="width:@(Size)px;height:@(Size)px" aria-hidden="true"></span>
    <span class="sr-only">@Label</span>
</span>

@code {
    [Parameter] public int Size { get; set; } = 16;
    [Parameter] public string Label { get; set; } = "Working";
}
```

Create `src/SqlAgent.Host/Components/Shared/Ui/Spinner.razor.css`:

```css
.spinner-wrap { display: inline-flex; align-items: center; }
.spinner {
  display: inline-block;
  border: 2px solid var(--base-200);
  border-top-color: var(--primary-500);
  border-radius: var(--radius-round);
  animation: spin .7s linear infinite;
}
@keyframes spin { to { transform: rotate(360deg); } }
```

- [ ] **Step 6: Write `EmptyState.razor`**

```razor
<div class="empty">
    <Icon Name="@Icon" Size="28" Class="empty-icon" />
    <p class="empty-title">@Title</p>
    @if (!string.IsNullOrWhiteSpace(Hint))
    {
        <p class="empty-hint">@Hint</p>
    }
    @if (ChildContent is not null)
    {
        <div class="empty-actions">@ChildContent</div>
    }
</div>

@code {
    [Parameter, EditorRequired] public string Icon { get; set; } = "info";
    [Parameter, EditorRequired] public string Title { get; set; } = "";
    [Parameter] public string? Hint { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
}
```

Create `src/SqlAgent.Host/Components/Shared/Ui/EmptyState.razor.css`:

```css
.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-8) var(--space-4);
  text-align: center;
  color: var(--text-100);
}
.empty ::deep .empty-icon { color: var(--base-200); }
.empty-title { color: var(--title-50); font-weight: 500; }
.empty-hint { font-size: var(--text-xs); }
.empty-actions { margin-top: var(--space-2); }
```

- [ ] **Step 7: Make the namespace available to every component**

Modify `src/SqlAgent.Host/Components/_Imports.razor` — append:

```razor
@using SqlAgent.Host.Components.Shared.Ui
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter UiPrimitiveTests`
Expected: PASS, 18 tests (7 facts + 11 theory cases).

- [ ] **Step 9: Commit**

```bash
git add src/SqlAgent.Host/Components/Shared/Ui src/SqlAgent.Host/Components/_Imports.razor \
        tests/SqlAgent.Tests/UiPrimitiveTests.cs
git commit -m "$(cat <<'EOF'
Add presentational UI primitives: Icon, Badge, Spinner, EmptyState

Icon geometry is authored in-repo as stroke-only 24x24 paths rather than pulled
from an icon package: there is no npm toolchain here, and an icon font would be
another binary to vendor for eighteen glyphs. A theory test enumerates the names
the shell references by string, so a missing glyph fails the build instead of
leaving a blank space to be found by eye.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Interactive UI primitives

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/Ui/Menu.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/MenuItem.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/Segmented.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/SegmentedOption.cs`
- Create: `src/SqlAgent.Host/Components/Shared/Ui/Modal.razor` + `.razor.css`
- Test: `tests/SqlAgent.Tests/UiInteractionTests.cs`

**Not in this task:** `ConfirmDialog`. Phase D's SQL blocks are its only caller, so it is built in
Phase D beside them rather than sitting here unrendered.

**Interfaces:**
- Consumes: `Icon`, tokens.
- Produces:
  - `<Menu Placement="MenuPlacement">` with `Trigger` and `ChildContent` render fragments. `MenuPlacement` is `Bottom | Top | Right`. Closes on backdrop click and on Escape.
  - `<MenuItem Icon="string?" OnClick="EventCallback" Danger="bool">child</MenuItem>` and an optional `Trailing` fragment.
  - `<Segmented Options="IReadOnlyList<SegmentedOption>" Value="string" ValueChanged="EventCallback<string>" AriaLabel="string?" />` where `SegmentedOption` is `record SegmentedOption(string Value, string Label, string? Icon = null)`.
  - `<Modal Title="string" OnClose="EventCallback">child</Modal>` with an optional `Footer` fragment.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/UiInteractionTests.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using SqlAgent.Host.Components.Shared.Ui;

namespace SqlAgent.Tests;

public class UiInteractionTests
{
    [Fact]
    public void A_menu_is_closed_until_its_trigger_is_clicked()
    {
        using var ctx = new Bunit.TestContext();

        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>open me</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));

        Assert.Empty(menu.FindAll("#body"));

        menu.Find(".menu-trigger").Click();

        Assert.Single(menu.FindAll("#body"));
    }

    [Fact]
    public void Clicking_the_backdrop_closes_the_menu()
    {
        // Without a backdrop the only way out of an open menu is re-clicking the trigger, which is not
        // how any menu on any platform behaves. It is a plain element rather than a document-level JS
        // listener so it works in the static first render too.
        using var ctx = new Bunit.TestContext();
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));
        menu.Find(".menu-trigger").Click();

        menu.Find(".menu-backdrop").Click();

        Assert.Empty(menu.FindAll("#body"));
    }

    [Fact]
    public void Escape_closes_the_menu()
    {
        using var ctx = new Bunit.TestContext();
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent("<div id=\"body\">contents</div>"));
        menu.Find(".menu-trigger").Click();

        menu.Find(".menu-root").KeyDown(Key.Escape);

        Assert.Empty(menu.FindAll("#body"));
    }

    [Fact]
    public void Choosing_a_menu_item_invokes_its_callback_and_closes_the_menu()
    {
        using var ctx = new Bunit.TestContext();
        var clicked = false;
        var menu = ctx.RenderComponent<Menu>(p => p
            .Add(m => m.Trigger, (RenderFragment)(b => b.AddMarkupContent(0, "<span>t</span>")))
            .AddChildContent<MenuItem>(ip => ip
                .Add(i => i.OnClick, EventCallback.Factory.Create(new object(), () => clicked = true))
                .AddChildContent("Settings")));
        menu.Find(".menu-trigger").Click();

        menu.Find(".menu-item").Click();

        Assert.True(clicked);
        Assert.Empty(menu.FindAll(".menu-item"));
    }

    [Fact]
    public void A_segmented_control_marks_the_selected_option_and_reports_changes()
    {
        using var ctx = new Bunit.TestContext();
        var chosen = "system";
        var segmented = ctx.RenderComponent<Segmented>(p => p
            .Add(s => s.Options, new List<SegmentedOption>
            {
                new("system", "System", "monitor"),
                new("light", "Light", "sun"),
                new("dark", "Dark", "moon"),
            })
            .Add(s => s.Value, chosen)
            .Add(s => s.ValueChanged, EventCallback.Factory.Create<string>(new object(), v => chosen = v)));

        var buttons = segmented.FindAll("button");
        Assert.Equal(3, buttons.Count);
        Assert.Equal("true", buttons[0].GetAttribute("aria-pressed"));

        buttons[2].Click();

        Assert.Equal("dark", chosen);
    }

    [Fact]
    public void A_modal_renders_its_title_and_closes_on_escape_and_on_the_scrim()
    {
        using var ctx = new Bunit.TestContext();
        var closes = 0;
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About SQL Agent")
            .Add(m => m.OnClose, EventCallback.Factory.Create(new object(), () => closes++))
            .AddChildContent("<p>body</p>"));

        Assert.Contains("About SQL Agent", modal.Markup);
        Assert.Equal("dialog", modal.Find("[role]").GetAttribute("role"));

        modal.Find(".modal-scrim").Click();
        modal.Find(".modal-root").KeyDown(Key.Escape);

        Assert.Equal(2, closes);
    }

    [Fact]
    public void A_click_inside_the_modal_panel_does_not_close_it()
    {
        // The scrim and the panel are nested, so without stopPropagation every click on the dialog's
        // own content would bubble to the scrim's handler and dismiss the dialog mid-interaction.
        using var ctx = new Bunit.TestContext();
        var closes = 0;
        var modal = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "t")
            .Add(m => m.OnClose, EventCallback.Factory.Create(new object(), () => closes++))
            .AddChildContent("<p id=\"inside\">body</p>"));

        modal.Find("#inside").Click();

        Assert.Equal(0, closes);
    }

    [Fact]
    public void A_modal_footer_is_rendered_only_when_supplied()
    {
        // The footer is the slot Phase D's confirm dialog will fill. It must be genuinely optional, or
        // every plain modal (About, for one) grows an empty bordered strip.
        using var ctx = new Bunit.TestContext();

        var plain = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About")
            .AddChildContent("<p>body</p>"));
        Assert.Empty(plain.FindAll(".modal-foot"));

        var withFooter = ctx.RenderComponent<Modal>(p => p
            .Add(m => m.Title, "About")
            .Add(m => m.Footer, (RenderFragment)(b => b.AddMarkupContent(0, "<button>OK</button>")))
            .AddChildContent("<p>body</p>"));
        Assert.Single(withFooter.FindAll(".modal-foot"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter UiInteractionTests`
Expected: FAIL — compile error, `Menu`/`MenuItem`/`Segmented`/`Modal` not found.

- [ ] **Step 3: Write `Menu.razor`**

```razor
@* tabindex is what makes @onkeydown fire on a div at all, and -1 keeps the wrapper out of the tab
   order so Escape works without adding a phantom tab stop before every menu. *@
<div class="menu-root" @onkeydown="OnKeyDown" tabindex="-1">
    <div class="menu-trigger" @onclick="Toggle">@Trigger</div>

    @if (_open)
    {
        @* A plain element, not a document-level JS listener: this works in the static first render
           and needs no interop, and clicks on the menu's own panel never reach it because the panel
           is a sibling painted above it. *@
        <div class="menu-backdrop" @onclick="Close"></div>
        <div class="menu-panel @PlacementClass" role="menu">
            <CascadingValue Value="this" IsFixed="true">@ChildContent</CascadingValue>
        </div>
    }
</div>

@code {
    [Parameter] public RenderFragment? Trigger { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public MenuPlacement Placement { get; set; } = MenuPlacement.Bottom;

    private bool _open;

    private string PlacementClass => Placement switch
    {
        MenuPlacement.Top => "place-top",
        MenuPlacement.Right => "place-right",
        _ => "place-bottom",
    };

    private void Toggle() => _open = !_open;

    public void Close() => _open = false;

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") Close();
    }
}
```

Create `src/SqlAgent.Host/Components/Shared/Ui/MenuPlacement.cs`:

```csharp
namespace SqlAgent.Host.Components.Shared.Ui;

/// <summary>Where a <c>Menu</c>'s panel sits relative to its trigger.</summary>
public enum MenuPlacement
{
    Bottom,
    Top,
    Right,
}
```

Add the `KeyboardEventArgs` using to `_Imports.razor` if it is not already there (it comes with
`Microsoft.AspNetCore.Components.Web`, which the default `_Imports.razor` includes — verify with
`cat src/SqlAgent.Host/Components/_Imports.razor` and add `@using Microsoft.AspNetCore.Components.Web`
only if absent).

Create `src/SqlAgent.Host/Components/Shared/Ui/Menu.razor.css`:

```css
.menu-root { position: relative; outline: none; }
.menu-trigger { cursor: pointer; }
/* Below the panel's z-index, above everything else, so an open menu is modal to the page without a
   JS focus trap. */
.menu-backdrop { position: fixed; inset: 0; z-index: 40; }
.menu-panel {
  position: absolute;
  z-index: 41;
  min-width: 200px;
  padding: var(--space-1);
  background: var(--dropdown-background);
  border: 1px solid var(--base-200);
  border-radius: var(--radius-control);
  box-shadow: var(--shadow-menu);
}
.menu-panel.place-bottom { top: calc(100% + 6px); left: 0; }
.menu-panel.place-top { bottom: calc(100% + 6px); left: 0; }
.menu-panel.place-right { top: 0; left: calc(100% + 6px); }
```

- [ ] **Step 4: Write `MenuItem.razor`**

```razor
<button type="button" class="menu-item @(Danger ? "danger" : "")" role="menuitem" @onclick="Activate">
    @if (!string.IsNullOrWhiteSpace(Icon))
    {
        <Icon Name="@Icon" Size="16" />
    }
    <span class="menu-item-label">@ChildContent</span>
    @if (Trailing is not null)
    {
        <span class="menu-item-trailing" @onclick:stopPropagation="true">@Trailing</span>
    }
</button>

@code {
    [Parameter] public string? Icon { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public RenderFragment? Trailing { get; set; }
    [Parameter] public EventCallback OnClick { get; set; }
    [Parameter] public bool Danger { get; set; }

    /// <summary>The menu this item lives in, so choosing an item dismisses it. Cascaded by Menu.</summary>
    [CascadingParameter] private Menu? Owner { get; set; }

    private async Task Activate()
    {
        // Close first: if OnClick navigates, the menu would otherwise be left open behind the new page,
        // and a caller that throws would strand it open with no backdrop click able to reach it.
        Owner?.Close();
        await OnClick.InvokeAsync();
    }
}
```

Create `src/SqlAgent.Host/Components/Shared/Ui/MenuItem.razor.css`:

```css
.menu-item {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  width: 100%;
  border: none;
  border-radius: var(--radius-control);
  background: none;
  color: var(--text-50);
  padding: var(--space-2) var(--space-3);
  text-align: left;
}
.menu-item:hover:not(:disabled) { background: var(--dropdown-hover-background); color: var(--title-50); }
.menu-item.danger { color: var(--danger-500); }
.menu-item-label { flex: 1; min-width: 0; }
.menu-item-trailing { margin-left: auto; display: flex; align-items: center; }
```

Note: the `Trailing` slot carries interactive content (the theme segmented control lives there), so
its wrapper stops click propagation — without that, adjusting the theme would also fire the row's
`OnClick` and close the menu.

- [ ] **Step 5: Write `Segmented.razor`**

```razor
<div class="segmented" role="group" aria-label="@AriaLabel">
    @foreach (var option in Options)
    {
        var selected = option.Value == Value;
        <button type="button"
                class="segment @(selected ? "selected" : "")"
                aria-pressed="@(selected ? "true" : "false")"
                title="@option.Label"
                @onclick="() => Select(option.Value)">
            @if (!string.IsNullOrWhiteSpace(option.Icon))
            {
                <Icon Name="@option.Icon" Size="15" />
            }
            @if (ShowLabels)
            {
                <span>@option.Label</span>
            }
            else
            {
                <span class="sr-only">@option.Label</span>
            }
        </button>
    }
</div>

@code {
    [Parameter, EditorRequired] public IReadOnlyList<SegmentedOption> Options { get; set; } = [];
    [Parameter] public string Value { get; set; } = "";
    [Parameter] public EventCallback<string> ValueChanged { get; set; }
    [Parameter] public string? AriaLabel { get; set; }
    [Parameter] public bool ShowLabels { get; set; }

    private Task Select(string value) => value == Value ? Task.CompletedTask : ValueChanged.InvokeAsync(value);
}
```

Create `src/SqlAgent.Host/Components/Shared/Ui/SegmentedOption.cs`:

```csharp
namespace SqlAgent.Host.Components.Shared.Ui;

/// <summary>One choice in a <c>Segmented</c> control. <paramref name="Icon"/> is an
/// <c>Icon</c> name; when labels are hidden it is the only visible content, and the label is still
/// rendered for assistive technology.</summary>
public record SegmentedOption(string Value, string Label, string? Icon = null);
```

Create `src/SqlAgent.Host/Components/Shared/Ui/Segmented.razor.css`:

```css
.segmented {
  display: inline-flex;
  gap: 2px;
  padding: 2px;
  background: var(--background-soft-200);
  border: 1px solid var(--base-100);
  border-radius: var(--radius-round);
}
.segment {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: none;
  background: none;
  color: var(--text-100);
  padding: 4px 8px;
  border-radius: var(--radius-round);
  font-size: var(--text-xs);
}
.segment:hover:not(.selected) { color: var(--title-50); }
.segment.selected { background: var(--background-100); color: var(--title-50); }
```

- [ ] **Step 6: Write `Modal.razor`**

```razor
<div class="modal-root" @onkeydown="OnKeyDown" tabindex="-1">
    <div class="modal-scrim" @onclick="Close"></div>
    @* stopPropagation, not a separate handler: the panel sits inside the scrim's stacking context, so
       every click on the dialog's own content would otherwise bubble to the scrim and dismiss it. *@
    <div class="modal-panel" role="dialog" aria-modal="true" aria-label="@Title"
         @onclick:stopPropagation="true">
        <div class="modal-head">
            <h3>@Title</h3>
            <button type="button" class="ghost modal-close" @onclick="Close" aria-label="Close">
                <Icon Name="x" Size="18" />
            </button>
        </div>
        <div class="modal-body">@ChildContent</div>
        @if (Footer is not null)
        {
            <div class="modal-foot">@Footer</div>
        }
    </div>
</div>

@code {
    [Parameter, EditorRequired] public string Title { get; set; } = "";
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public RenderFragment? Footer { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private Task Close() => OnClose.InvokeAsync();

    private Task OnKeyDown(KeyboardEventArgs e) => e.Key == "Escape" ? Close() : Task.CompletedTask;
}
```

Create `src/SqlAgent.Host/Components/Shared/Ui/Modal.razor.css`:

```css
.modal-root { position: fixed; inset: 0; z-index: 60; display: grid; place-items: center; outline: none; }
.modal-scrim { position: absolute; inset: 0; background: var(--scrim); }
.modal-panel {
  position: relative;
  z-index: 1;
  width: min(520px, calc(100vw - 32px));
  max-height: calc(100vh - 64px);
  overflow: auto;
  background: var(--background-100);
  border: 1px solid var(--base-100);
  border-radius: var(--radius-card);
  box-shadow: var(--shadow-menu);
}
.modal-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding: var(--space-4) var(--space-5);
  border-bottom: 1px solid var(--base-100);
}
.modal-body { padding: var(--space-5); }
.modal-foot {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
  padding: var(--space-4) var(--space-5);
  border-top: 1px solid var(--base-100);
}
.modal-close { padding: 4px; }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter UiInteractionTests`
Expected: PASS, 8 tests.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Host/Components/Shared/Ui tests/SqlAgent.Tests/UiInteractionTests.cs
git commit -m "$(cat <<'EOF'
Add interactive UI primitives: Menu, MenuItem, Segmented, Modal

Dismissal is built from plain elements — a backdrop element and stopPropagation
— rather than document-level JS listeners, so these work in the static first
render and need no interop, and bUnit can test them without a JS engine.

Modal's footer is an optional slot; Phase D's confirm dialog fills it, beside
the SQL blocks that are its only caller.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Theme toggle wired to the browser

**Files:**
- Create: `src/SqlAgent.Host/Components/Shared/Ui/ThemeToggle.razor`
- Test: `tests/SqlAgent.Tests/ThemeToggleTests.cs`

**Interfaces:**
- Consumes: `Segmented`, `SegmentedOption`, `window.sqlAgentUi` from Task 1.
- Produces: `<ThemeToggle ShowLabels="bool" />`. On first render it reads `sqlAgentUi.getTheme()`; on change it calls `sqlAgentUi.setTheme(value)`.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ThemeToggleTests.cs`:

```csharp
using Bunit;
using SqlAgent.Host.Components.Shared.Ui;

namespace SqlAgent.Tests;

public class ThemeToggleTests
{
    [Fact]
    public void The_stored_theme_is_read_from_the_browser_on_first_render()
    {
        // The server cannot know the theme: it lives in localStorage, applied by theme.js before the
        // circuit connects. If the toggle rendered its own default instead of reading it back, the
        // control would show "System" on a page that is actually pinned to dark.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("dark");

        var toggle = ctx.RenderComponent<ThemeToggle>();

        var dark = toggle.FindAll("button").Single(b => b.TextContent.Contains("Dark"));
        Assert.Equal("true", dark.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Choosing_a_theme_pushes_it_to_the_browser()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
        var toggle = ctx.RenderComponent<ThemeToggle>();

        toggle.FindAll("button").Single(b => b.TextContent.Contains("Light")).Click();

        var invocation = ctx.JSInterop.VerifyInvoke("sqlAgentUi.setTheme");
        Assert.Equal("light", invocation.Arguments[0]);
    }

    [Fact]
    public void All_three_theme_choices_are_offered()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");

        var toggle = ctx.RenderComponent<ThemeToggle>();

        Assert.Equal(3, toggle.FindAll("button").Count);
        Assert.Contains("System", toggle.Markup);
        Assert.Contains("Light", toggle.Markup);
        Assert.Contains("Dark", toggle.Markup);
    }

    [Fact]
    public void A_browser_that_cannot_report_a_theme_falls_back_to_system()
    {
        // JSDisconnectedException on a torn-down circuit, or a private mode where localStorage throws,
        // must not take the page down through WorkArea's boundary.
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetException(new InvalidOperationException("no storage"));

        var toggle = ctx.RenderComponent<ThemeToggle>();

        var system = toggle.FindAll("button").Single(b => b.TextContent.Contains("System"));
        Assert.Equal("true", system.GetAttribute("aria-pressed"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ThemeToggleTests`
Expected: FAIL — compile error, `ThemeToggle` not found.

- [ ] **Step 3: Write `ThemeToggle.razor`**

```razor
@using Microsoft.JSInterop
@inject IJSRuntime JS

<Segmented Options="Options" Value="@_theme" ValueChanged="ApplyAsync"
           AriaLabel="Theme" ShowLabels="ShowLabels" />

@code {
    [Parameter] public bool ShowLabels { get; set; }

    private static readonly IReadOnlyList<SegmentedOption> Options =
    [
        new("system", "System", "monitor"),
        new("light", "Light", "sun"),
        new("dark", "Dark", "moon"),
    ];

    private string _theme = "system";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Read, do not assume: theme.js already applied the stored value to <html> before this circuit
        // existed, so the server's only way to show the right selection is to ask the browser.
        if (!firstRender) return;
        try
        {
            _theme = await JS.InvokeAsync<string>("sqlAgentUi.getTheme");
        }
        catch (Exception)
        {
            // Private-mode localStorage throws rather than returning null, and a disconnected circuit
            // throws JSDisconnectedException. Neither is worth failing the page over: "system" is the
            // same default theme.js itself falls back to, so the control stays truthful.
            _theme = "system";
        }
        StateHasChanged();
    }

    private async Task ApplyAsync(string theme)
    {
        _theme = theme;
        try
        {
            await JS.InvokeVoidAsync("sqlAgentUi.setTheme", theme);
        }
        catch (JSDisconnectedException)
        {
            // The tab is gone; there is nothing left to theme.
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ThemeToggleTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/SqlAgent.Host/Components/Shared/Ui/ThemeToggle.razor tests/SqlAgent.Tests/ThemeToggleTests.cs
git commit -m "$(cat <<'EOF'
Add the theme toggle, reading the stored preference back from the browser

The theme lives in localStorage and is applied by theme.js before the circuit
exists, so the server cannot know it. The control reads it on first render
rather than rendering its own default, which would show "System" on a page
already pinned to dark. A browser that cannot report one falls back to system
instead of failing the page.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Application shell — sidebar, collapse, mobile drawer

**Files:**
- Modify: `src/SqlAgent.Host/Components/Layout/MainLayout.razor:1-12`
- Create: `src/SqlAgent.Host/Components/Layout/MainLayout.razor.css`
- Create: `src/SqlAgent.Host/Components/Layout/Sidebar.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Layout/SidebarHeader.razor` + `.razor.css`
- Create: `src/SqlAgent.Host/Components/Layout/SidebarNav.razor` + `.razor.css`
- Test: `tests/SqlAgent.Tests/ShellTests.cs`

**Interfaces:**
- Consumes: `Icon`, `SchemaRail` (existing), `WorkArea` (existing), `window.sqlAgentUi.getSidebar`/`setSidebar`.
- Produces:
  - `<Sidebar />` — owns collapse and drawer state.
  - `<SidebarHeader Collapsed="bool" OnToggleCollapse="EventCallback" OnCloseDrawer="EventCallback" />`
  - `<SidebarNav Collapsed="bool" />`
  - CSS contract: `.app`, `.app-main`, `.app-card` in `app.css`; `html.sidebar-collapsed` narrows the sidebar pre-paint.

**Note on scope:** this phase's `SidebarNav` carries the two routes that exist — Workspace and
Connections. Phase B replaces them with New Chat and Search. Do **not** add non-functional New
Chat / Search buttons here: a button that does nothing is worse than the link it replaced.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/ShellTests.cs`:

```csharp
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class ShellTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly Bunit.TestContext _ctx = new();

    public ShellTests()
    {
        // The sidebar hosts SchemaRail, which resolves the connection services, so the shell test needs
        // the same registrations the rail's own tests use.
        _conn.Open();
        _ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        _ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        _ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        _ctx.Services.AddScoped<DatabaseConnectionService>();
        _ctx.Services.AddScoped<TablePolicyService>();
        _ctx.Services.AddScoped<ScopedRunner>();
        _ctx.Services.AddScoped<AppState>();
        _ctx.Services.AddLogging();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.JSInterop.Setup<string>("sqlAgentUi.getSidebar").SetResult("expanded");

        using var scope = _ctx.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void The_sidebar_renders_the_product_mark_and_the_routes_that_exist()
    {
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.Contains("SQL Agent", sidebar.Markup);
        // Phase A ships the routes that exist. New Chat and Search arrive in Phase B with the pages
        // behind them; a button that does nothing is worse than the link it replaced.
        Assert.Contains("Workspace", sidebar.Markup);
        Assert.Contains("Connections", sidebar.Markup);
    }

    [Fact]
    public void Collapsing_the_sidebar_marks_it_collapsed_and_persists_the_choice()
    {
        var sidebar = _ctx.RenderComponent<Sidebar>();

        sidebar.Find("[data-testid=collapse-toggle]").Click();

        Assert.Contains("collapsed", sidebar.Find("aside").ClassName);
        var invocation = _ctx.JSInterop.VerifyInvoke("sqlAgentUi.setSidebar");
        Assert.Equal("collapsed", invocation.Arguments[0]);
    }

    [Fact]
    public void The_collapsed_state_is_read_back_from_the_browser_on_first_render()
    {
        // Same reason as the theme: the class is applied to <html> pre-paint, so the component has to
        // ask rather than assume, or an expanded-looking sidebar renders inside a narrow shell.
        using var ctx = new Bunit.TestContext();
        ctx.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite(_conn));
        ctx.Services.AddSingleton<ISecretStore, InMemorySecretStore>();
        ctx.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
        ctx.Services.AddScoped<DatabaseConnectionService>();
        ctx.Services.AddScoped<TablePolicyService>();
        ctx.Services.AddScoped<ScopedRunner>();
        ctx.Services.AddScoped<AppState>();
        ctx.Services.AddLogging();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getSidebar").SetResult("collapsed");

        var sidebar = ctx.RenderComponent<Sidebar>();

        Assert.Contains("collapsed", sidebar.Find("aside").ClassName);
    }

    [Fact]
    public void The_drawer_opens_and_closes_for_narrow_viewports()
    {
        var sidebar = _ctx.RenderComponent<Sidebar>();

        sidebar.Find("[data-testid=drawer-open]").Click();
        Assert.Contains("drawer-open", sidebar.Find("aside").ClassName);

        sidebar.Find(".sidebar-scrim").Click();
        Assert.DoesNotContain("drawer-open", sidebar.Find("aside").ClassName);
    }

    [Fact]
    public void The_schema_rail_still_lives_in_the_sidebar()
    {
        // Phase A must not remove a working surface. The rail is the only visibility control until the
        // config page lands in Phase C.
        var sidebar = _ctx.RenderComponent<Sidebar>();

        Assert.Contains("Connection", sidebar.Markup);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _conn.Dispose();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ShellTests`
Expected: FAIL — compile error, `Sidebar` not found.

- [ ] **Step 3: Write `SidebarHeader.razor`**

```razor
<div class="sidebar-head">
    <a class="brand" href="/">
        <Icon Name="database" Size="22" Class="brand-mark" />
        <span class="brand-name">SQL Agent</span>
    </a>

    <button type="button" class="ghost icon-button drawer-close" data-testid="drawer-close"
            @onclick="OnCloseDrawer" aria-label="Close menu">
        <Icon Name="x" Size="18" />
    </button>

    <button type="button" class="ghost icon-button collapse-toggle" data-testid="collapse-toggle"
            @onclick="OnToggleCollapse"
            aria-label="@(Collapsed ? "Expand sidebar" : "Collapse sidebar")">
        <Icon Name="panel-left" Size="18" />
    </button>
</div>

@code {
    [Parameter] public bool Collapsed { get; set; }
    [Parameter] public EventCallback OnToggleCollapse { get; set; }
    [Parameter] public EventCallback OnCloseDrawer { get; set; }
}
```

Create `src/SqlAgent.Host/Components/Layout/SidebarHeader.razor.css`:

```css
.sidebar-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
  margin-bottom: var(--space-6);
}
.brand { display: flex; align-items: center; gap: var(--space-2); color: var(--title-50); }
.brand:hover { text-decoration: none; }
.brand ::deep .brand-mark { color: var(--primary-500); }
.brand-name { font-size: var(--text-base); font-weight: 600; }
.icon-button { padding: 6px; border-radius: var(--radius-control); }

/* The drawer close button only exists on narrow viewports; the collapse toggle only on wide ones. */
.drawer-close { display: inline-flex; }
.collapse-toggle { display: none; }
@media (min-width: 1024px) {
  .drawer-close { display: none; }
  .collapse-toggle { display: inline-flex; }
}
```

- [ ] **Step 4: Write `SidebarNav.razor`**

```razor
@* Phase A lists the routes that exist. Phase B replaces these with New Chat and Search once the
   chat pages are behind them. *@
<nav class="sidebar-nav">
    <NavLink class="nav-row" href="/" Match="NavLinkMatch.All">
        <Icon Name="message-square" Size="18" />
        <span class="nav-label">Workspace</span>
    </NavLink>
    <NavLink class="nav-row" href="/connections">
        <Icon Name="database" Size="18" />
        <span class="nav-label">Connections</span>
    </NavLink>
    <NavLink class="nav-row" href="/settings">
        <Icon Name="settings" Size="18" />
        <span class="nav-label">Settings</span>
    </NavLink>
</nav>

@code {
    [Parameter] public bool Collapsed { get; set; }
}
```

Create `src/SqlAgent.Host/Components/Layout/SidebarNav.razor.css`:

```css
.sidebar-nav { display: flex; flex-direction: column; gap: 2px; margin-bottom: var(--space-4); }
.nav-row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-control);
  color: var(--text-100);
  font-weight: 500;
}
.nav-row:hover { background: var(--background-soft-100); color: var(--title-50); text-decoration: none; }
.nav-row.active { background: var(--primary-50); color: var(--primary-500); }
.nav-label { white-space: nowrap; overflow: hidden; }
```

`NavLink` adds its own `active` class on the matching route, which is what the `.nav-row.active` rule
above styles.

- [ ] **Step 5: Write `Sidebar.razor`**

```razor
@using Microsoft.JSInterop
@inject IJSRuntime JS

@if (_drawerOpen)
{
    <div class="sidebar-scrim" @onclick="CloseDrawer"></div>
}

<button type="button" class="ghost drawer-trigger" data-testid="drawer-open"
        @onclick="OpenDrawer" aria-label="Open menu">
    <Icon Name="menu" Size="20" />
</button>

<aside class="sidebar custom-scroll @(_collapsed ? "collapsed" : "") @(_drawerOpen ? "drawer-open" : "")">
    <SidebarHeader Collapsed="_collapsed"
                   OnToggleCollapse="ToggleCollapseAsync"
                   OnCloseDrawer="CloseDrawer" />

    <SidebarNav Collapsed="_collapsed" />

    <div class="sidebar-body custom-scroll">
        @* Phase C replaces the rail with the Databases section and the config page. Until then it is
           the only place a connection can be picked or a table hidden, so it stays. *@
        <SchemaRail />
    </div>

    <div class="sidebar-foot">
        @* UserCard arrives in Task 6. *@
        @UserSlot
    </div>
</aside>

@code {
    /// <summary>Filled by Task 6 with <c>UserCard</c>. A slot rather than a hard reference so this
    /// component is testable before that component exists.</summary>
    [Parameter] public RenderFragment? UserSlot { get; set; }

    private bool _collapsed;
    private bool _drawerOpen;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // theme.js put html.sidebar-collapsed in place before this circuit existed; read it back rather
        // than rendering an expanded sidebar inside an already-narrowed shell.
        if (!firstRender) return;
        try
        {
            _collapsed = await JS.InvokeAsync<string>("sqlAgentUi.getSidebar") == "collapsed";
        }
        catch (Exception)
        {
            _collapsed = false;
        }
        StateHasChanged();
    }

    private async Task ToggleCollapseAsync()
    {
        _collapsed = !_collapsed;
        try
        {
            await JS.InvokeVoidAsync("sqlAgentUi.setSidebar", _collapsed ? "collapsed" : "expanded");
        }
        catch (JSDisconnectedException)
        {
            // Tab closed mid-click; the state is irrelevant now.
        }
    }

    private void OpenDrawer() => _drawerOpen = true;

    private void CloseDrawer() => _drawerOpen = false;
}
```

Create `src/SqlAgent.Host/Components/Layout/Sidebar.razor.css`:

```css
.sidebar {
  display: flex;
  flex-direction: column;
  width: var(--sidebar-width);
  flex: 0 0 var(--sidebar-width);
  padding: var(--space-5);
  background: var(--background-50);
  border-right: 1px solid var(--base-100);
  overflow: hidden;
  transition: width .25s, flex-basis .25s, transform .25s;
}
.sidebar.collapsed { width: var(--sidebar-collapsed-width); flex-basis: var(--sidebar-collapsed-width); }
/* Labels disappear, icons stay: a collapsed rail of icons is navigable, a rail of clipped words is not. */
.sidebar.collapsed ::deep .nav-label,
.sidebar.collapsed ::deep .brand-name,
.sidebar.collapsed .sidebar-body,
.sidebar.collapsed .sidebar-foot { display: none; }

.sidebar-body { flex: 1; min-height: 0; overflow-y: auto; }
.sidebar-foot { margin-top: var(--space-4); }

.sidebar-scrim { position: fixed; inset: 0; background: var(--scrim); z-index: 49; }
.drawer-trigger {
  position: fixed;
  top: var(--space-3);
  left: var(--space-3);
  z-index: 30;
  padding: 6px;
}

/* Below 1024px the sidebar leaves the flow entirely and slides in over the page. */
@media (max-width: 1023px) {
  .sidebar {
    position: fixed;
    inset: 0 auto 0 0;
    z-index: 50;
    transform: translateX(-100%);
  }
  .sidebar.drawer-open { transform: translateX(0); }
  .sidebar.collapsed { width: var(--sidebar-width); flex-basis: var(--sidebar-width); }
}
@media (min-width: 1024px) {
  .drawer-trigger { display: none; }
  .sidebar-scrim { display: none; }
}
```

- [ ] **Step 6: Rewrite `MainLayout.razor`**

```razor
@inherits LayoutComponentBase

@* The header nav is gone: the sidebar owns navigation now. WorkArea stays exactly where it was —
   it is the error boundary for every page, and its LocationChanged recovery depends on living
   above @Body rather than inside a page. *@
<div class="app">
    <Sidebar />
    <main class="app-main">
        <div class="app-card custom-scroll">
            <WorkArea>@Body</WorkArea>
        </div>
    </main>
</div>
```

Create `src/SqlAgent.Host/Components/Layout/MainLayout.razor.css`:

```css
/* Layout lives in app.css (.app, .app-main, .app-card) because the restyled pages and the tests
   reference those class names directly. This file holds only what is specific to the layout
   component itself. */
.app { min-height: 100vh; }
@media (max-width: 1023px) {
  /* Leave room for the fixed drawer trigger so it never covers page content. */
  .app-main { padding-top: 56px; }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter ShellTests`
Expected: PASS, 5 tests.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS. `WorkAreaBoundaryTests` and the existing page tests must be unaffected — `WorkArea`
is still the direct wrapper of `@Body`. If `WorkAreaBoundaryTests` renders `MainLayout` directly it
may now need the `sqlAgentUi.getSidebar` interop stub; add
`ctx.JSInterop.Mode = JSRuntimeMode.Loose;` to that fixture if so, and say so in the commit message.

- [ ] **Step 9: Commit**

```bash
git add src/SqlAgent.Host/Components/Layout tests/SqlAgent.Tests/ShellTests.cs
git commit -m "$(cat <<'EOF'
Replace the two-link header with a sidebar application shell

MainLayout was a <header> with two links. It becomes a sidebar plus an inset
main card: product mark, collapse toggle, nav rows, the existing schema rail,
and a slot for the user card. WorkArea stays the direct wrapper of @Body, so its
error boundary and LocationChanged recovery are unchanged.

Collapse state is read back from the browser on first render, for the same
reason the theme is: theme.js applies html.sidebar-collapsed before the circuit
exists. Below 1024px the sidebar becomes an overlay drawer.

Nav lists the routes that exist today. New Chat and Search arrive in Phase B
with the pages behind them.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Host info service and the user card

**Files:**
- Create: `src/SqlAgent.Host/Web/HostInfo.cs`
- Create: `src/SqlAgent.Host/Components/Layout/UserCard.razor` + `.razor.css`
- Modify: `src/SqlAgent.Host/Components/Layout/Sidebar.razor` (fill the user slot)
- Modify: `src/SqlAgent.Host/Program.cs:48` (register `HostInfo`)
- Test: `tests/SqlAgent.Tests/UserCardTests.cs`

**Interfaces:**
- Consumes: `Menu`, `MenuItem`, `ThemeToggle`, `Modal`, `Icon`, `IConfiguration`, `LoopbackUrl`, `LaunchUrlFile`.
- Produces:
  - `HostInfo` (singleton) with: `string AccountName`, `string MachineName`, `string Initials`, `string Version`, `string StoreDirectory`, `string BindUrl`, `int Port`.
  - `<UserCard />` rendering the account and a menu (Settings, Theme, About).

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/UserCardTests.cs`:

```csharp
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Host.Components.Layout;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class UserCardTests
{
    private static Bunit.TestContext NewContext()
    {
        var ctx = new Bunit.TestContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlAgent:Storage:ConnectionString"] = "Data Source=/tmp/sqlagent-test/sqlagent.db",
                ["SqlAgent:Web:Port"] = "5150",
            })
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(config);
        ctx.Services.AddSingleton<HostInfo>();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
        return ctx;
    }

    [Fact]
    public void The_card_shows_the_os_account_and_machine()
    {
        // There is no user model here: the host is single-user and loopback-only, authenticated by a
        // launch token. Showing the OS account is true without inventing an identity.
        using var ctx = NewContext();

        var card = ctx.RenderComponent<UserCard>();

        Assert.Contains(Environment.UserName, card.Markup);
        Assert.Contains(Environment.MachineName, card.Markup);
    }

    [Fact]
    public void There_is_no_sign_out_action()
    {
        // Nothing to sign out of. An item that appears to end a session but cannot would be a lie about
        // the security model.
        using var ctx = NewContext();
        var card = ctx.RenderComponent<UserCard>();

        card.Find(".user-card-trigger").Click();

        Assert.DoesNotContain("Sign out", card.Markup);
    }

    [Fact]
    public void The_menu_offers_settings_theme_and_about()
    {
        using var ctx = NewContext();
        var card = ctx.RenderComponent<UserCard>();

        card.Find(".user-card-trigger").Click();

        Assert.Contains("Settings", card.Markup);
        Assert.Contains("Theme", card.Markup);
        Assert.Contains("About", card.Markup);
    }

    [Fact]
    public void About_reports_the_port_and_store_location_from_configuration()
    {
        using var ctx = NewContext();
        var card = ctx.RenderComponent<UserCard>();
        card.Find(".user-card-trigger").Click();

        card.FindAll(".menu-item").Single(i => i.TextContent.Contains("About")).Click();

        Assert.Contains("5150", card.Markup);
        Assert.Contains("sqlagent-test", card.Markup);
    }

    [Fact]
    public void Host_info_derives_initials_from_the_account_name()
    {
        var config = new ConfigurationBuilder().Build();
        var info = new HostInfo(config);

        Assert.False(string.IsNullOrWhiteSpace(info.Initials));
        Assert.True(info.Initials.Length <= 2);
    }

    [Fact]
    public void Host_info_falls_back_to_the_default_port_when_none_is_configured()
    {
        var info = new HostInfo(new ConfigurationBuilder().Build());

        Assert.Equal(LoopbackUrl.DefaultPort, info.Port);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter UserCardTests`
Expected: FAIL — compile error, `HostInfo` and `UserCard` not found.

- [ ] **Step 3: Write `HostInfo.cs`**

```csharp
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace SqlAgent.Host.Web;

/// <summary>
/// Facts about this host that the UI reports but cannot change: the OS account it runs as, where the
/// store lives, and where it is listening. Everything here is host configuration, owned by
/// appsettings.json and the runbook, so the About dialog and the Settings page read it rather than
/// offering a form that would need its own validation and restart story.
/// </summary>
public sealed class HostInfo(IConfiguration configuration)
{
    public string AccountName { get; } = Environment.UserName;

    public string MachineName { get; } = Environment.MachineName;

    /// <summary>Up to two letters for the avatar. Falls back to "?" so an empty account name (possible
    /// in some service contexts) cannot render a blank circle.</summary>
    public string Initials { get; } = Initialize(Environment.UserName);

    public string Version { get; } =
        typeof(HostInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HostInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Directory holding the SQLite store, resolved the same way LaunchUrlFile resolves it.</summary>
    public string StoreDirectory { get; } = LaunchUrlFile.ResolveDirectory(configuration);

    public int Port { get; } = ResolvePort(configuration);

    public string BindUrl { get; } = LoopbackUrl.Resolve(configuration);

    private static string Initialize(string account)
    {
        // Split on the separators real account names use — "ada.lovelace", "ada_lovelace",
        // "DOMAIN\ada" — then take the first letter of the first two parts.
        var parts = account.Split(['.', '_', '-', ' ', '\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var letters = parts.Take(2).Select(p => char.ToUpperInvariant(p[0]));
        return string.Concat(letters);
    }

    private static int ResolvePort(IConfiguration configuration) =>
        int.TryParse(configuration["SqlAgent:Web:Port"], out var port) && port is > 0 and < 65536
            ? port
            : LoopbackUrl.DefaultPort;
}
```

Both helpers this depends on already exist with the signatures used above, verified at plan time:
`LoopbackUrl.DefaultPort` is a `public const int` (`LoopbackUrl.cs:11`) and
`LaunchUrlFile.ResolveDirectory(IConfiguration)` is `public static`
(`LaunchUrlFile.cs:29`). Nothing needs adding — do not duplicate the `5099` literal here.

- [ ] **Step 4: Write `UserCard.razor`**

```razor
@inject HostInfo Info

<Menu Placement="MenuPlacement.Top">
    <Trigger>
        <div class="user-card-trigger">
            <span class="avatar" aria-hidden="true">@Info.Initials</span>
            <span class="user-text">
                <span class="user-name truncate">@Info.AccountName</span>
                <span class="user-host truncate">@Info.MachineName</span>
            </span>
            <Icon Name="chevron-down" Size="16" />
        </div>
    </Trigger>
    <ChildContent>
        @* No Sign out: the only session concept is the cookie the launch token is exchanged for, and
           there is no user record to sign out of. An item that looked like one would misrepresent the
           security model. *@
        <MenuItem Icon="settings" OnClick="GoToSettings">Settings</MenuItem>
        <MenuItem Icon="sun">
            Theme
            <Trailing><ThemeToggle /></Trailing>
        </MenuItem>
        <MenuItem Icon="info" OnClick="ShowAbout">About</MenuItem>
    </ChildContent>
</Menu>

@if (_aboutOpen)
{
    <Modal Title="About SQL Agent" OnClose="() => _aboutOpen = false">
        <dl class="about">
            <dt>Version</dt><dd>@Info.Version</dd>
            <dt>Listening on</dt><dd>@Info.BindUrl (port @Info.Port)</dd>
            <dt>Store</dt><dd class="mono">@Info.StoreDirectory</dd>
            <dt>Account</dt><dd>@Info.AccountName on @Info.MachineName</dd>
        </dl>
    </Modal>
}

@code {
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private bool _aboutOpen;

    private void GoToSettings() => Nav.NavigateTo("/settings");

    private void ShowAbout() => _aboutOpen = true;
}
```

Create `src/SqlAgent.Host/Components/Layout/UserCard.razor.css`:

```css
.user-card-trigger {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2);
  border: 1px solid var(--base-100);
  border-radius: var(--radius-control);
  background: var(--background-soft-50);
  color: var(--text-100);
}
.user-card-trigger:hover { background: var(--background-soft-100); }
.avatar {
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  flex: 0 0 32px;
  border-radius: var(--radius-round);
  background: var(--primary-500);
  color: var(--primary-text);
  font-size: var(--text-xs);
  font-weight: 600;
}
.user-text { display: flex; flex-direction: column; min-width: 0; flex: 1; }
.user-name { color: var(--title-50); font-weight: 500; }
.user-host { font-size: var(--text-xs); }

.about { display: grid; grid-template-columns: auto 1fr; gap: var(--space-2) var(--space-4); }
.about dt { color: var(--text-100); font-size: var(--text-xs); }
.about dd { color: var(--title-50); word-break: break-all; }
```

- [ ] **Step 5: Fill the sidebar's user slot**

Modify `src/SqlAgent.Host/Components/Layout/Sidebar.razor` — replace the footer block:

```razor
    <div class="sidebar-foot">
        <UserCard />
    </div>
```

and delete the now-unused `UserSlot` parameter and its doc comment from the `@code` block.

- [ ] **Step 6: Register `HostInfo`**

Modify `src/SqlAgent.Host/Program.cs` — beside the existing `LaunchToken` registration (line 48):

```csharp
builder.Services.AddSingleton<LaunchToken>();
builder.Services.AddSingleton<HostInfo>();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter "UserCardTests|ShellTests"`
Expected: PASS. `ShellTests` needs `HostInfo` registered in its own fixture now that `Sidebar`
renders `UserCard` — add to `ShellTests`' constructor:

```csharp
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new ConfigurationBuilder().Build());
        _ctx.Services.AddSingleton<HostInfo>();
        _ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
```

and drop the now-obsolete `UserSlot` reference if the test set one.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Host/Web/HostInfo.cs src/SqlAgent.Host/Components/Layout \
        src/SqlAgent.Host/Program.cs tests/SqlAgent.Tests/UserCardTests.cs \
        tests/SqlAgent.Tests/ShellTests.cs
git commit -m "$(cat <<'EOF'
Add the sidebar user card, backed by a HostInfo service

The reference interface's user card carries a name, an email, and Sign out. This
host has no user model and no login — it is loopback-only and single-user,
authenticated by a launch token — so the card shows the OS account and machine,
and the menu is Settings, Theme, and About. There is deliberately no Sign out
item: nothing exists to sign out of, and an item that looked like one would
misrepresent the security model.

HostInfo centralizes the facts the UI reports but cannot change (version, store
directory, bind URL, port), read from configuration rather than offered as a
form that would need its own validation and restart story.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Settings page and the LLM-configured signal

**Files:**
- Modify: `src/SqlAgent.Core/Llm/LlmGateway.cs` (add `IsConfigured`)
- Modify: `src/SqlAgent.Host/Program.cs:118-126` (`UnavailableLlmSqlGateway` overrides it)
- Create: `src/SqlAgent.Host/Components/Pages/Settings.razor` + `.razor.css`
- Test: `tests/SqlAgent.Tests/SettingsPageTests.cs`

**Interfaces:**
- Consumes: `HostInfo`, `ThemeToggle`, `Badge`, `ILlmSqlGateway`.
- Produces:
  - `ILlmSqlGateway.IsConfigured` — `bool`, default interface member returning `true`; the placeholder gateway overrides it to `false`.
  - Route `/settings`.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/SettingsPageTests.cs`:

```csharp
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlAgent.Core;
using SqlAgent.Host.Components.Pages;
using SqlAgent.Host.Web;

namespace SqlAgent.Tests;

public class SettingsPageTests
{
    /// <summary>A gateway that claims to be configured, standing in for a real provider once one is
    /// wired. Its GenerateSqlAsync is never called by these tests — only IsConfigured is under test.</summary>
    private sealed class ConfiguredGatewayStub : ILlmSqlGateway
    {
        public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException("not exercised by these tests");
    }

    /// <summary>Mirrors the host's real placeholder: no provider is wired, so IsConfigured is false.</summary>
    private sealed class UnconfiguredGatewayStub : ILlmSqlGateway
    {
        public bool IsConfigured => false;

        public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("No LLM provider is configured on this server.");
    }

    private static Bunit.TestContext NewContext(ILlmSqlGateway gateway)
    {
        var ctx = new Bunit.TestContext();
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SqlAgent:Web:Port"] = "5150" })
            .Build());
        ctx.Services.AddSingleton<HostInfo>();
        ctx.Services.AddSingleton(gateway);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("sqlAgentUi.getTheme").SetResult("system");
        return ctx;
    }

    [Fact]
    public void An_unconfigured_provider_is_reported_plainly_with_a_pointer_to_the_runbook()
    {
        // The composer's model selector links here. Saying "not configured" and naming the runbook is
        // the honest version; a page that looked configurable would send the user hunting for a form
        // that does not exist.
        using var ctx = NewContext(new UnconfiguredGatewayStub());

        var page = ctx.RenderComponent<Settings>();

        Assert.Contains("No model configured", page.Markup);
        Assert.Contains("runbook", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_configured_provider_is_reported_as_configured()
    {
        using var ctx = NewContext(new ConfiguredGatewayStub());

        var page = ctx.RenderComponent<Settings>();

        Assert.DoesNotContain("No model configured", page.Markup);
    }

    [Fact]
    public void A_gateway_that_says_nothing_is_treated_as_configured()
    {
        // IsConfigured is a default interface member returning true, so a future real provider does not
        // have to remember to implement it to be usable.
        Assert.True(new ConfiguredGatewayStub().IsConfigured);
    }

    [Fact]
    public void The_environment_panel_reports_version_port_and_store_location()
    {
        using var ctx = NewContext(new UnconfiguredGatewayStub());

        var page = ctx.RenderComponent<Settings>();

        Assert.Contains("5150", page.Markup);
        Assert.Contains("Store", page.Markup);
        Assert.Contains("Version", page.Markup);
    }

    [Fact]
    public void The_theme_control_is_available_on_the_page_as_well_as_in_the_menu()
    {
        using var ctx = NewContext(new UnconfiguredGatewayStub());

        var page = ctx.RenderComponent<Settings>();

        Assert.Contains("System", page.Markup);
        Assert.Contains("Light", page.Markup);
        Assert.Contains("Dark", page.Markup);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter SettingsPageTests`
Expected: FAIL — compile error: `ILlmSqlGateway.IsConfigured` does not exist, `Settings` not found.

- [ ] **Step 3: Add `IsConfigured` to the gateway interface**

Modify `src/SqlAgent.Core/Llm/LlmGateway.cs`. Read the file first, then add to the `ILlmSqlGateway`
interface:

```csharp
    /// <summary>
    /// Whether a real provider is wired behind this gateway. A default member returning true means a
    /// future provider does not have to opt in to be usable; only the placeholder overrides it.
    ///
    /// This is a UI signal only — it exists so the Settings page and the composer's model selector can
    /// say "no model configured" instead of inviting a question that will certainly fail. It does NOT
    /// change the failure path: NlQueryService still keys llm_not_configured off the placeholder's
    /// NotSupportedException, so a gateway that lies here still fails closed.
    /// </summary>
    bool IsConfigured => true;
```

- [ ] **Step 4: Override it in the placeholder gateway**

Modify `src/SqlAgent.Host/Program.cs`'s `UnavailableLlmSqlGateway`:

```csharp
internal sealed class UnavailableLlmSqlGateway : ILlmSqlGateway
{
    /// <summary>No provider is wired in this build. Surfaces as "no model configured" in the UI; the
    /// hard failure below is what actually enforces it.</summary>
    public bool IsConfigured => false;

    public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("No LLM provider is configured on this server.");
}
```

- [ ] **Step 5: Write `Settings.razor`**

```razor
@page "/settings"
@inject HostInfo Info
@inject ILlmSqlGateway Llm

<h1>Settings</h1>

<section class="settings-panel">
    <div class="settings-head">
        <h2>Appearance</h2>
        <p class="muted">Stored in this browser only.</p>
    </div>
    <ThemeToggle ShowLabels="true" />
</section>

<section class="settings-panel">
    <div class="settings-head">
        <h2>Language model</h2>
        @if (Llm.IsConfigured)
        {
            <Badge Tone="BadgeTone.Success">Configured</Badge>
        }
        else
        {
            <Badge Tone="BadgeTone.Warning">No model configured</Badge>
        }
    </div>
    @if (!Llm.IsConfigured)
    {
        <p class="muted">
            Natural-language questions cannot be answered until a provider is wired on this server.
            Configuration is host-side — see <code>docs/runbook.md</code>.
        </p>
    }
</section>

<section class="settings-panel">
    <div class="settings-head">
        <h2>Environment</h2>
        <p class="muted">Set in <code>appsettings.json</code>; shown here for reference.</p>
    </div>
    <dl class="settings-facts">
        <dt>Version</dt><dd>@Info.Version</dd>
        <dt>Listening on</dt><dd>@Info.BindUrl (port @Info.Port)</dd>
        <dt>Store</dt><dd class="mono">@Info.StoreDirectory</dd>
        <dt>Account</dt><dd>@Info.AccountName on @Info.MachineName</dd>
    </dl>
</section>
```

Create `src/SqlAgent.Host/Components/Pages/Settings.razor.css`:

```css
.settings-panel {
  max-width: 640px;
  margin-top: var(--space-5);
  padding: var(--space-5);
  border: 1px solid var(--base-100);
  border-radius: var(--radius-card);
  background: var(--background-soft-50);
}
.settings-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  margin-bottom: var(--space-3);
}
.settings-head p { font-size: var(--text-xs); }
.settings-facts { display: grid; grid-template-columns: auto 1fr; gap: var(--space-2) var(--space-4); }
.settings-facts dt { color: var(--text-100); font-size: var(--text-xs); }
.settings-facts dd { color: var(--title-50); word-break: break-all; }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter SettingsPageTests`
Expected: PASS, 5 tests.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS. `NlQueryServiceTests` uses its own gateway stubs; they inherit the default
`IsConfigured => true` and need no change. If any stub is declared with an explicit interface
implementation list that now fails to compile, add nothing — the default member covers it.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Core/Llm/LlmGateway.cs src/SqlAgent.Host/Program.cs \
        src/SqlAgent.Host/Components/Pages/Settings.razor \
        src/SqlAgent.Host/Components/Pages/Settings.razor.css \
        tests/SqlAgent.Tests/SettingsPageTests.cs
git commit -m "$(cat <<'EOF'
Add a settings page and an IsConfigured signal on the LLM gateway

/settings reports appearance, language-model status, and environment. Only the
theme is editable: everything else is host configuration owned by
appsettings.json and the runbook, and a form here would need its own validation
and restart story.

ILlmSqlGateway gains IsConfigured as a default interface member returning true,
overridden to false by the placeholder gateway. It is a UI signal only — so the
page and, later, the composer's model selector can say "no model configured"
rather than inviting a question that will certainly fail. The failure path is
unchanged: NlQueryService still keys llm_not_configured off the placeholder's
NotSupportedException, so a gateway that lies here still fails closed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Restyle the existing screens

**Files:**
- Create: `src/SqlAgent.Host/Components/Pages/Connections.razor.css`
- Create: `src/SqlAgent.Host/Components/Pages/Workspace.razor.css`
- Create: `src/SqlAgent.Host/Components/Layout/SchemaRail.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/ResultGrid.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/ChatOutcome.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/OutcomeMessage.razor.css`
- Create: `src/SqlAgent.Host/Components/Shared/SqlEditor.razor.css`
- Modify: `src/SqlAgent.Host/Components/Pages/Workspace.razor:6-9` (tab markup only)
- Test: `tests/SqlAgent.Tests/RestyleRegressionTests.cs`

**Interfaces:**
- Consumes: tokens; the existing components' current class names (`tabs`, `rail`, `tree`, `label`,
  `meta`, `actions`, `grid-scroll`, `transcript`, `question`, `generated-sql`, `clarification`,
  `editor`, `outcome`, `outcome-code`, `hidden`).
- Produces: no new API. **No behavior, text, or element removal** — existing tests assert on rendered
  text and find buttons by their labels.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlAgent.Tests/RestyleRegressionTests.cs`:

```csharp
namespace SqlAgent.Tests;

/// <summary>
/// Phase A restyles the existing screens and must not change their behavior. The component tests for
/// those screens (ResultGridTests, SchemaRailTests, WorkspaceTests, ConnectionsPageTests) are the real
/// guard and must stay green. These tests pin the two things those cannot see: that every class name
/// the existing markup already uses actually has a rule somewhere, and that no restyle silently
/// dropped a stylesheet.
/// </summary>
public class RestyleRegressionTests
{
    private static readonly string[] ClassesUsedByExistingMarkup =
    [
        "tabs", "rail", "tree", "label", "meta", "actions", "grid-scroll",
        "transcript", "question", "generated-sql", "clarification", "editor",
        "outcome", "outcome-code",
    ];

    [Theory]
    [InlineData("src/SqlAgent.Host/Components/Pages/Connections.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Pages/Workspace.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Layout/SchemaRail.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/ResultGrid.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/ChatOutcome.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/OutcomeMessage.razor.css")]
    [InlineData("src/SqlAgent.Host/Components/Shared/SqlEditor.razor.css")]
    public void Every_restyled_component_has_a_stylesheet(string path)
    {
        var css = File.ReadAllText(RepoPaths.Find(path));
        Assert.NotEmpty(css.Trim());
    }

    [Fact]
    public void Every_class_the_existing_markup_uses_is_styled_somewhere()
    {
        // These class names were in the markup with no stylesheet at all, which is how the UI shipped
        // unstyled. Any one of them left unstyled is an unstyled region of a real screen.
        var sheets = new[]
        {
            "src/SqlAgent.Host/wwwroot/css/app.css",
            "src/SqlAgent.Host/Components/Pages/Connections.razor.css",
            "src/SqlAgent.Host/Components/Pages/Workspace.razor.css",
            "src/SqlAgent.Host/Components/Layout/SchemaRail.razor.css",
            "src/SqlAgent.Host/Components/Shared/ResultGrid.razor.css",
            "src/SqlAgent.Host/Components/Shared/ChatOutcome.razor.css",
            "src/SqlAgent.Host/Components/Shared/OutcomeMessage.razor.css",
            "src/SqlAgent.Host/Components/Shared/SqlEditor.razor.css",
        };
        var all = string.Concat(sheets.Select(s => File.ReadAllText(RepoPaths.Find(s))));

        var unstyled = ClassesUsedByExistingMarkup.Where(c => !all.Contains($".{c}", StringComparison.Ordinal)).ToList();

        Assert.Empty(unstyled);
    }

    [Fact]
    public void No_component_stylesheet_hard_codes_a_hex_color()
    {
        // Tokens are the only way both themes stay consistent. A literal hex in a component sheet is a
        // color that will be wrong in one of the two themes.
        var componentSheets = Directory
            .EnumerateFiles(
                Path.GetDirectoryName(RepoPaths.Find("src/SqlAgent.Host/Components/App.razor"))!,
                "*.razor.css",
                SearchOption.AllDirectories)
            .ToList();

        Assert.NotEmpty(componentSheets);
        var offenders = componentSheets
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(f), @"#[0-9a-fA-F]{3,8}\b"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter RestyleRegressionTests`
Expected: FAIL — `RepoPaths.Find` throws for each missing `.razor.css`.

- [ ] **Step 3: Write `SchemaRail.razor.css`**

```css
.rail { display: flex; flex-direction: column; gap: var(--space-3); }
.rail .label {
  display: block;
  color: var(--text-100);
  font-size: var(--text-xs);
  text-transform: uppercase;
  letter-spacing: .04em;
}
.rail select, .rail input[type=search] { width: 100%; }
.rail .meta { margin-top: calc(-1 * var(--space-2)); }

.tree { list-style: none; padding: 0; display: flex; flex-direction: column; gap: 1px; }
.tree li {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: 6px var(--space-2);
  border-radius: var(--radius-control);
  color: var(--text-50);
  font-size: var(--text-sm);
}
.tree li:hover { background: var(--background-soft-100); }
/* A hidden table stays listed — the rail is where it is restored — but must read as excluded. */
.tree li.hidden { color: var(--text-100); text-decoration: line-through; opacity: .7; }
.tree li span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tree input[type=checkbox] { accent-color: var(--primary-500); flex: 0 0 auto; }
```

- [ ] **Step 4: Write `Workspace.razor.css` and adjust the tab markup**

Create `src/SqlAgent.Host/Components/Pages/Workspace.razor.css`:

```css
.tabs {
  display: inline-flex;
  gap: 2px;
  padding: 2px;
  margin-bottom: var(--space-4);
  background: var(--background-soft-200);
  border: 1px solid var(--base-100);
  border-radius: var(--radius-round);
}
.tabs button {
  border: none;
  background: none;
  color: var(--text-100);
  padding: 6px var(--space-4);
  border-radius: var(--radius-round);
}
.tabs button:hover { background: none; color: var(--title-50); }
.tabs button.active { background: var(--background-100); color: var(--title-50); font-weight: 500; }

.transcript { display: flex; flex-direction: column; gap: var(--space-4); margin-bottom: var(--space-5); }
.question {
  align-self: flex-end;
  max-width: 70%;
  padding: var(--space-3) var(--space-4);
  border-radius: var(--radius-pill);
  background: var(--background-soft-100);
  color: var(--title-50);
}
```

Modify `src/SqlAgent.Host/Components/Pages/Workspace.razor` lines 6-9 — the `.tabs` rule above needs
the buttons to carry `type="button"` so they cannot submit an enclosing form, and the existing
`class="@(...)"` already supplies `active`. Change only the two buttons:

```razor
<div class="tabs">
    <button type="button" class="@(_tab == Tab.Sql ? "active" : "")" @onclick="() => _tab = Tab.Sql">SQL</button>
    <button type="button" class="@(_tab == Tab.Chat ? "active" : "")" @onclick="() => _tab = Tab.Chat">Chat</button>
</div>
```

The button text (`SQL`, `Chat`) is unchanged — `WorkspaceTests` finds them by label.

- [ ] **Step 5: Write `Connections.razor.css`**

Read `src/SqlAgent.Host/Components/Pages/Connections.razor` first and style only the class names and
elements it actually uses. Start from this, adding rules for any class the file uses that is not
covered by `app.css`:

```css
.connections-list { display: flex; flex-direction: column; gap: var(--space-2); margin-bottom: var(--space-5); }
form { display: flex; flex-direction: column; gap: var(--space-3); max-width: 520px; }
form label { display: flex; flex-direction: column; gap: 6px; }
fieldset { border: 1px solid var(--base-100); border-radius: var(--radius-card); padding: var(--space-4); }
legend { color: var(--title-50); font-weight: 600; padding: 0 var(--space-2); }
```

- [ ] **Step 6: Write the remaining component stylesheets**

`src/SqlAgent.Host/Components/Shared/ResultGrid.razor.css`:

```css
.grid-scroll { max-height: 60vh; overflow: auto; }
.grid-scroll table { font-size: var(--text-sm); }
.grid-scroll thead th {
  position: sticky;
  top: 0;
  background: var(--background-soft-50);
  z-index: 1;
}
.grid-scroll tbody tr:hover { background: var(--background-soft-50); }
```

`src/SqlAgent.Host/Components/Shared/ChatOutcome.razor.css`:

```css
.generated-sql {
  margin: var(--space-2) 0;
  padding: var(--space-3) var(--space-4);
  border: 1px solid var(--base-100);
  border-radius: var(--radius-control);
  background: var(--background-soft-50);
  color: var(--title-50);
  overflow-x: auto;
  white-space: pre;
}
.clarification {
  padding: var(--space-3) var(--space-4);
  border-left: 3px solid var(--primary-500);
  background: var(--primary-50);
  color: var(--title-50);
  border-radius: var(--radius-control);
}
```

`src/SqlAgent.Host/Components/Shared/OutcomeMessage.razor.css`:

```css
.outcome { color: var(--text-50); }
.outcome p { margin: 0; }
.outcome-code { font-family: var(--font-mono); }
```

`src/SqlAgent.Host/Components/Shared/SqlEditor.razor.css`:

```css
/* CodeMirror builds its own DOM inside this host element, so its nodes are not scoped by CSS
   isolation and need ::deep to reach. */
.editor {
  border: 1px solid var(--base-200);
  border-radius: var(--radius-control);
  overflow: hidden;
}
.editor ::deep .CodeMirror {
  height: 220px;
  background: var(--input-background);
  color: var(--title-50);
  font-family: var(--font-mono);
  font-size: var(--text-sm);
}
.editor ::deep .CodeMirror-gutters {
  background: var(--background-soft-100);
  border-right: 1px solid var(--base-100);
}
.editor ::deep .CodeMirror-cursor { border-left-color: var(--title-50); }
.editor ::deep .CodeMirror-selected { background: var(--primary-50); }
```

- [ ] **Step 7: Run the restyle tests, then the whole suite**

Run: `dotnet test tests/SqlAgent.Tests/SqlAgent.Tests.csproj --filter RestyleRegressionTests`
Expected: PASS, 9 tests.

Run: `dotnet test SqlAgent.slnx --configuration Release`
Expected: PASS — in particular `ResultGridTests`, `SchemaRailTests`, `WorkspaceTests`,
`WorkspaceChatTests`, and `ConnectionsPageTests`, which assert on markup text. If any fails, the
restyle changed markup it should not have: revert that markup change rather than editing the test.

- [ ] **Step 8: Commit**

```bash
git add src/SqlAgent.Host/Components src/SqlAgent.Host/wwwroot \
        tests/SqlAgent.Tests/RestyleRegressionTests.cs
git commit -m "$(cat <<'EOF'
Style the existing screens against the design tokens

Connections, Workspace, the schema rail, the result grid, the chat outcome, the
outcome message, and the SQL editor all carried class names that no stylesheet
defined. Each gets a colocated .razor.css consuming tokens only.

Markup is untouched apart from adding type="button" to the two workspace tabs:
the existing component tests find controls by their labels, so behavior and text
had to stay exactly as they were. A test asserts no component stylesheet
hard-codes a hex color, since a literal would be wrong in one of the two themes.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: Documentation and phase verification

**Files:**
- Modify: `docs/web-ui.md` (shell section, theme section, manual checklist)
- Modify: `README.md` (screen list)

**Interfaces:**
- Consumes: everything above.
- Produces: no code.

- [ ] **Step 1: Document the shell and themes in `docs/web-ui.md`**

Replace the "The three screens" section heading with "The screens" and insert this before it:

```markdown
## The shell

The UI is a sidebar plus an inset main card. The sidebar carries the product mark, a collapse
toggle, the nav rows, the schema rail, and the user card; the card holds the current page.

- **Collapse** shrinks the sidebar to an icon rail. Below 1024px it leaves the layout entirely and
  becomes an overlay drawer opened from the hamburger at the top left.
- **The user card** shows the OS account and machine name. There is deliberately **no Sign out**:
  the host is single-user and loopback-only, the only session concept is the `sqlagent_session`
  cookie the launch token is exchanged for, and no user record exists to sign out of. Its menu
  offers Settings, Theme, and About.

## Themes

Three settings — system, light, dark — chosen from the segmented control in the user menu or on
`/settings`.

The choice lives in `localStorage` (`sqlagent.theme`), not in the SQLite store: it is a per-browser
preference, and a server round trip would paint the wrong theme first. `wwwroot/js/theme.js` is
loaded **synchronously from `<head>`** and applies the stored value to `<html>` before Blazor
connects; moving it to `<body>`, or adding `defer`, reintroduces a flash. `system` sets no class at
all, and `app.css` keys the OS preference off the absence of both classes so an explicit choice
always wins over the OS.

Colors are CSS custom properties in `wwwroot/css/app.css`; components consume `var(--token)` and
never a literal color. The dark palette is written twice — once for `:root.dark`, once inside
`@media (prefers-color-scheme: dark)` for the system setting — and
`DesignSystemTests.Every_token_redefined_for_dark_mode_is_also_redefined_for_the_system_preference`
pins the two blocks to the same property set so they cannot drift.

The sidebar's collapsed state is stored and applied the same way (`sqlagent.sidebar`, `html.sidebar-collapsed`).
```

- [ ] **Step 2: Extend the manual regression checklist**

In `docs/web-ui.md`, add these rows to the existing checklist table:

```markdown
| Set theme to Dark, reload | Page is dark on first paint — no white flash |
| Set theme to System, switch the OS between light and dark | Page follows the OS without a reload |
| Set theme to Light on a dark-mode OS | Page stays light — the explicit choice wins |
| Collapse the sidebar, reload | Sidebar renders collapsed on first paint, not wide-then-narrow |
| Narrow the window below 1024px | Sidebar becomes a drawer; the hamburger opens it; the scrim closes it |
| Open the user menu, adjust the theme from its row | Theme changes and the menu stays open |
| Open About from the user menu | Version, bind URL, port, and store path are correct |
| Tab through the sidebar and the composer | Focus ring is visible on every control |
| Load the UI with `wwwroot/fonts/DMSans-Variable.woff2` removed | Text renders in the system sans-serif, not a serif |
```

- [ ] **Step 3: Update the README screen list**

In `README.md`, replace the "Details on the three screens" phrasing with "Details on the shell, the
screens, the token" and leave the rest of the sentence intact.

- [ ] **Step 4: Full verification**

```bash
dotnet build SqlAgent.slnx --configuration Release
dotnet test SqlAgent.slnx --configuration Release
```

Expected: build clean, all tests pass. Then run the app and walk the new checklist rows by hand:

```bash
dotnet run --project src/SqlAgent.Host/SqlAgent.Host.csproj
```

Open the URL from `launch-url.txt`. Confirm: the shell renders styled, both themes work with no
flash, the sidebar collapses and persists, the drawer works under 1024px, About reports the right
port and store path, `/settings` renders, and Connections + Workspace + the rail all still function.

- [ ] **Step 5: Commit**

```bash
git add docs/web-ui.md README.md
git commit -m "$(cat <<'EOF'
Document the shell, the theme model, and the new manual checks

Records why theme.js is a synchronous <head> script (a deferred one flashes),
why the theme lives in localStorage rather than the store, why the dark palette
is written twice and what pins the copies together, and why the user card has no
Sign out. Adds the nine manual checks bUnit cannot reach: theme application on
first paint, OS following, collapse persistence, the drawer, and the missing-font
fallback.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase A Definition of Done

- [ ] `dotnet build SqlAgent.slnx --configuration Release` is clean.
- [ ] `dotnet test SqlAgent.slnx --configuration Release` is green, including every pre-existing test.
- [ ] Every screen that worked before Phase A still works, at the same route, with the same behavior.
- [ ] Light, dark, and system themes all render correctly, and the stored choice applies before first paint.
- [ ] The sidebar collapses, persists, and becomes a drawer below 1024px.
- [ ] `/settings` reports appearance, language-model status, and environment.
- [ ] No component stylesheet contains a literal color.
- [ ] `docs/web-ui.md` documents the shell and themes and lists the new manual checks.

## Self-Review Notes

Checked against the spec's Phase A section:

| Spec requirement | Task |
|---|---|
| Token block, light + dark, verbatim values | 1 |
| System theme via `prefers-color-scheme`, explicit choice wins | 1 |
| Pre-paint theme application, no flash | 1 |
| DM Sans vendored, OFL, system fallback, mono stack | 1 |
| Type scale and radii | 1 |
| Button/input/badge/menu/alert token sets | 1 (`app.css`), 2 (`Badge`), 3 (`Menu`) |
| `ConfirmDialog` | deferred to Phase D, where the SQL blocks that call it are built |
| 300px sidebar, 20px padding, inset rounded card | 1 (`app.css`), 5 |
| Collapse to icon rail, persisted | 5 |
| Overlay drawer below 1024px, scrim, hamburger | 5 |
| Icons as inline SVG, no dependency | 2 |
| Provider glyphs | deferred to Phase C, where the database list that needs them is built |
| User card: OS account, machine, initials, no Sign out | 6 |
| User menu: Settings, Theme, About | 6 |
| Settings page: theme, provider status, environment | 7 |
| Reduced-motion respect | 1 |
| Existing screens restyled, not removed | 8 |
| Docs + manual checklist | 9 |

**Deferred on purpose, and where each lands.** Phase A ships only what Phase A renders, so nothing
sits in the codebase waiting for a caller a later phase might rename or never write. Each later
phase's plan must pick its items up:

| Deferred | Lands in | Why not now |
|---|---|---|
| Provider glyphs (`MS`, `PG` database marks) | C | Only the Databases sidebar section draws them. |
| `search`, `plus` icons | B | New Chat and Search arrive with the pages behind them. |
| `folder`, `chevron-right`, `more-vertical` icons | B | Projects, history rows, and their `⋮` menus. |
| `check`, `alert-triangle` icons | D | Access-level control and the DDL warning band. |
| `ConfirmDialog` | D | Its only caller is the SQL block's confirm-before-run gate. |

`UiPrimitiveTests.No_icon_ships_that_nothing_renders` enforces this for the icon set: adding a glyph
without a caller fails the build, so each phase adds its own.

**Type consistency:** `Icon.Names`, `BadgeTone`, `MenuPlacement`, `SegmentedOption`,
`HostInfo.{AccountName,MachineName,Initials,Version,StoreDirectory,BindUrl,Port}`,
`ILlmSqlGateway.IsConfigured`, and `window.sqlAgentUi.{getTheme,setTheme,getSidebar,setSidebar}` are
each defined once and referenced with the same names throughout.

---

## Carried forward from Phase A

Findings that Phase A's reviews raised and consciously did not fix. Each was
adjudicated at the final whole-branch review and ruled acceptable to carry. They
are recorded here rather than in a code comment because a defect that lives only
in a comment is a defect nobody is assigned.

| # | Item | Lands in | Why it was carried |
|---|---|---|---|
| 1 | **Mobile Modal-in-drawer.** Below 1024px `.sidebar` carries a CSS `transform`, so `position: fixed` descendants resolve against the drawer, not the viewport. The About dialog centres on the drawer, overhangs it, and rides off-screen when the drawer closes while still considering itself open. | B | Needs a portal; disproportionate to Phase A. Documented in `UserCard.razor` and `docs/web-ui.md`. |
| 2 | **`Modal.razor`'s `autofocus` may or may not work — verify, do not assume broken.** Phase A proved `autofocus` is skipped when the opening click leaves focus on a surviving trigger. Modal's shape differs: `MenuItem.Activate` closes the menu before invoking `OnClick`, removing the focused button, so `activeElement` reverts to `<body>` and the flush should fire. | B | Unproven either way; the scrim still closes the dialog. Note `UiInteractionTests.The_modal_close_button_autofocuses_…` is a green test asserting the mechanism — if the browser check goes the other way, that test needs rewriting too. |
| 3 | Below 1024px a **closed** drawer's contents stay in the tab order (hidden by `transform`, not `display:none`/`inert`), and focus is not restored to the hamburger on close. | B | Pre-existing shape — Tab already walked into the off-screen drawer before Phase A. |
| 4 | `SqlEditor.razor:56` catches only `JSDisconnectedException` — the one interop site Phase A's exception-filter sweep did not reach. Arguably right in a `DisposeAsync`, where a `JSException` is a real bug that should surface. | D | Deliberate asymmetry, but undocumented. Decide and write down which. |
| 5 | `ResultGrid.razor:56` calls `sqlAgentDownload` with **no try/catch**, inside a click handler. | D | Pre-existing; `DataTable` supersedes `ResultGrid` in D anyway. |
| 6 | The environment `<dl>` is rendered twice — About dialog and `/settings` — from the same `HostInfo`, with byte-identical CSS under two class names. | E | E adds a storage-provider row to Settings; extract a shared `HostFacts.razor` before the two disagree. |
| 7 | **The collapsed sidebar is styled in two places** — `app.css` keyed on `html.sidebar-collapsed` (pre-paint) and `Sidebar.razor.css` keyed on the circuit-added class. They agree today by hand, with nothing enforcing it; this split caused two Phase A defects. `app.css`'s `.nav-label` / `.brand-name` selectors are **not scoped under the sidebar**. | B | B adds `HistorySection`, `ProjectSection` and a Search row. Either delete the duplicate set (theme works this way — one source of truth, no defects) or add a parity test mirroring `DesignSystemTests`'s dark-block check, and scope the `app.css` selectors under `.app aside.sidebar`. |
| 8 | Safari does not focus a `<button>` on plain mouse click (a macOS convention), so Escape does not close the user menu there via mouse. Tabbing works. | B | No interop-free fix. Revisit when B's `Ctrl`/`Cmd`+`K` modal makes a document-level key listener worth its cost. |
| 9 | `Spinner` and `EmptyState` ship rendered by nothing, contradicting this plan's own "Phase A ships only what Phase A renders" amendment. The enforcing test covers glyphs but not components. | B uses both | Harmless, but the rule should cover components too, or be restated. |
| 10 | `Ui/Toggle` is in the spec's file layout, never shipped, never entered in the deferral table. | C | C needs a read-only toggle and a master DDL toggle. Silent drop, recorded so C plans for it. |
| 11 | `RestyleRegressionTests.Every_class_the_existing_markup_uses_is_styled_somewhere` concatenates all sheets before searching, so **a rule in the wrong sheet still passes** — precisely the failure that hit Phase A twice. | any | Testing scope, not just presence, needs the compiled `obj/**/scopedcss/**` output. |
| 12 | `UiPrimitiveTests.No_icon_ships_that_nothing_renders` compares `Icon.Names` against a hardcoded array and never inspects markup — a change-detector wearing a policy's name. | any | Rename it to what it does, or scan `*.razor` for `Name="…"` and make it real. |
| 13 | **`EventCallback` trap.** `@onclick="@(cond ? Handler : null)"` does **not** detach a handler: Razor compiles it to `EventCallback.Factory.Create(this, expr)`, and with a null delegate the component is still the receiver, so `RequiresExplicitReceiver` is true and the attribute renders anyway. Use `default(EventCallback<T>)`. | any | Generalizes across the codebase. `Sidebar.DrawerKeyHandler` documents it; the aside is the only conditional attachment today. |

### Two lessons for B–E

**bUnit cannot see what broke most often here.** It renders markup but runs no
browser, no CSS engine, no HTML parser, no focus model and no circuit. Every
visual and behavioral defect this phase produced was invisible to it: focus
reachability, the parser restructuring nested buttons, scoped CSS failing to
reach a child component's markup, clipping, and a dead circuit. Where correctness
depends on any of those, assert on rendered DOM structure or on stylesheet source
text and say so in the test comment — or drive a real browser.

**Explanatory prose was wrong more often than the code it explained.** Five
load-bearing comments in this phase stated something false: an exception
inheritance relationship, what an `overflow: hidden` was clipping, what a guard
prevented, and two documentation claims about MCP tools and configuration keys.
Each was believed until someone checked it against the source. Treat a comment
that justifies a design decision as a claim to verify, not as context.
