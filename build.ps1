param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectDirectory "CadDrawingNumber.csproj"
$projectFile2016 = Join-Path $projectDirectory "CadDrawingNumber2016.csproj"
$bundleDirectory = Join-Path $projectDirectory "dist\CadDrawingNumber.bundle"
$autocad2016Directory = Join-Path $projectDirectory "dist\AutoCAD2016"
$resolvedProjectDirectory = [System.IO.Path]::GetFullPath($projectDirectory).TrimEnd('\') + '\'
$resolvedBundleDirectory = [System.IO.Path]::GetFullPath($bundleDirectory)

if (-not $resolvedBundleDirectory.StartsWith(
    $resolvedProjectDirectory,
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "输出目录不在项目目录内，已停止清理。"
}

# 优先使用项目内自带的 .dotnet SDK，其次回退到 PATH 中的 dotnet。
$dotnetExe = Join-Path $projectDirectory ".dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) {
        $dotnetExe = $dotnetCommand.Source
    } else {
        throw "未找到 dotnet。请先安装 .NET 8 SDK，或在项目目录下放 .dotnet。"
    }
}

& $dotnetExe build $projectFile -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "项目编译失败。"
}

& $dotnetExe build $projectFile2016 -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "AutoCAD 2016 专用版编译失败。"
}

if (Test-Path -LiteralPath $resolvedBundleDirectory) {
    Remove-Item -LiteralPath $resolvedBundleDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path (Join-Path $bundleDirectory "Contents\2021-2024") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $bundleDirectory "Contents\2025-Plus") | Out-Null
New-Item -ItemType Directory -Force -Path $autocad2016Directory | Out-Null
Copy-Item (Join-Path $projectDirectory "PackageContents.xml") $bundleDirectory -Force

$net48Output = Join-Path $projectDirectory "bin\$Configuration\net48"
$net8Output = Join-Path $projectDirectory "bin\$Configuration\net8.0-windows"
$acad2016Output = Join-Path $projectDirectory "bin\AutoCAD2016"

Copy-Item (Join-Path $net48Output "CadDrawingNumber.dll") (Join-Path $bundleDirectory "Contents\2021-2024") -Force
Copy-Item (Join-Path $net8Output "CadDrawingNumber.dll") (Join-Path $bundleDirectory "Contents\2025-Plus") -Force
# 2016 项目设置了 AssemblyName=TYPC，输出文件名为 TYPC.dll。
Copy-Item (Join-Path $acad2016Output "TYPC.dll") $autocad2016Directory -Force

Write-Host "构建完成: $bundleDirectory"
Write-Host "AutoCAD 2016 DLL: $autocad2016Directory\TYPC.dll"
