@echo off
rem Builds a single SpotifyLyricsFloat.exe into .\publish (needs the .NET 10 Desktop Runtime to run).
cd /d "%~dp0"
dotnet publish src\LyricsFloat.App -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none -o publish
