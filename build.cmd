@echo off
setlocal
chcp 65001 >nul

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] 未找到 .NET Framework 编译器 csc.exe，请确认系统已安装 .NET Framework 4.x
    exit /b 1
)

echo [INFO] 使用编译器: %CSC%
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /out:ProxySelfHeal.exe src\ProxySelfHeal.cs

if errorlevel 1 (
    echo [ERROR] 编译失败
    exit /b 1
)

echo.
echo [OK] 已生成 ProxySelfHeal.exe
for %%F in (ProxySelfHeal.exe) do echo      大小: %%~zF 字节
echo.
echo 运行 "ProxySelfHeal.exe --help" 查看用法
endlocal
