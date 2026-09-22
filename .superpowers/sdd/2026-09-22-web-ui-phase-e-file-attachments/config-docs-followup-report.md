# Configuration docs follow-up

Updated `docs/runbook.md` and `docs/web-ui.md` to describe host resolution of
`SqlAgent:Files:Provider` and `SqlAgent:Files:MaxBytes`, including documented defaults.
The docs still identify `local-disk` as the only registered provider and retain the
10-attachment message cap.

Verified against `src/SqlAgent.Host/Program.cs`, which resolves `FileStorageOptions`
from configuration before registering the local-disk provider.
