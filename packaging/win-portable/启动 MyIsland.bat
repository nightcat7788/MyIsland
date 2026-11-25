@echo off
chcp 65001 >nul
rem MyIsland 启动器：双击运行（需要 PowerShell 7 / pwsh，Windows 11 一般自带）
rem 如提示找不到 pwsh，请先安装：winget install Microsoft.PowerShell
where pwsh >nul 2>nul
if %errorlevel% neq 0 (
    echo [MyIsland] 未找到 pwsh，正在尝试用 winget 安装 PowerShell 7...
    winget install Microsoft.PowerShell --accept-source-agreements --accept-package-agreements
)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
