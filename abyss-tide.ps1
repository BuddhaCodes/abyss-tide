<#
  Abyss Tide — arranca el agua ASCII que vive detrás de Windows Terminal.
  La primera vez compila AbyssTide.cs con el compilador que ya trae Windows y guarda la DLL;
  las siguientes arranca al instante. Deja un icono en la bandeja (clic derecho → menú).

  Uso manual:  powershell -NoProfile -STA -ExecutionPolicy Bypass -File .\abyss-tide.ps1
#>
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'abyss-tide.config.json')
)

$ErrorActionPreference = 'Stop'

$srcPath = Join-Path $PSScriptRoot 'AbyssTide.cs'
$refs    = 'System.Windows.Forms', 'System.Drawing'
$cache   = Join-Path $env:LOCALAPPDATA 'AbyssTide\cache'
$hash    = (Get-FileHash -Algorithm SHA256 -Path $srcPath).Hash.Substring(0, 16)
$dll     = Join-Path $cache ("AbyssTide-{0}-ps{1}.dll" -f $hash, $PSVersionTable.PSVersion.Major)

if (-not (Test-Path $dll)) {
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    Get-ChildItem $cache -Filter 'AbyssTide-*.dll' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue        # versiones viejas
    try {
        Add-Type -TypeDefinition ([IO.File]::ReadAllText($srcPath)) -ReferencedAssemblies $refs `
                 -OutputAssembly $dll -OutputType Library
    } catch {
        Write-Warning "No se pudo guardar la DLL compilada: $($_.Exception.Message)"
    }
}

if (-not ('AbyssTide.App' -as [type])) {
    if (Test-Path $dll) { Add-Type -Path $dll }
    else { Add-Type -TypeDefinition ([IO.File]::ReadAllText($srcPath)) -ReferencedAssemblies $refs }
}

[AbyssTide.App]::Run([IO.Path]::GetFullPath($ConfigPath))
