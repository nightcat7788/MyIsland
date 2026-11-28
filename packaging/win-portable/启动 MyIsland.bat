@echo off
chcp 65001 >nul
rem MyIsland 便携版启动器：优先使用 pwsh 内存加载，未安装 pwsh 时回退直接启动 Island.App.exe
where pwsh >nul 2>nul
if %errorlevel% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Dir "%~dp0" %*
    exit /b %errorlevel%
)
if exist "%~dp0Island.App.exe" (
    start "" "%~dp0Island.App.exe" %*
    exit /b 0
)
echo [MyIsland] 未找到 PowerShell 7 (pwsh) 或 Island.App.exe。
choice /C YN /M "是否使用 winget 安装 Microsoft.PowerShell"
if errorlevel 2 exit /b 1
winget install Microsoft.PowerShell
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Dir "%~dp0" %*
