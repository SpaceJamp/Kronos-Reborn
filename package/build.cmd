@echo off

call "%~dp0config.cmd"

REM
REM Collect the git metadata that Directory.Build.props injects into the assembly.
REM
REM Only the branch and the tag are passed in. The commit is filled in by the SDK from
REM SourceRevisionId on its own, and the timestamp is stamped by the csproj, so neither needs doing
REM here. MSBuild cannot run git while evaluating properties, which is the whole reason the first two
REM are passed rather than read directly.
REM
REM An empty tag between releases is expected, not an error: nothing is tagged until a release is cut,
REM and a build with no tag simply does not offer a release-notes link.
REM
set "git_branch="
set "git_tag="

for /f "usebackq delims=" %%i in (`git rev-parse --abbrev-ref HEAD 2^>nul`) do set "git_branch=%%i"
for /f "usebackq delims=" %%i in (`git describe --tags --exact-match 2^>nul`) do set "git_tag=%%i"

if "%git_branch%"=="" set "git_branch=unknown"

echo.
echo ################################
echo Compiling
echo ################################
echo.

REM Self-contained, so the installer carries its own runtime and a user does not have to install
REM .NET first. A framework-dependent build is a fraction of the size and much worse for the person
REM who downloaded it.
dotnet publish "%csproj_file%" ^
    --runtime win-x64 ^
    --self-contained ^
    --configuration Release ^
    -p:PublishDir=bin\publish\portable\ ^
    -p:GitBranch="%git_branch%" ^
    -p:GitTag="%git_tag%" || goto :error

echo.
echo ################################
echo Zipping the portable build
echo ################################
echo.

if not exist "%publish_dir%" (
    echo ERROR: nothing was published to "%publish_dir%".
    set "result=1"
    exit /b 1
)

if exist "%output_zip%" del /q "%output_zip%"
powershell -NoProfile -Command "Compress-Archive -Path '%publish_dir%\*' -DestinationPath '%output_zip%' -Force" || goto :error

echo.
echo Built %output_zip%
echo.

REM Hand the path back to GitHub Actions when running there. The workflow uploads exactly what was
REM produced rather than re-deriving the filename from a pattern, which is what used to break: the
REM name in config.cmd and the glob in the workflow disagreed, nothing compared them, and the upload
REM step failed on a file that existed. Guarded so a local run writes nothing.
if defined GITHUB_OUTPUT >>"%GITHUB_OUTPUT%" echo portable=%output_zip%

exit /b 0

:error
echo.
echo ERROR: build failed with error code %errorlevel%.
set "result=%errorlevel%"
exit /b %result%
