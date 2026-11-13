# 一键构建安装包：把主程序、.NET 运行时、assets 素材、extension 扩展、卸载程序打成 payload.zip，
# 再内嵌进安装向导，输出 dist\网页视频遥控器-安装程序.exe
# 用法： powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$payload = Join-Path $root 'build\payload'
$zip = Join-Path $root 'build\payload.zip'
$uninstOut = Join-Path $root 'build\uninst'
$setupOut = Join-Path $root 'build\setup'
$dist = Join-Path $root 'dist'

$appExeName = '网页视频遥控器.exe'
$uninstName = '卸载.exe'

# ---------- 1. 主程序：自包含多文件发布（exe 只留程序本身，运行时/素材都是独立文件） ----------
Write-Host '==> dotnet publish (net8 self-contained, multi-file / win-x64)' -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
& dotnet publish .\WebVideoRemote.csproj -c Release -r win-x64 --nologo `
    -p:SelfContained=true -p:PublishSingleFile=false `
    -o $payload
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

# apphost 按文件名找 runtimeconfig/deps，重命名时三个文件要一起改；托管 dll 名保持不变
Rename-Item (Join-Path $payload 'WebVideoRemote.exe') $appExeName
Rename-Item (Join-Path $payload 'WebVideoRemote.runtimeconfig.json') '网页视频遥控器.runtimeconfig.json'
Rename-Item (Join-Path $payload 'WebVideoRemote.deps.json') '网页视频遥控器.deps.json'

# ---------- 2. 界面素材 ----------
Write-Host '==> 复制界面素材 assets' -ForegroundColor Cyan
$assetsDir = Join-Path $payload 'assets'
New-Item -ItemType Directory -Force $assetsDir | Out-Null
foreach ($n in @('hero_furina_band.png', 'hero_firefly_band.png', 'char_furina_card.png', 'char_firefly_card.png')) {
    Copy-Item (Join-Path $root ('assets\' + $n)) $assetsDir -Force
}

# ---------- 3. 浏览器桥接扩展 ----------
Write-Host '==> 复制浏览器桥接扩展 extension' -ForegroundColor Cyan
Copy-Item -Recurse (Join-Path $root 'extension') (Join-Path $payload 'extension') -Force

# ---------- 4. 卸载程序 ----------
Write-Host '==> 编译卸载程序' -ForegroundColor Cyan
if (Test-Path $uninstOut) { Remove-Item -Recurse -Force $uninstOut }
& dotnet build (Join-Path $root 'installer\Uninstall\Uninstall.csproj') -c Release --nologo -o $uninstOut
if ($LASTEXITCODE -ne 0) { throw '卸载程序编译失败' }
Copy-Item (Join-Path $uninstOut 'WebVideoRemoteUninstall.exe') (Join-Path $payload $uninstName) -Force

# ---------- 5. 打包安装负载 ----------
Write-Host '==> 打包 payload.zip' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item -Force $zip }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $payload, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# ---------- 6. 安装向导（内嵌 payload.zip） ----------
Write-Host '==> 编译安装向导' -ForegroundColor Cyan
if (Test-Path $setupOut) { Remove-Item -Recurse -Force $setupOut }
& dotnet build (Join-Path $root 'installer\Setup\Setup.csproj') -c Release --nologo -o $setupOut
if ($LASTEXITCODE -ne 0) { throw '安装向导编译失败' }

New-Item -ItemType Directory -Force $dist | Out-Null
$setupName = '网页视频遥控器-V2.0-安装程序.exe'
Copy-Item (Join-Path $setupOut 'WebVideoRemoteSetup.exe') (Join-Path $dist $setupName) -Force

# 旧版散装文件已经不再需要
$oldExt = Join-Path $dist 'extension'
if (Test-Path $oldExt) { Remove-Item -Recurse -Force $oldExt }
foreach ($old in @('网页视频遥控器.exe', '网页视频遥控器-安装程序.exe')) {
  $oldExe = Join-Path $dist $old
  if (Test-Path $oldExe) { Remove-Item -Force $oldExe }
}

# ---------- 汇总 ----------
$setup = Join-Path $dist $setupName
Write-Host ''
Write-Host ('==> 完成: ' + $setup) -ForegroundColor Green
Write-Host ('    安装包大小 : ' + [math]::Round((Get-Item $setup).Length / 1MB, 1) + ' MB')
Write-Host ('    释放后大小 : ' + [math]::Round((Get-ChildItem -Recurse -File $payload | Measure-Object Length -Sum).Sum / 1MB, 1) + ' MB')
Write-Host ('    文件数量   : ' + (Get-ChildItem -Recurse -File $payload).Count + ' 个')
Write-Host ('    安装负载   : ' + $zip)
