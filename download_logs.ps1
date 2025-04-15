# === CONFIG ===
$VpsUser = "root"
$VpsHost = "147.93.44.247"
$RemotePath = "/opt/operator_portal_api_server/publish/logs/*.txt"
$LocalPath = "$env:USERPROFILE\Downloads\portalrest_logs"


# === SETUP ===
if (!(Test-Path $LocalPath)) {
    New-Item -ItemType Directory -Path $LocalPath | Out-Null
}

# === ACTION ===
Write-Host "Downloading logs from $VpsUser@$VpsHost..."

scp "${VpsUser}@${VpsHost}:$RemotePath" "$LocalPath\"

if ($LASTEXITCODE -eq 0) {
    Write-Host "Log files downloaded to $LocalPath"
} else {
    Write-Host "Failed to download log files."
}
