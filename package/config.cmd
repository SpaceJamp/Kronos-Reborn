@echo off

REM
REM Single source for the version and the output filenames.
REM
REM The csproj takes its version from Directory.Build.props; config.cmd here is the value the
REM installer is compiled with and the name the artifacts are given. Two places, so they have to
REM agree, and a test checks that they do. Handing the real value to ISCC rather than repeating it in
REM the .iss is what stops the installer's name drifting from the project's.
REM

set app_version=0.1.0

set project_name=Kronos Reborn
set project_id=KronosReborn

set output_dir=%~dp0Output
set output_installer=%output_dir%\%project_id%-%app_version%-installer.exe
set output_zip=%output_dir%\%project_id%-%app_version%-portable.zip
set csproj_file=%~dp0..\src\%project_id%\%project_id%.csproj

REM PublishDir is resolved by MSBuild relative to the project, not to the current directory, so the
REM published output lands under src\<project>\bin\publish\portable. It is spelled out once here
REM because three separate things need it - the zip, the Inno script and anyone reading a failed
REM build - and getting it subtly wrong produces an empty archive rather than an error.
set publish_dir=%~dp0..\src\%project_id%\bin\publish\portable

if not exist "%output_dir%" mkdir "%output_dir%"

exit /b 0
