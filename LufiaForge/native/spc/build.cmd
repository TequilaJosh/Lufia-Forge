@echo off
rem Builds lufia_spc.dll (x64) with the Visual Studio C++ tools and copies it into the app project.
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do set VS=%%i
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist obj mkdir obj
cl /nologo /O2 /EHsc /MT /LD /DNDEBUG /DBLARGG_LITTLE_ENDIAN=1 /Foobj\ lufia_spc.cpp Snes_Spc.cpp Spc_Cpu.cpp Spc_Dsp.cpp Spc_Filter.cpp /Fe:obj\lufia_spc.dll || exit /b 1
copy /y obj\lufia_spc.dll ..\..\LufiaForge\lufia_spc.dll >nul
echo built lufia_spc.dll
