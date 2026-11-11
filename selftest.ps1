# Instala o Smart Tooling com o autoteste, abre o Stationeers em um mundo novo descartavel, espera o
# resultado no log do BepInEx e reinstala a versao normal (sem o codigo de teste).
# Uso:  powershell -ExecutionPolicy Bypass -File SmartTooling\selftest.ps1
param(
    [string]$GameDir = "G:\SteamLibrary\steamapps\common\Stationeers",
    [int]$TimeoutSeconds = 280
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$deploy = Join-Path $here "deploy.ps1"
$docs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "My Games\Stationeers"
$modDll = Join-Path $docs "mods\SmartTooling\SmartTooling.dll"
$log = Join-Path $GameDir "BepInEx\LogOutput.log"
$flag = Join-Path ([IO.Path]::GetTempPath()) "smarttooling_selftest.flag"

if (Get-Process rocketstation -ErrorAction SilentlyContinue) { throw "O Stationeers esta aberto. Feche-o antes de rodar o autoteste." }

& $deploy -GameDir $GameDir -SelfTest | Out-Null
$savesBefore = (Get-ChildItem (Join-Path $docs "saves") -Directory).Name
Set-Content $flag "1"
$before = (Get-Item $log).LastWriteTime
$done = $false
try {
    Start-Process (Join-Path $GameDir "rocketstation.exe") -WorkingDirectory $GameDir
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 6
        if ((Get-Item $log).LastWriteTime -gt $before) {
            $content = Get-Content $log -Raw -ErrorAction SilentlyContinue
            if ($content -match "SELFTEST DONE") { $done = $true; Start-Sleep -Seconds 8; break }
        }
    }
}
finally {
    Remove-Item $flag -Force -ErrorAction SilentlyContinue
}

Get-Content $log | Where-Object { $_ -match "SELFTEST|\[Error|\[Warning.*SmartTooling|SmartTooling .* loaded|^\s+at " } |
    Where-Object { $_ -notmatch "SELFTEST status" }

if (-not $done) {
    Write-Output "AUTOTESTE NAO TERMINOU em $TimeoutSeconds s. Jogo ainda aberto: $([bool](Get-Process rocketstation -ErrorAction SilentlyContinue))"
}

# Reinstala a versao normal.
& $deploy -GameDir $GameDir | Out-Null
$bytes = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($modDll))
$savesNew = @((Get-ChildItem (Join-Path $docs "saves") -Directory).Name | Where-Object { $savesBefore -notcontains $_ })
Write-Output "Versao normal reinstalada. Contem SelfTest: $($bytes.Contains('SelfTest')). Pastas de save novas: [$($savesNew -join ', ')]. Jogo aberto: $([bool](Get-Process rocketstation -ErrorAction SilentlyContinue))"
