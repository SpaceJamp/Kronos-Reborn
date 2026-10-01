@echo off

call "%~dp0config.cmd"

echo.
echo ################################
echo Compiling the installer
echo ################################
echo.

REM Inno Setup is installed per-user by winget and machine-wide by the traditional installer, so
REM check both rather than requiring it on PATH. The bare name is the last resort.
set "iscc_exe="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "iscc_exe=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "iscc_exe=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "iscc_exe=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not defined iscc_exe set "iscc_exe=ISCC.exe"

REM Unconditional, with the error suppressed so a missing previous build is not noise. It has to be
REM unconditional: leaving a stale exe in place would let a failed compile look like a success.
if exist "%output_installer%" del /q "%output_installer%"

REM The version comes from config.cmd rather than being repeated in the .iss, which is what used to
REM let the installer's name and the project's version drift apart.
"%iscc_exe%" /DAppVersion=%app_version% Installer.iss || goto :error

echo.
echo Built %output_installer%
echo.

REM As in build.cmd: report the path rather than have the workflow reconstruct the name. This one
REM matters more, because the installer was previously asked for by a workflow that never ran this
REM script, so the file could not have existed however the name was spelled.
if defined GITHUB_OUTPUT >>"%GITHUB_OUTPUT%" echo installer=%output_installer%

exit /b 0

:error
echo.
echo ERROR: installer build failed with error code %errorlevel%.
set "result=%errorlevel%"
exit /b %result%
