<#
  Abyss Tide — arranca el agua ASCII que vive detrás de Windows Terminal.
  Compila AbyssTide.cs con el compilador que ya trae Windows (no hace falta instalar nada)
  y deja un icono en la bandeja del sistema (clic derecho → Pausar / Salir).

  Uso manual:  powershell -NoProfile -STA -ExecutionPolicy Bypass -File .\abyss-tide.ps1
#>
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'abyss-tide.config.json')
)

$ErrorActionPreference = 'Stop'

$src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AbyssTide.cs'))
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Windows.Forms, System.Drawing -Language CSharp

$cfg = New-Object AbyssTide.Config
if (Test-Path $ConfigPath) {
    $json = [IO.File]::ReadAllText($ConfigPath) | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) {
        if ($p.Name.StartsWith('_')) { continue }   # claves "_..." son comentarios
        try { $cfg.($p.Name) = $p.Value }
        catch { Write-Warning "Opción desconocida o inválida en la config: $($p.Name)" }
    }
}

[AbyssTide.App]::Run($cfg)
