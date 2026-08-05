@echo off
rem Builds the Stage 2 poll/inject harness (x86, matches the extension).
rem Run from this directory in a normal prompt; vcvars32 sets up the toolchain.
call "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars32.bat" >nul
cl /nologo /W3 /EHsc /std:c++17 /I "..\SWGCommandExtension" stage2_harness.cpp "..\SWGCommandExtension\DiscordBridge.cpp" /Fe:stage2_harness.exe
