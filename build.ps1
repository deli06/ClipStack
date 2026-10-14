<#
.SYNOPSIS
    ClipStack'i test eder, yayınlar ve kurulum paketini oluşturur.

.DESCRIPTION
    1. Birim testlerini çalıştırır (-SkipTests ile atlanır).
    2. ClipStack.exe'yi .NET gömülü, sıkıştırılmış tek dosya olarak artifacts\publish'e yayınlar
       (kullanıcının .NET kurmasına gerek kalmaz).
    3. Lisans ve üçüncü taraf bildirimlerini yanına koyar.
    4. Inno Setup 6 kuruluysa artifacts\ClipStack-Setup-<sürüm>.exe kurulum paketini derler.

    Sürüm numarası ClipStack\ClipStack.csproj içindeki <Version>'dan okunur.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SkipTests
#>
param(
    [switch]$SkipTests,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'ClipStack\ClipStack.csproj'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

[xml]$xml = Get-Content $project -Encoding UTF8
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw "ClipStack.csproj içinde <Version> bulunamadı." }
Write-Host "ClipStack $version" -ForegroundColor Green

if (-not $SkipTests) {
    Step 'Birim testleri'
    dotnet test (Join-Path $root 'ClipStack.Tests') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Testler başarısız; yayınlama yapılmadı.' }
}

Step 'Yayınlama (win-x64, .NET gömülü tek exe)'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Yayınlama başarısız.' }

Step '.NET üçüncü taraf bildirimleri'
# Gömülü .NET çalışma zamanının kendi bildirim dosyası (NuGet önbelleğindeki çalışma zamanı paketinden).
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$runtimeDir = Get-ChildItem (Join-Path $nuget 'microsoft.netcore.app.runtime.win-x64') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '8.*' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $runtimeDir) { throw '.NET çalışma zamanı paketi NuGet önbelleğinde bulunamadı.' }
Copy-Item (Join-Path $runtimeDir.FullName 'THIRD-PARTY-NOTICES.TXT') (Join-Path $publish 'dotnet-THIRD-PARTY-NOTICES.txt')
Write-Host "  .NET $($runtimeDir.Name)"

Get-ChildItem $publish | ForEach-Object { '  {0,-34} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }

Step 'Kurulum paketi (Inno Setup)'
$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning ("Inno Setup 6 bulunamadı; kurulum paketi oluşturulmadı. Yayınlanan exe: $publish`n" +
                   "Kurmak için: https://jrsoftware.org/isdl.php (ya da: winget install JRSoftware.InnoSetup)")
    exit 0
}

& $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$publish" "/DRepoRoot=$root" "/O$artifacts" `
    (Join-Path $root 'installer\ClipStack.iss')
if ($LASTEXITCODE -ne 0) { throw 'Kurulum paketi derlenemedi.' }

$setup = Join-Path $artifacts "ClipStack-Setup-$version.exe"
Write-Host ("`nHazır: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
