@echo off
rem 编译"上下分屏助手"（使用 Windows 自带的 .NET Framework 编译器，无需安装任何东西）
cd /d "%~dp0"
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /codepage:65001 /win32icon:app.ico /out:UpDownSplit.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll UpDownSplit.cs
if errorlevel 1 (
    echo 编译失败
    exit /b 1
)
echo 编译完成: %~dp0UpDownSplit.exe
