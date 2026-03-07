param(
    [string[]]$Configurations = @(
        "Release_BepInEx_Mono",
        "Release_BepInEx_IL2CPP",
        "Release_MelonLoader_Mono",
        "Release_MelonLoader_IL2CPP"
    )
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcProject = Join-Path $projectRoot "src/UnityInfoBridge.csproj"

foreach ($config in $Configurations) {
    Write-Host "Building $config ..."
    dotnet build $srcProject -c $config
}

Write-Host "Build finished."
