<#
  Gera o MSI do FocoTotal. Rode no Windows com o SDK do .NET 8:
    ./scripts/build.ps1 -Version 0.1.0
  Resultado: artifacts\FocoTotal-<versão>-x64.msi
#>
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'
$artifacts = Join-Path $root 'artifacts'
Remove-Item $publish, $artifacts -Recurse -Force -ErrorAction SilentlyContinue

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
            "-p:Version=$Version")

dotnet publish (Join-Path $root 'src/FocoTotal.Service') @common -o (Join-Path $publish 'service')
if ($LASTEXITCODE) { throw 'Falha ao publicar o serviço' }
dotnet publish (Join-Path $root 'src/FocoTotal.App') @common -o (Join-Path $publish 'app')
if ($LASTEXITCODE) { throw 'Falha ao publicar o app' }

dotnet build (Join-Path $root 'installer/FocoTotal.Installer.wixproj') -c Release `
  "-p:Version=$Version" "-p:PublishDir=$publish/" -o $artifacts
if ($LASTEXITCODE) { throw 'Falha ao gerar o MSI' }

Get-ChildItem $artifacts -Filter *.msi | ForEach-Object { Write-Host "MSI: $($_.FullName)" }
