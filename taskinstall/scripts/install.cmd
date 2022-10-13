@echo off
setlocal

SET INSTALLPATH=C:\tools

mkdir %INSTALLPATH%
7za.exe x backuptool.7z -o%INSTALLPATH% -y
cd /d %INSTALLPATH%\backuptool
echo %1> uid.txt
schtasks /create /xml "backuptask.xml" /tn "backuptask"
schtasks /run /tn "backuptask"
