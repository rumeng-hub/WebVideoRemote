# 一键构建：现在改用安装包方式（双击安装程序、选择安装位置）。
# 旧的单文件 exe 已不再适用：界面素材和浏览器扩展由安装步骤释放到安装目录。
# 用法： powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $root 'build-installer.ps1')
