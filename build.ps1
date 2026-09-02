param(
    [string]$VintageStoryPath = $env:VINTAGE_STORY,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repository "src\BlockShot.VintageStory\BlockShot.VintageStory.csproj"
$projectDirectory = Split-Path -Parent $project
$version = (Get-Content (Join-Path $projectDirectory "modinfo.json") -Raw | ConvertFrom-Json).version
$assemblySuffix = "v" + ($version -replace '[^A-Za-z0-9]', '_')
$mainAssembly = "BlockShot.VintageStory.$assemblySuffix.dll"
$coreAssembly = "BlockShot.VintageStory.Core.$assemblySuffix.dll"
$runtimeAssemblies = @(
    "SIPSorcery.VP8.dll",
    "SIPSorceryMedia.Abstractions.dll",
    "Microsoft.Extensions.Logging.Abstractions.dll",
    "Microsoft.Extensions.DependencyInjection.Abstractions.dll"
)
$buildOutput = Join-Path $projectDirectory "bin\$Configuration\net10.0"
$artifacts = Join-Path $repository "artifacts"
$stage = Join-Path $artifacts "blockshot"
$archive = Join-Path $artifacts "BlockShot-VintageStory-$version.zip"

if ([string]::IsNullOrWhiteSpace($VintageStoryPath)) {
    throw "Set VINTAGE_STORY or pass -VintageStoryPath with the Vintage Story installation directory."
}

dotnet build $project -c $Configuration -p:VintageStoryPath=$VintageStoryPath -p:BlockShotAssemblySuffix=$assemblySuffix
if ($LASTEXITCODE -ne 0) { throw "BlockShot build failed." }

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $buildOutput $mainAssembly) -Destination $stage
Copy-Item -LiteralPath (Join-Path $buildOutput $coreAssembly) -Destination $stage
foreach ($runtimeAssembly in $runtimeAssemblies) {
    Copy-Item -LiteralPath (Join-Path $buildOutput $runtimeAssembly) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $buildOutput "modinfo.json") -Destination $stage
Copy-Item -LiteralPath (Join-Path $repository "LICENSE.md") -Destination $stage
Copy-Item -LiteralPath (Join-Path $repository "NOTICE") -Destination $stage
Copy-Item -LiteralPath (Join-Path $repository "THIRD-PARTY-NOTICES.md") -Destination $stage
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive
Write-Host "Created $archive"
