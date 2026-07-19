# Builds the end-user release artifacts with Velopack: an installer
# (Ostrasort-win-Setup.exe), a portable zip (Ostrasort-win-Portable.zip), the
# update package (*-full.nupkg) and the update manifest (releases.win.json).
# Output: publish\releases\. Upload the whole folder with `vpk upload github`
# (see docs\development.md).
#
# Close the running app first: it locks its own exe and the publish will fail.
$ErrorActionPreference = 'Stop'
$root     = $PSScriptRoot
$rawDir   = Join-Path $root 'publish\raw'          # plain self-contained publish (what Velopack packs)
$relDir   = Join-Path $root 'publish\releases'     # the release artifacts
$csproj   = Join-Path $root 'Ostrasort.csproj'
$icon     = Join-Path $root 'app.ico'

# vpk (the Velopack CLI) is a global dotnet tool. Install it once with:
#   dotnet tool install -g vpk
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk (the Velopack CLI) is not installed. Run:  dotnet tool install -g vpk"
}

# 1) Plain self-contained publish to a directory. NOT single-file: Velopack does
#    its own bundling, and a normal layout keeps the WPF native DLLs
#    (PresentationNative, wpfgfx, D3DCompiler) beside the exe so they load
#    without the single-file self-extract dance the old build needed.
if (Test-Path $rawDir) { Remove-Item $rawDir -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 `
    --self-contained true `
    -p:DebugType=None `
    -o $rawDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$rawExe = Join-Path $rawDir 'Ostrasort.exe'
if (-not (Test-Path $rawExe)) { throw "Publish did not produce $rawExe." }

# 2) Smoke-test the PUBLISHED exe (not bin\Release): construct every WPF window
#    the way a real launch does, so a native-load or theming regression fails the
#    build here instead of on a user's machine.
$smoke = Start-Process -FilePath $rawExe -ArgumentList '--smoke-gui','--no-pause' -Wait -PassThru -NoNewWindow
if ($smoke.ExitCode -ne 0) { throw "Published exe failed its GUI smoke test (exit $($smoke.ExitCode)). Do NOT ship this build." }

# 3) Version from the built exe (the single source of truth: csproj <Version>).
$ver = ((Get-Item $rawExe).VersionInfo.ProductVersion -split '\+')[0]
if (-not $ver) { throw "Could not read a version from $rawExe." }

# 4) Pack with Velopack. Produces installer + portable + update package + manifest
#    in $relDir. --channel defaults to 'win', so the manifest is releases.win.json
#    (exactly what the in-app UpdateManager reads on Windows).
if (Test-Path $relDir) { Remove-Item $relDir -Recurse -Force }
vpk pack `
    --packId Ostrasort `
    --packVersion $ver `
    --packDir $rawDir `
    --mainExe Ostrasort.exe `
    --packTitle Ostrasort `
    --packAuthors Valtora `
    --icon $icon `
    --outputDir $relDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE." }

"`nRelease artifacts (v$ver)  ->  $relDir" | Write-Output
Get-ChildItem $relDir | Sort-Object Name | ForEach-Object {
    "  {0,-34} {1,8:N1} KB" -f $_.Name, ($_.Length / 1KB)
}
"`nGUI smoke passed. Publish with:" | Write-Output
"  vpk upload github --repoUrl https://github.com/Valtora/Ostrasort --publish --releaseName v$ver --tag v$ver --token (gh auth token)" | Write-Output
exit 0
