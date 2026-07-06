# Builds the end-user release artifact: ONE self-contained Ostrasort.exe
# (no .NET install needed on the target machine). Output: publish\Ostrasort.exe
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'publish'

# IncludeNativeLibrariesForSelfExtract is REQUIRED for WPF: WPF's native DLLs
# (PresentationNative, wpfgfx, D3DCompiler) cannot be loaded from the bundle
# in memory, so without this the app dies at first window with a
# DllNotFoundException deep in HwndSubclass. This flag extracts native libs to
# a temp folder on first run so LoadLibrary finds them.
dotnet publish (Join-Path $PSScriptRoot 'Ostrasort.csproj') -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Get-Item (Join-Path $out 'Ostrasort.exe')

# Smoke-test the PUBLISHED single-file exe (not just bin\Release): construct the
# WPF windows the way a real launch does. This is what catches single-file-only
# native-load failures that a bin\Release smoke test cannot.
& $exe.FullName --smoke-gui --no-pause | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Published exe failed its GUI smoke test (exit $LASTEXITCODE) - single-file WPF is broken, do NOT ship this build." }

# Name the validated artifact with its version (Ostrasort vX.Y.Z.exe), replacing
# any previously-built versioned exe so publish\ holds just the current release.
$ver = ($exe.VersionInfo.ProductVersion -split '\+')[0]
Get-ChildItem $out -Filter 'Ostrasort v*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force
$named = Join-Path $out "Ostrasort v$ver.exe"
Move-Item $exe.FullName $named -Force
$exe = Get-Item $named

"`n{0}`n  v{1}   {2:N1} MB   single-file, self-contained win-x64   (GUI smoke passed)" -f
    $exe.FullName, $exe.VersionInfo.ProductVersion, ($exe.Length / 1MB)

# Launch the freshly-built exe so the release can be eyeballed immediately.
# Start-Process is non-blocking, so the GUI opens and this script returns.
Start-Process -FilePath $exe.FullName
exit 0
