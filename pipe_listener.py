import time
import datetime
import win32pipe, win32file, pywintypes
import re
import os

PIPE_NAME = r'\\.\pipe\RetroBatArcadePipe'
LOG_FILE = "pipe_output.log"

def pipe_server():
    print("========================================")
    print(f" DEMARRAGE DU SERVEUR DE BORNE ARCADE ")
    print("========================================")
    
    # Pattern pour detecter un horodatage C++ : [HH:MM:SS.mmm]
    TS_PATTERN = re.compile(r'^\[\d{2}:\d{2}:\d{2}\.\d{3}\]')
    
    while True:
        now = datetime.datetime.now().strftime('%H:%M:%S')
        msg = f"\n[{now}] [EN ATTENTE] Le serveur ecoute sur {PIPE_NAME}..."
        print(msg, flush=True)
        
        pipe = win32pipe.CreateNamedPipe(
            PIPE_NAME,
            win32pipe.PIPE_ACCESS_INBOUND,
            win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
            1, 65536, 65536,
            0,
            None
        )
        
        try:
            win32pipe.ConnectNamedPipe(pipe, None)
            now = datetime.datetime.now().strftime('%H:%M:%S')
            print(f"[{now}] => Connexion etablie avec RetroArch ! Lecture de la RAM en cours a 60 FPS:")
            
            buffer = ""
            while True:
                # Lecture des donnees brutes depuis le pipe
                try:
                    hr, data = win32file.ReadFile(pipe, 65536)
                    if not data: break
                    
                    # Decodage et gestion du buffer pour les messages partiels
                    chunk = data.decode('utf-8', errors='ignore')
                    buffer += chunk
                    
                    # On traite les lignes completes
                    while "\n" in buffer:
                        line, buffer = buffer.split("\n", 1)
                        message = line.strip()
                        if not message: continue
                        
                        # Horodatage
                        if not TS_PATTERN.match(message):
                            timestr = datetime.datetime.now().strftime('%H:%M:%S.%f')[:-3]
                            message = f"[{timestr}] {message}"
                        
                        # Double sortie : Console ET Fichier
                        print(message, flush=True)
                        try:
                            with open(LOG_FILE, "a", encoding="utf-8") as f:
                                f.write(message + "\n")
                        except: pass
                            
                except pywintypes.error as e:
                    if e.winerror == 109: # Pipe brise
                        break
                    raise e
                    
        except pywintypes.error as e:
            now = datetime.datetime.now().strftime('%H:%M:%S')
            print(f"[{now}] Erreur pipe : {e}")
        finally:
            win32file.CloseHandle(pipe)

if __name__ == '__main__':
    pipe_server()
