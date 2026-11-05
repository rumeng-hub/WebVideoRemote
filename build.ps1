# 一键构建：编译出 网页视频遥控器.exe（自包含单文件，目标机器无需安装 .NET）
# 用法： powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$pubDir = Join-Path $root 'build\publish'

Write-Host "==> dotnet publish (net8 self-contained single-file / win-x64)" -ForegroundColor Cyan
# SelfContained=true   : 运行时打进包里，任何 Win10 1809+ 机器双击即跑
# PublishSingleFile    : 一个 exe 搞定（首次启动解压到临时目录）
# EnableCompression    : 压缩内嵌程序集，体积约减半
& dotnet publish .\WebVideoRemote.csproj -c Release -r win-x64 --nologo `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $pubDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# ---------- 打包 ----------
Write-Host "==> 复制到 dist" -ForegroundColor Cyan
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $pubDir 'WebVideoRemote.exe') (Join-Path $dist '网页视频遥控器.exe') -Force

Write-Host "==> 复制浏览器桥接扩展" -ForegroundColor Cyan
$extDist = Join-Path $dist 'extension'
if (Test-Path $extDist) { Remove-Item -Recurse -Force $extDist }
Copy-Item -Recurse (Join-Path $root 'extension') $extDist

$exe = Join-Path $dist '网页视频遥控器.exe'
Write-Host ("==> 完成: " + $exe + "  (" + (Get-Item $exe).Length + " 字节)") -ForegroundColor Green
Write-Host ("    扩展目录: " + $extDist) -ForegroundColor Green
