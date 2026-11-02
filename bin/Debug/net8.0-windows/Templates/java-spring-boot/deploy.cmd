@echo off
rem Cap nhat BE: git pull -> build jar -> thay run\app.jar (giu ban cu run\app.prev.jar).
rem Tool Manager chay: java -jar run\app.jar, thu muc lam viec = thu muc repo.
rem Dung: deploy.cmd            (pull + build + thay jar)
rem       deploy.cmd --no-pull  (chi build + thay jar, dung tren may dev)
setlocal
cd /d "%~dp0"

if /i not "%~1"=="--no-pull" (
    echo === git pull
    git pull --ff-only
    if errorlevel 1 goto :fail
)

for /f %%c in ('git rev-parse --short HEAD') do set COMMIT=%%c
echo === Build commit %COMMIT% (ban dang chay chua bi anh huong)
call "%~dp0mvnw.cmd" -q -DskipTests package
if errorlevel 1 goto :fail

set NEWJAR=
for %%f in (target\*.jar) do set NEWJAR=%%f
if "%NEWJAR%"=="" (
    echo Khong thay file jar trong target\
    goto :fail
)

if not exist run mkdir run
if not exist run\app.jar goto :copy

rem Jar dang chay bi Windows khoa: cho den khi tool duoc Stop
set ASKED=
:wait
move /y run\app.jar run\app.prev.jar >nul 2>&1
if not errorlevel 1 goto :copy
if not defined ASKED (
    echo.
    echo Build xong. Bam STOP tool BE trong Tool Manager de thay jar...
    set ASKED=1
)
ping -n 3 127.0.0.1 >nul
goto :wait

:copy
copy /y "%NEWJAR%" run\app.jar >nul
if errorlevel 1 goto :fail
echo %COMMIT% %DATE% %TIME%> run\version.txt
echo.
echo === Xong: run\app.jar = commit %COMMIT%. Bam START tool BE trong Tool Manager.
echo     Ban cu: run\app.prev.jar (quay lai bang rollback.cmd)
exit /b 0

:fail
echo.
echo === LOI: dung lai, ban dang chay khong bi thay doi.
exit /b 1
