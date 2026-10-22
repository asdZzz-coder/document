@echo off
chcp 65001 >nul
setlocal
rem ============================================================
rem  文件整理 安裝程式
rem  ClickOnce 會記住「從哪個資料夾安裝」，換資料夾安裝同一個程式會被拒絕。
rem  所以一律先把安裝檔複製到固定的資料夾 %LOCALAPPDATA%\DocumentKeeper-Setup，
rem  再從那裡安裝；程式內的「檢查更新」也會把新版放回這裡。
rem ============================================================
set "SRC=%~dp0"
set "DEST=%LOCALAPPDATA%\DocumentKeeper-Setup"

if not exist "%SRC%DocumentKeeper.application" (
    echo 找不到 DocumentKeeper.application。請先把整個 zip 解壓縮，再執行「安裝.cmd」。
    pause
    exit /b 1
)

rem 已經在固定資料夾裡執行，就不用再複製
if /i "%SRC%"=="%DEST%\" goto install

echo 正在準備安裝檔...
rem 先移除固定資料夾裡舊版本的檔案（只動 DocumentKeeper_* 版本資料夾）
if exist "%DEST%\Application Files" (
    for /d %%D in ("%DEST%\Application Files\DocumentKeeper_*") do rmdir /s /q "%%D"
)
robocopy "%SRC%." "%DEST%" DocumentKeeper.application /R:1 /W:1 /NJH /NJS /NFL /NDL /NP >nul
if errorlevel 8 goto copyfail
robocopy "%SRC%Application Files" "%DEST%\Application Files" /E /R:1 /W:1 /NJH /NJS /NFL /NDL /NP >nul
if errorlevel 8 goto copyfail

:install
echo 正在開啟安裝程式...
rem ClickOnce 的背景服務（dfsvc）沒在執行時，開 .application 有時完全沒反應，先把它叫起來
tasklist /FI "IMAGENAME eq dfsvc.exe" 2>nul | find /I "dfsvc.exe" >nul
if errorlevel 1 (
    set "DFSVC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\dfsvc.exe"
    if not exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\dfsvc.exe" set "DFSVC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\dfsvc.exe"
    call :startdfsvc
)
start "" "%DEST%\DocumentKeeper.application"
exit /b 0

:startdfsvc
if exist "%DFSVC%" (
    start "" "%DFSVC%"
    rem 等一秒讓服務準備好
    ping -n 2 127.0.0.1 >nul
)
exit /b 0

:copyfail
echo 複製安裝檔失敗，請確認磁碟空間或稍後再試。
pause
exit /b 1
