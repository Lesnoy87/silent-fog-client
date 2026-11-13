param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet build (Join-Path $here "RustNightClient.csproj") -c Release -p:GameDir="$GameDir"
Copy-Item (Join-Path $here "bin\Release\netstandard2.1\RustNightClient.dll") (Join-Path $here "..\RustNightClient.dll") -Force
Write-Host "RustNightClient.dll обновлён." -ForegroundColor Green
