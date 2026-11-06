param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile2016 = Join-Path $projectDirectory "CadDrawingNumber2016.csproj"
$autocad2016Directory = Join-Path $projectDirectory "dist\AutoCAD2016"

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

& $dotnetExe build $projectFile2016 -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "AutoCAD 2016 专用版编译失败。"
}

New-Item -ItemType Directory -Force -Path $autocad2016Directory | Out-Null

$acad2016Output = Join-Path $projectDirectory "bin\AutoCAD2016"
$builtDll = Join-Path $acad2016Output "TYPC.dll"

# 从编译产物读取程序集版本，打包为带版本号的文件名（TYPC_vX.Y.Z.dll），
# 避免与旧版本混淆。TYPC_Load.lsp 会自动探测目录里最新的 TYPC_v*.dll。
$version = [System.Reflection.AssemblyName]::GetAssemblyName($builtDll).Version
$versionTag = "$($version.Major).$($version.Minor).$($version.Build)"

Get-ChildItem -LiteralPath $autocad2016Directory -Filter "TYPC*.dll" -File |
    Remove-Item -Force

$targetDll = Join-Path $autocad2016Directory "TYPC_v$versionTag.dll"
Copy-Item $builtDll $targetDll -Force

Write-Host "构建完成。AutoCAD 2016 DLL: $targetDll"
