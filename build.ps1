# 一键构建：编译出 网页视频遥控器.exe
# 用法： powershell -ExecutionPolicy Bypass -File build.ps1
# 优先用 .NET SDK（dotnet build）；如果机器上没装或不可用，则回退到 .NET Framework 自带的 csc.exe。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$out      = Join-Path $root 'build\WebVideoRemote.exe'
$manifest = Join-Path $root 'src\app.manifest'
$rtw      = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'
$csc      = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$winmd    = 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd'
$winmdDir = 'C:\Windows\System32\WinMetadata'

# WinRT 元数据：优先用 Windows SDK 的整套 Windows.winmd；
# 如果 SDK 被清理过（这个文件夹没了），就用系统自带的拆分元数据代替
$winmdRefs = @()
if (Test-Path $winmd) {
    $winmdRefs += ('/reference:"' + $winmd + '"')
} elseif (Test-Path $winmdDir) {
    foreach ($f in @('Windows.Foundation.winmd', 'Windows.Media.winmd', 'Windows.Storage.winmd',
                     'Windows.ApplicationModel.winmd', 'Windows.System.winmd')) {
        $p = Join-Path $winmdDir $f
        if (Test-Path $p) { $winmdRefs += ('/reference:"' + $p + '"') }
    }
}

New-Item -ItemType Directory -Force -Path (Join-Path $root 'build') | Out-Null

$built = $false

# ---------- 方式一：.NET SDK ----------
$sdkOk = $false
try {
    $sdkList = & dotnet --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdkList) { $sdkOk = $true }
} catch { }

if ($sdkOk) {
    Write-Host "==> 用 dotnet build 编译 (net48 / WinForms)" -ForegroundColor Cyan
    & dotnet build .\WebVideoRemote.csproj -c Release --nologo
    if ($LASTEXITCODE -eq 0) { $built = $true }
    else { Write-Host "dotnet build 失败，改用 csc.exe" -ForegroundColor Yellow }
}

# ---------- 方式二：.NET Framework 自带的 csc.exe ----------
if (-not $built) {
    if (-not (Test-Path $csc))   { throw "找不到 csc.exe，也没有可用的 .NET SDK" }
    if ($winmdRefs.Count -eq 0)  { throw "找不到任何 WinRT 元数据（Windows.winmd 或 System32\WinMetadata）" }
    Write-Host "==> 用 csc.exe 直接编译" -ForegroundColor Cyan

    $files = Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { '"' + $_.FullName + '"' }
    $ext = Join-Path $root 'extension'
    # Windows.winmd 依赖 facades 里的转发程序集（MSBuild 会自动带，csc 要手动加）
    $facades = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades'
    $facadeRefs = @()
    if (Test-Path $facades) {
        foreach ($f in @('System.Runtime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll',
                         'System.ObjectModel.dll', 'System.Collections.dll', 'System.Runtime.Extensions.dll')) {
            $p = Join-Path $facades $f
            if (Test-Path $p) { $facadeRefs += ('/reference:"' + $p + '"') }
        }
    }
    $cscArgs = @(
        '/nologo', '/codepage:65001', '/target:winexe', '/platform:anycpu', '/optimize+',
        ('/out:"' + $out + '"'),
        ('/win32manifest:"' + $manifest + '"'),
        '/reference:System.dll', '/reference:System.Core.dll',
        '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
        ('/reference:"' + $rtw + '"'),
        ('/resource:"' + (Join-Path $ext 'manifest.json') + '",WebVideoRemote.extension.manifest.json'),
        ('/resource:"' + (Join-Path $ext 'content.js')    + '",WebVideoRemote.extension.content.js'),
        ('/resource:"' + (Join-Path $ext 'background.js') + '",WebVideoRemote.extension.background.js'),
        ('/resource:"' + (Join-Path $ext 'options.html')  + '",WebVideoRemote.extension.options.html'),
        ('/resource:"' + (Join-Path $ext 'options.js')    + '",WebVideoRemote.extension.options.js')
    ) + $winmdRefs + $facadeRefs + $files

    & $csc $cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc 编译失败" }
    $built = $true
}

# 运行时配置（.NET 4.8）
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
</configuration>
'@ | Set-Content (Join-Path $root 'build\WebVideoRemote.exe.config') -Encoding UTF8

# ---------- 打包 ----------
Write-Host "==> 复制到 dist" -ForegroundColor Cyan
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item $out (Join-Path $dist '网页视频遥控器.exe') -Force
Copy-Item (Join-Path $root 'build\WebVideoRemote.exe.config') (Join-Path $dist '网页视频遥控器.exe.config') -Force

Write-Host "==> 复制浏览器桥接扩展" -ForegroundColor Cyan
$extDist = Join-Path $dist 'extension'
if (Test-Path $extDist) { Remove-Item -Recurse -Force $extDist }
Copy-Item -Recurse (Join-Path $root 'extension') $extDist

$exe = Join-Path $dist '网页视频遥控器.exe'
Write-Host ("==> 完成: " + $exe + "  (" + (Get-Item $exe).Length + " 字节)") -ForegroundColor Green
Write-Host ("    扩展目录: " + $extDist) -ForegroundColor Green
