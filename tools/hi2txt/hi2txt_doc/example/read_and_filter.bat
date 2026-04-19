%~d0
cd "%~dp0"

rem Add parameter -hiscoredat <path>\hiscore.dat
rem if hiscore.dat is not in the application directory nor in the working directory
rem Add parameter -descr <path>\db
rem if XML database is not in the application directory nor in the working directory

call ..\hi2txt.exe -ra -keep-field "RANK" -keep-field "NAME" -keep-field "SCORE" -keep-first-table "yes" raiden2.hi

pause