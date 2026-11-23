# Builds goodbyedpi.exe (x86_64) and packs it with WinDivert into dist\x86_64.
# Needs: MinGW-w64 (winget install BrechtSanders.WinLibs.POSIX.UCRT) and Git for Windows.
param([switch]$Clean)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$wdName = 'WinDivert-2.2.0-D'
$wdSha = '1d461cfdfa7ba88ebcfbb3603b71b703e9f72aba8aeff99a75ce293e6f89d2ba'
$wd = Join-Path $root "deps\$wdName"

# MinGW from winget (WinLibs) unless gcc is already on PATH
if (-not (Get-Command gcc -ErrorAction SilentlyContinue)) {
    $mingw = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\BrechtSanders.WinLibs*\mingw64\bin" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $mingw) { throw 'MinGW-w64 not found: winget install BrechtSanders.WinLibs.POSIX.UCRT' }
    $env:PATH = "$($mingw.FullName);$env:PATH"
}
# The Makefile uses sh, which and rm: take them from Git for Windows
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) {
    $gitUsr = Join-Path (Split-Path (Split-Path $git.Source)) 'usr\bin'
    if (Test-Path $gitUsr) { $env:PATH = "$env:PATH;$gitUsr" }
}

# WinDivert headers, import library and driver
if (-not (Test-Path $wd)) {
    New-Item -ItemType Directory -Force (Join-Path $root 'deps') | Out-Null
    $zip = Join-Path $root "deps\$wdName.zip"
    Invoke-WebRequest "https://reqrypt.org/download/$wdName.zip" -OutFile $zip -UseBasicParsing
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $wdSha) { Remove-Item $zip; throw 'WinDivert checksum mismatch' }
    Expand-Archive $zip (Join-Path $root 'deps')
}

Push-Location (Join-Path $root 'src')
try {
    if ($Clean) { mingw32-make clean | Out-Null }
    mingw32-make CPREFIX= BIT64=1 "WINDIVERTHEADERS=../deps/$wdName/include" "WINDIVERTLIBS=../deps/$wdName/x64" -j8
    if ($LASTEXITCODE) { throw "build failed ($LASTEXITCODE)" }
} finally { Pop-Location }

$dist = Join-Path $root 'dist\x86_64'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $root 'src\goodbyedpi.exe'), "$wd\x64\WinDivert.dll", "$wd\x64\WinDivert64.sys" $dist -Force
Write-Host "OK: $dist"

# The panel embeds dist\x86_64, so it is built after it. Needs the .NET SDK
# (winget install Microsoft.DotNet.SDK.10); the result runs on .NET Framework 4.8.
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet -and (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe")) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }
if ($dotnet -and (Test-Path "$env:ProgramFiles\dotnet\sdk")) {
    & $dotnet build (Join-Path $root 'panel\GoodbyeDPI.Panel.csproj') -c Release -nologo -v quiet
    if ($LASTEXITCODE) { throw "panel build failed ($LASTEXITCODE)" }
    Copy-Item (Join-Path $root 'panel\bin\Release\net48\GoodbyeDPI-Panel.exe') (Join-Path $root 'dist') -Force
    Write-Host "OK: $(Join-Path $root 'dist\GoodbyeDPI-Panel.exe')"

    # Setup file (Inno Setup 6: winget install JRSoftware.InnoSetup)
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        $ver = ([xml](Get-Content (Join-Path $root 'panel\GoodbyeDPI.Panel.csproj'))).Project.PropertyGroup.Version | Select-Object -First 1
        & $iscc -Q "/DAppVersion=$ver" (Join-Path $root 'installer\GoodbyeDPI.iss')
        if ($LASTEXITCODE) { throw "setup build failed ($LASTEXITCODE)" }
        Write-Host "OK: $(Join-Path $root 'dist\GoodbyeDPI-Kurulum.exe')"
    } else {
        Write-Host "Setup skipped: Inno Setup not found"
    }
} else {
    Write-Host "Panel skipped: .NET SDK not found"
}
