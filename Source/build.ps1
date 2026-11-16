param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet build (Join-Path $here "SilentFogClient.csproj") -c Release -p:GameDir="$GameDir"
Copy-Item (Join-Path $here "bin\Release\netstandard2.1\SilentFogClient.dll") (Join-Path $here "..\SilentFogClient.dll") -Force
Write-Host "SilentFogClient.dll обновлён." -ForegroundColor Green
