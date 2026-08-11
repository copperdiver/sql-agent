param(
    [string]$PublishPath = "C:\Program Files\SqlAgent",
    [string]$ServiceName = "SqlAgent",
    [string]$DisplayName = "SQL Agent",
    [int]$Port = 5099
)

$exe = Join-Path $PublishPath "SqlAgent.Host.exe"
if (-not (Test-Path $exe)) {
    throw "Host executable not found at $exe. Publish the host before installing the service."
}

New-Service `
    -Name $ServiceName `
    -DisplayName $DisplayName `
    -BinaryPathName "`"$exe`"" `
    -StartupType Automatic

# New-Service has no environment switch; the SCM reads this multi-string value when it starts the service.
Set-ItemProperty `
    -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name Environment `
    -Type MultiString `
    -Value @("SqlAgent__Web__Port=$Port")

Start-Service -Name $ServiceName

# The launch token is deliberately kept out of every log sink -- the Event Log this service would
# otherwise write to is readable by more principals than the service account, and the token is the
# only thing guarding a TCP port every local account can reach. It goes to launch-url.txt beside the
# SQLite store instead, ACL'd to the service account and BUILTIN\Administrators. The store path
# follows SqlAgent__Storage__ConnectionString; with none set the host uses sqlagent.db in its own
# working directory, which for this service is $PublishPath.
$launchUrlFile = Join-Path $PublishPath "launch-url.txt"
Write-Host "SQL Agent UI will listen on http://127.0.0.1:$Port"
Write-Host "Open the URL (token included) from: $launchUrlFile"
Write-Host "  Get-Content `"$launchUrlFile`"    # run elevated; the file is not readable by ordinary users"
Write-Host "If that path is not writable by the service account, set SqlAgent__LocalAuth__Token in the"
Write-Host "service Environment value above to a token of your own instead. See docs/runbook.md."
