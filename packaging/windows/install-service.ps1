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

Write-Host "SQL Agent UI will listen on http://127.0.0.1:$Port — the launch token is written to the service log."
