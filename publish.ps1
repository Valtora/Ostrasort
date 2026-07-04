# Builds the end-user release artifact: ONE self-contained Ostrasort.exe
# (no .NET install needed on the target machine). Output: publish\Ostrasort.exe
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'publish'

dotnet publish (Join-Path $PSScriptRoot 'Ostrasort.csproj') -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Get-Item (Join-Path $out 'Ostrasort.exe')
"`n{0}`n  v{1}   {2:N1} MB   single-file, self-contained win-x64" -f
    $exe.FullName, $exe.VersionInfo.ProductVersion, ($exe.Length / 1MB)
exit 0
