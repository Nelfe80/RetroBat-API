%~d0
cd "%~dp0"

rem Add parameter -hiscoredat <path>\hiscore.dat
rem if hiscore.dat is not in the application directory nor in the working directory
rem Add parameter -descr <path>\db
rem if XML database is not in the application directory nor in the working directory

call ..\hi2txt.exe -ra -max-lines 5 -max-columns 3 raiden2.hi


pause