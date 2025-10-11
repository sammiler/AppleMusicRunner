cd AppleMusicDecrypt
set WSL_AMDATA_PATH=/mnt/c/MyFile/dev/AMData
..\wsl1\LxRunOffline.exe r -n deb-amd -c "AMData='%WSL_AMDATA_PATH%' /root/.local/bin/poetry run python3 main.py"
cmd /k