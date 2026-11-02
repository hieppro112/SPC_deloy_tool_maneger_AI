@echo off
rem Cap nhat FE: git pull -> flutter build web -> chep sang run\web (giu ban cu run\web.prev).
rem Tool Manager chay: node serve-web.js run\web 5050, thu muc lam viec = thu muc repo.
rem Khong can Stop tool FE: serve-web.js doc file moi tu run\web ngay.
rem Dung: deploy.cmd            (pull + build + chep)
rem       deploy.cmd --no-pull  (chi build + chep)
setlocal
cd /d "%~dp0"

rem Project Flutter nam o thu muc con heatpress_fe-main, hoac ngay thu muc nay
set APP=%~dp0heatpress_fe-main
if not exist "%APP%\pubspec.yaml" set APP=%~dp0.
if not exist "%APP%\pubspec.yaml" (
    echo Khong tim thay pubspec.yaml
    goto :fail
)

if /i not "%~1"=="--no-pull" (
    echo === git pull
    git pull --ff-only
    if errorlevel 1 goto :fail
)

for /f %%c in ('git rev-parse --short HEAD') do set COMMIT=%%c
echo === Build commit %COMMIT% (web dang chay chua bi anh huong)
pushd "%APP%"
call flutter build web --release
set BUILD_ERR=%ERRORLEVEL%
popd
if not "%BUILD_ERR%"=="0" goto :fail
if not exist "%APP%\build\web\index.html" goto :fail

if exist run\web\index.html (
    robocopy run\web run\web.prev /MIR /NJH /NJS /NFL /NDL /NP >nul
    if errorlevel 8 goto :fail
)
robocopy "%APP%\build\web" run\web /MIR /NJH /NJS /NFL /NDL /NP >nul
if errorlevel 8 goto :fail
echo %COMMIT% %DATE% %TIME%> run\version.txt

echo.
echo === Xong: run\web = commit %COMMIT%. Tai lai trang tren trinh duyet la thay ban moi.
echo     Ban cu: run\web.prev (quay lai bang rollback.cmd)
exit /b 0

:fail
echo.
echo === LOI: dung lai, web dang chay khong bi thay doi.
exit /b 1
