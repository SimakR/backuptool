setlocal
REM убеждаемся что есть админские права
net sessions >NUL 2>&1 || echo "Error : Run as admin!" && timeout /T 3 && exit /b
REM восстанавливаем путь
cd /d %~dp0
REM Бывают кривые х64 тачки с кривым PATH, где по-умолчанию запускается x32 cmd.exe
IF DEFINED PROCESSOR_ARCHITEW6432 C:\windows\sysnative\cmd.exe /c %0 && exit /b

SET REMOTEPATH=
SET REMOTEUSER=
SET REMOTEPWD=
SET FILELOG=log.txt
SET /p UID=<uid.txt
IF NOT DEFINED UID SET UID=%COMPUTERNAME%
SET MARKERFILE=%TEMP%\%UID%

del %MARKERFILE%
echo Script started> %FILELOG%

for /f "tokens=*" %%S in ('net use ^| find "%REMOTEPATH%"') do set STRRAW=%%S
IF NOT DEFINED STRRAW echo Backup share not mounted, trying to mount>>%FILELOG% && goto :connect
set BACKUPDRIVE=%STRRAW:~13,1%
echo Backup share already mounted as drive %BACKUPDRIVE%:>>%FILELOG%
goto backup

:connect
for %%d in (e f g h i j k l m n o p q r s t u v w x y z) do net use %%d: "%REMOTEPATH%" "%REMOTEPWD%" /user:%REMOTEUSER% /persistent:no && set BACKUPDRIVE=%%d&& echo Backup share mounted as drive %%d:>>%FILELOG% && goto backup
echo Failed to mount backup share>>%FILELOG%
echo Aborting>>%FILELOG%
goto end_error

:backup
echo Backup profiles>>%FILELOG% && backuptool -bs 1G -d "%BACKUPDRIVE%:\%UID%" -b profiles >>%FILELOG% && echo Done>>%FILELOG% || echo Error>>%FILELOG% && goto end_error
for /f %%A in ('ListSystemPathes.exe') do echo Backup path %%A>>%FILELOG% && backuptool.exe -bs 1G -d "%BACKUPDRIVE%:\%UID%" -i -b "%%A" >>%FILELOG% && echo Done>>%FILELOG% || echo Error>>%FILELOG% && goto end_error

echo OK>%MARKERFILE%

echo All done>>%FILELOG%
echo Exiting>>%FILELOG%
net use %BACKUPDRIVE%: /delete /yes
net use %REMOTEPATH% /delete /yes
goto end

:end_error

echo ERROR>%MARKERFILE%

:end
timeout /t 10