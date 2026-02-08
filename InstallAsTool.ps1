[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot "src/LangQuery.Cli/LangQuery.Cli.csproj"
$packageOutput = Join-Path $repoRoot ".tmp/dotnet-tool"
$packageId = "LangQuery.Cli.Tool"
$toolCommand = "langquery"

if (-not (Test-Path $projectPath)) {
    throw "Could not find CLI project at '$projectPath'."
}

$now = Get-Date
$minuteInDay = [int][Math]::Floor($now.TimeOfDay.TotalMinutes)
$version = "{0:yy}.{0:MM}.{0:dd}.{1}" -f $now, $minuteInDay

New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
Get-ChildItem -Path $packageOutput -Filter "*.nupkg" -File -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "Packing $packageId version $version..."
dotnet pack $projectPath --configuration Release --output $packageOutput -p:Version=$version --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed."
}

Write-Host "Installing global tool package $packageId..."
$updateArgs = @(
    "tool", "update",
    "--global", $packageId,
    "--version", $version,
    "--allow-downgrade",
    "--add-source", $packageOutput,
    "--ignore-failed-sources"
)

dotnet @updateArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "Tool not installed yet. Running dotnet tool install..."

    $installArgs = @(
        "tool", "install",
        "--global", $packageId,
        "--version", $version,
        "--add-source", $packageOutput,
        "--ignore-failed-sources"
    )

    dotnet @installArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool install failed."
    }
}

Write-Host "Installed '$toolCommand' version $version."
Write-Host "Run '$toolCommand info' to verify the installed version."
