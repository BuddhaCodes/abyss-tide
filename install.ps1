<#
  Instala Abyss Tide para el usuario actual (no requiere administrador).

    .\install.ps1               instala, añade los perfiles a Windows Terminal y lo arranca
    .\install.ps1 -NoAutostart  igual, pero sin arrancar con Windows
    .\install.ps1 -Uninstall    lo detiene y lo quita todo

  Si PowerShell bloquea el script:
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param([switch]$Uninstall, [switch]$NoAutostart)

$ErrorActionPreference = 'Stop'
$here     = $PSScriptRoot
$appDir   = Join-Path $env:LOCALAPPDATA 'AbyssTide'
$fragDir  = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows Terminal\Fragments\AbyssTide'
$startLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'Abyss Tide.lnk'
$psExe    = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$runArgs  = "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$appDir\abyss-tide.ps1`""

function Stop-AbyssTide {
    Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" |
        Where-Object { $_.CommandLine -like '*abyss-tide.ps1*' -and $_.ProcessId -ne $PID } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

if ($Uninstall) {
    Stop-AbyssTide
    Remove-Item $startLnk -Force -ErrorAction SilentlyContinue
    Remove-Item $fragDir  -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $appDir   -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Abyss Tide desinstalado. Reinicia Windows Terminal para que desaparezcan los perfiles.'
    return
}

Stop-AbyssTide
New-Item -ItemType Directory -Force -Path $appDir, $fragDir | Out-Null

Copy-Item (Join-Path $here 'AbyssTide.cs')     $appDir -Force
Copy-Item (Join-Path $here 'abyss-tide.ps1')   $appDir -Force
Copy-Item (Join-Path $here 'abyss-tide.hlsl')  $appDir -Force
$cfgDst = Join-Path $appDir 'abyss-tide.config.json'
if (-not (Test-Path $cfgDst)) { Copy-Item (Join-Path $here 'abyss-tide.config.json') $cfgDst }   # no pisar tus ajustes

# Fragmento de Windows Terminal: esquema "Abyss Tide" + dos perfiles nuevos
Copy-Item (Join-Path $here 'abyss-tide.hlsl') $fragDir -Force
$shader = (Join-Path $fragDir 'abyss-tide.hlsl') -replace '\\', '\\'
$frag = [IO.File]::ReadAllText((Join-Path $here 'abyss-tide.fragment.json')).Replace('__SHADER_PATH__', $shader)
[IO.File]::WriteAllText((Join-Path $fragDir 'abyss-tide.json'), $frag, (New-Object Text.UTF8Encoding $false))

if (-not $NoAutostart) {
    $sh = New-Object -ComObject WScript.Shell
    $lnk = $sh.CreateShortcut($startLnk)
    $lnk.TargetPath = $psExe
    $lnk.Arguments = $runArgs
    $lnk.WorkingDirectory = $appDir
    $lnk.WindowStyle = 7
    $lnk.Description = 'Abyss Tide — agua ASCII para Windows Terminal'
    $lnk.Save()
}

Start-Process -FilePath $psExe -ArgumentList $runArgs -WindowStyle Hidden

Write-Host ''
Write-Host 'Abyss Tide instalado.' -ForegroundColor Cyan
Write-Host '  1. Cierra y vuelve a abrir Windows Terminal.'
Write-Host '  2. Abre el perfil "PowerShell · Abyss Tide" (o ponlo como predeterminado en Configuración).'
Write-Host '  3. Arrastra la ventana y suéltala de golpe. El icono de la bandeja permite pausar o salir.'
Write-Host "  Ajustes: $cfgDst"
