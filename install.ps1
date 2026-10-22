<#
  Instala Abyss Tide para el usuario actual (no requiere administrador).

    .\install.ps1                instala, añade los perfiles a Windows Terminal y lo arranca
    .\install.ps1 -NoAutostart   sin arrancar con Windows
    .\install.ps1 -NoShellHook   sin tocar tu perfil de PowerShell (no habrá agua roja al fallar un comando)
    .\install.ps1 -Uninstall     lo detiene y lo quita todo, incluido el hook del perfil

  Si PowerShell bloquea el script:
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param([switch]$Uninstall, [switch]$NoAutostart, [switch]$NoShellHook)

$ErrorActionPreference = 'Stop'
$here     = $PSScriptRoot
$appDir   = Join-Path $env:LOCALAPPDATA 'AbyssTide'
$fragDir  = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows Terminal\Fragments\AbyssTide'
$startLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'Abyss Tide.lnk'
$psExe    = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$runArgs  = "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$appDir\abyss-tide.ps1`""
$docs     = [Environment]::GetFolderPath('MyDocuments')
$profiles = @((Join-Path $docs 'WindowsPowerShell\profile.ps1'), (Join-Path $docs 'PowerShell\profile.ps1'))
$hookRx   = '(?s)(\r?\n)?# >>> abyss-tide >>>.*?# <<< abyss-tide <<<(\r?\n)?'

# Hook del prompt: avisa a Abyss Tide si el último comando falló (agua roja) o salió bien (burbujas).
$hook = @'
# >>> abyss-tide >>>
# Avisa a Abyss Tide (agua de Windows Terminal) si el ultimo comando fallo. Lo quita: install.ps1 -Uninstall
if (-not (Test-Path variable:global:__AbyssTidePrompt)) {
    $global:__AbyssTidePrompt = $function:prompt
    function global:prompt {
        $abyssOk = $?
        try {
            $h = Get-History -Count 1
            if ($h -and $h.Id -ne $global:__AbyssTideLastId) {
                $global:__AbyssTideLastId = $h.Id
                $state = 'ok'; if (-not $abyssOk) { $state = 'error' }
                [IO.File]::WriteAllText((Join-Path $env:LOCALAPPDATA 'AbyssTide\signal.txt'), $state)
            }
        } catch { }
        if (-not $abyssOk) { Write-Error '' -ErrorAction Ignore }   # conserva $? para tu prompt original
        & $global:__AbyssTidePrompt
    }
}
# <<< abyss-tide <<<
'@

function Stop-AbyssTide {
    Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" |
        Where-Object { $_.CommandLine -like '*abyss-tide.ps1*' -and $_.ProcessId -ne $PID } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
}

function Remove-ShellHook {
    foreach ($p in $profiles) {
        if (-not (Test-Path $p)) { continue }
        $text = [IO.File]::ReadAllText($p)
        if ($text -match '# >>> abyss-tide >>>') {
            [IO.File]::WriteAllText($p, ($text -replace $hookRx, ''), (New-Object Text.UTF8Encoding $true))
        }
    }
}

function Add-ShellHook {
    Remove-ShellHook
    foreach ($p in $profiles) {
        New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null
        $text = ''
        if (Test-Path $p) { $text = [IO.File]::ReadAllText($p) }
        if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { $text += "`r`n" }
        $text += "`r`n" + ($hook -replace "`r?`n", "`r`n") + "`r`n"
        [IO.File]::WriteAllText($p, $text, (New-Object Text.UTF8Encoding $true))
    }
}

if ($Uninstall) {
    Stop-AbyssTide
    Remove-ShellHook
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
else { Write-Host "Se conserva tu configuración actual: $cfgDst (las opciones nuevas usan su valor por defecto)" }

# Fragmento de Windows Terminal: esquema "Abyss Tide" + dos perfiles nuevos
Copy-Item (Join-Path $here 'abyss-tide.hlsl') $fragDir -Force
$shader = (Join-Path $fragDir 'abyss-tide.hlsl') -replace '\\', '\\'
$frag = [IO.File]::ReadAllText((Join-Path $here 'abyss-tide.fragment.json')).Replace('__SHADER_PATH__', $shader)
[IO.File]::WriteAllText((Join-Path $fragDir 'abyss-tide.json'), $frag, (New-Object Text.UTF8Encoding $false))

if (-not $NoShellHook) { Add-ShellHook }

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
Write-Host '  3. Arrastra la ventana y suéltala de golpe. Escribe, pasa el ratón por el agua, falla un comando.'
Write-Host '  El icono de la bandeja tiene paletas, sensibilidad, peces y más.'
if (-not $NoShellHook) { Write-Host "  Se añadió un hook al prompt en: $($profiles -join ', ')" }
Write-Host "  Ajustes: $cfgDst  (se aplican al guardar)"
