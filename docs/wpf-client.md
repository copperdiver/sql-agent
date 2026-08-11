# WPF configuration & chat client (CD-50 T9)

The `SqlAgent.Client.Wpf` app is the Windows desktop front end. It is MVVM, has **no** database
drivers, and talks to the Core host **only** over the local named pipe (`SqlAgent.LocalApi`) using the
shared DTOs from `SqlAgent.Api.Local.Contracts`.

## Architecture

```
MainWindow.xaml ──► MainViewModel
                      ├─ ConnectionsViewModel     (list / add / edit / delete / test / read-only)
                      ├─ TableVisibilityViewModel  (per-table visible toggle)
                      └─ ChatViewModel             (SQL transcript, result grid, copy SQL, voice)
                              │
                      Services/LocalApiClient ──(named pipe, newline-delimited JSON)──► Core host
                      Services/IVoiceInputService  (NullVoiceInputService by default — replaceable)
```

Voice input is behind `IVoiceInputService`; the shipped `NullVoiceInputService` reports
`IsSupported = false`, so the mic button is hidden until a real engine is wired in `App.OnStartup`.

## Running end-to-end

1. Start the host so the pipe is served: `dotnet run --project src/SqlAgent.Host`.
   (The host now hosts `NamedPipeApiServer`; previously it was a no-op loop.)
2. Launch the client: `dotnet run --project src/SqlAgent.Client.Wpf`.

If the host is **not** running, the client still opens; every call simply reports
`Error (connection_failed): The SQL Agent host is not running` on the status line — no crash.

If the host has a local-access token configured (`SqlAgent__LocalAuth__Token`, off by default),
export `SQLAGENT_AUTH_TOKEN` with the same value before launching the client — `App.OnStartup`
reads it and passes it to `LocalApiClient`. Without it every call reports `unauthorized`.

## Manual test checklist

| Area | Steps | Expected |
|------|-------|----------|
| Startup, no host | Launch client with host stopped | Window opens; connections status shows `connection_failed`, app stays responsive |
| Add connection | Connections tab → New → fill Name/Provider/connection string → Save | Row appears in the list; secret never shown back |
| Edit | Select a row, change Name / Read-only → Save | List updates; leaving connection string blank keeps the saved secret |
| Test | Select a row → Test (or fill a draft → Test) | Status shows `Connection OK (<version>, <ms> ms)` or the failure reason |
| Read-only | Set Read-only, Save, then run an `UPDATE` in Chat | Chat shows `policy_denied_readonly` error message |
| Delete | Select a row → Delete | Row disappears; form resets to New |
| Table visibility | Select a connection → Tables tab | Every live table listed; toggling a checkbox persists (status confirms hidden/visible) |
| Visibility effect | Hide a table, then `SELECT` from it in Chat | Query is denied by policy; hidden table absent from results |
| Chat query | Chat tab → type `SELECT ...` → Send | User + agent messages appended; result grid fills; row count / ms shown |
| Truncation | Run a query over the row cap | Agent message notes `(results truncated)` |
| Copy SQL | After a query → Copy SQL | Executed SQL is on the clipboard |
| Error surfacing | Send invalid SQL | Red error message in the transcript; no unhandled exception |
| No UI freeze | Run a slow query | Buttons disable while running; window stays responsive (all calls are async) |

## Automated coverage

The new server-side ops backing the visibility tab (`list_table_policies`, `set_table_policy`) and the
`TablePolicyService` are covered in `tests/SqlAgent.Tests/LocalApiDispatcherTests.cs`. The client itself is
verified manually per the table above (it cannot be unit-tested without a Windows display / WPF reference,
which would break the Linux CI restore).
