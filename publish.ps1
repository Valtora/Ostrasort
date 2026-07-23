# Builds the end-user release artifacts with Velopack and shapes them for
# release. A release ships THREE files: the versioned installer
# (Ostrasort-v<ver>-Setup.exe), the update package (Ostrasort-<ver>-full.nupkg)
# and the update manifest (releases.win.json). vpk also emits a portable zip and
# a legacy RELEASES file into publish\releases\; those are NOT shipped. Upload the
# three with `gh release create` (this script prints the command; see
# docs\development.md).
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

# 5) Version-name the installer. vpk names it '<packId>-<channel>-Setup.exe'
#    (Ostrasort-win-Setup.exe); the updater keys off releases.win.json + the
#    nupkg, not the installer name, so renaming it is safe and gives each release
#    a self-describing download.
$setupSrc = Join-Path $relDir 'Ostrasort-win-Setup.exe'
$setupOut = Join-Path $relDir "Ostrasort-v$ver-Setup.exe"
if (-not (Test-Path $setupSrc)) { throw "vpk pack did not produce $setupSrc." }
if (Test-Path $setupOut) { Remove-Item $setupOut -Force }
Rename-Item $setupSrc $setupOut

$nupkg    = Join-Path $relDir "Ostrasort-$ver-full.nupkg"
$manifest = Join-Path $relDir 'releases.win.json'
foreach ($f in @($nupkg, $manifest)) {
    if (-not (Test-Path $f)) { throw "Expected release asset missing: $f" }
}

"`nRelease artifacts (v$ver)  ->  $relDir" | Write-Output
Get-ChildItem $relDir | Sort-Object Name | ForEach-Object {
    "  {0,-34} {1,8:N1} KB" -f $_.Name, ($_.Length / 1KB)
}
"`nShip ONLY these three (the nupkg + manifest are what self-update reads):" | Write-Output
"  Ostrasort-v$ver-Setup.exe, Ostrasort-$ver-full.nupkg, releases.win.json" | Write-Output
"`nGUI smoke passed. Publish with (draft the notes first):" | Write-Output
"  gh release create v$ver --title v$ver --notes-file notes.md ``" | Write-Output
"      `"$setupOut`" ``" | Write-Output
"      `"$nupkg`" ``" | Write-Output
"      `"$manifest`"" | Write-Output
exit 0
