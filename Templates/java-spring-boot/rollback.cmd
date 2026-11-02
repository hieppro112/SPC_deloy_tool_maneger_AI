@echo off
rem Quay lai ban truoc: run\app.prev.jar -> run\app.jar (ban loi giu lai o run\app.bad.jar).
setlocal
cd /d "%~dp0"

if not exist run\app.prev.jar (
    echo Khong co ban truoc run\app.prev.jar
    exit /b 1
)

set ASKED=
:wait
if not exist run\app.jar goto :swap
move /y run\app.jar run\app.bad.jar >nul 2>&1
if not errorlevel 1 goto :swap
if not defined ASKED (
    echo Bam STOP tool BE trong Tool Manager de quay lai ban truoc...
    set ASKED=1
)
ping -n 3 127.0.0.1 >nul
goto :wait

:swap
move /y run\app.prev.jar run\app.jar >nul
echo rollback %DATE% %TIME%> run\version.txt
echo === Xong: da quay lai ban truoc. Bam START tool BE trong Tool Manager.
exit /b 0
