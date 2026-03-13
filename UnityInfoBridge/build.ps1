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
$outputDirectories = @{
    "Release_BepInEx_Mono" = "UnityInfoBridge.BepInEx.Mono"
    "Release_BepInEx_IL2CPP" = "UnityInfoBridge.BepInEx.IL2CPP"
    "Release_MelonLoader_Mono" = "UnityInfoBridge.MelonLoader.Mono"
    "Release_MelonLoader_IL2CPP" = "UnityInfoBridge.MelonLoader.IL2CPP"
}
$mainAssemblies = @{
    "Release_BepInEx_Mono" = "UnityInfoBridge.BepInEx.Mono.dll"
    "Release_BepInEx_IL2CPP" = "UnityInfoBridge.BepInEx.IL2CPP.dll"
    "Release_MelonLoader_Mono" = "UnityInfoBridge.MelonLoader.Mono.dll"
    "Release_MelonLoader_IL2CPP" = "UnityInfoBridge.MelonLoader.IL2CPP.dll"
}

foreach ($config in $Configurations) {
    Write-Host "Building $config ..."
    dotnet build $srcProject -c $config

    $outputDir = Join-Path $projectRoot ("Release\" + $outputDirectories[$config])
    $mainAssembly = Join-Path $outputDir $mainAssemblies[$config]
    $jsonAssembly = Join-Path $outputDir "Newtonsoft.Json.dll"

    if (-not (Test-Path $mainAssembly)) {
        throw "Missing bridge assembly after build: $mainAssembly"
    }

    if (-not (Test-Path $jsonAssembly)) {
        throw "Missing Newtonsoft.Json.dll after build: $jsonAssembly"
    }
}

Write-Host "Build finished."
