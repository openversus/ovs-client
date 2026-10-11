@echo off
rem Double-click or run from cmd: the same as build.ps1, see it for the options.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*

