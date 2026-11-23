<#
    一键出包：自包含单文件、轻量单文件、调试版。
    用法：powershell -ExecutionPolicy Bypass -File scripts/build.ps1
    产物在 dist/，源码树不受影响。
#>
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'DeskTodo.csproj'
$dist = Join-Path $root 'dist'

if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Path $dist | Out-Null

function Invoke-Publish {
    param(
        [string]$OutDir,
        [string[]]$ExtraArgs
    )

    Write-Host "==> publish -> $OutDir" -ForegroundColor Cyan
    dotnet publish $project -c $Configuration -o $OutDir @ExtraArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败：$OutDir"
    }
}

$commonSingleFile = @(
    '-p:PublishSingleFile=true'
    '-p:DebugType=None'
    '-p:DebugSymbols=false'
)

# 1. 自包含单文件：不装 .NET 也能跑
$selfDir = Join-Path $dist 'selfcontained'
Invoke-Publish -OutDir $selfDir -ExtraArgs (@(
    '-r', 'win-x64'
    '--self-contained', 'true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:EnableCompressionInSingleFile=true'
) + $commonSingleFile)
Copy-Item (Join-Path $selfDir 'DeskTodo.exe') (Join-Path $dist '桌面清单.exe')

# 2. 轻量单文件：依赖 .NET 8 桌面运行时
$liteDir = Join-Path $dist 'lite'
Invoke-Publish -OutDir $liteDir -ExtraArgs (@(
    '-r', 'win-x64'
    '--self-contained', 'false'
) + $commonSingleFile)
Copy-Item (Join-Path $liteDir 'DeskTodo.exe') (Join-Path $dist '桌面清单-轻量版.exe')

# 3. 调试版：不抓屏、不排除抓屏，实心底，方便录屏和远程看界面
$debugDir = Join-Path $dist 'debug'
Invoke-Publish -OutDir $debugDir -ExtraArgs (@(
    '-r', 'win-x64'
    '--self-contained', 'false'
    '-p:DefineConstants=TDL_DEBUGUI'
) + $commonSingleFile)
Copy-Item (Join-Path $debugDir 'DeskTodo.exe') (Join-Path $dist '桌面清单-调试.exe')

# 4. 使用说明一并放进 dist
Copy-Item (Join-Path $root 'docs\USAGE.md') (Join-Path $dist '使用说明.md')
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $dist 'LICENSE')

Get-ChildItem $dist -Directory | Remove-Item -Recurse -Force

Write-Host ''
Write-Host '==> 完成' -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
