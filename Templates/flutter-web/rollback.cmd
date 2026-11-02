@echo off
rem Quay lai ban truoc: run\web.prev -> run\web. Khong can Stop tool FE.
setlocal
cd /d "%~dp0"

if not exist run\web.prev\index.html (
    echo Khong co ban truoc run\web.prev
    exit /b 1
)

robocopy run\web.prev run\web /MIR /NJH /NJS /NFL /NDL /NP >nul
if errorlevel 8 (
    echo === LOI khi chep run\web.prev
    exit /b 1
)
echo rollback %DATE% %TIME%> run\version.txt
echo === Xong: da quay lai ban truoc. Tai lai trang tren trinh duyet.
exit /b 0
