# Builds dist/Beastkeeper-<version>.zip (Thunderstore / manual install) from package/.
# Run after build.sh:  powershell -File pack.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = (Get-Content "$root\package\manifest.json" -Raw | ConvertFrom-Json).version_number

Copy-Item "$root\bin\Beastkeeper.dll" "$root\package\plugins\Beastkeeper\Beastkeeper.dll" -Force
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$zip = "$root\dist\Beastkeeper-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Confirm:$false }

# Build the zip by hand so entries use "/" (Compress-Archive on Windows PowerShell writes "\").
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($f in Get-ChildItem "$root\package" -Recurse -File) {
  $rel = $f.FullName.Substring("$root\package\".Length).Replace('\', '/')
  [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, $rel)
}
$z.Dispose()
Write-Host "Criado / created: $zip"
