@echo off
start "" /b /wait "%~dp0MeetTape.exe" %*
exit /b %errorlevel%
