@echo off
rem 编译"上下分屏助手"（使用 Windows 自带的 .NET Framework 编译器，无需安装任何东西）
rem 自动探测 csc.exe：先 Framework64 再 Framework，取版本号最高的
setlocal
cd /d "%~dp0"
set "CSC="
for %%D in ("%WINDIR%\Microsoft.NET\Framework64" "%WINDIR%\Microsoft.NET\Framework") do (
    for /f "delims=" %%F in ('dir /b /ad "%%~D\v4*" 2^>nul') do (
        if exist "%%~D\%%F\csc.exe" set "CSC=%%~D\%%F\csc.exe"
    )
)
if not defined CSC (
    echo 未找到 .NET Framework 编译器 csc.exe
    exit /b 1
)
"%CSC%" /nologo /target:winexe /codepage:65001 /win32icon:app.ico /out:UpDownSplit.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll UpDownSplit.cs
if errorlevel 1 (
    echo 编译失败
    exit /b 1
)
echo 编译完成: %~dp0UpDownSplit.exe
