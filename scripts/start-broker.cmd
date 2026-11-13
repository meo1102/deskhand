@echo off
rem deskhand watcher: starts the broker while DSH is running.
cd /d "%~dp0"
start "" /min "%~dp0..\helper\deskhand.exe" watch
