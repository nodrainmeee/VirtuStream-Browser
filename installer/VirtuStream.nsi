; Installateur VirtuStream Browser — nécessite les droits administrateur
; (enregistrement du filtre DirectShow caméra virtuelle, installation de VB-Cable optionnelle).
!include "MUI2.nsh"
!include "x64.nsh"
Name "VirtuStream Browser"
OutFile "..\dist\VirtuStream-Setup.exe"
InstallDir "$PROGRAMFILES64\VirtuStream"
RequestExecutionLevel admin
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "French"

Section "Application"
  SetOutPath "$INSTDIR"
  File /r "..\dist\app\*.*"
  ; Caméra virtuelle : les deux versions pour que les applications 32 et 64 bits la voient
  SetOutPath "$INSTDIR\softcam\x64"
  File "..\dist\softcam\x64\softcam.dll"
  SetOutPath "$INSTDIR\softcam\x86"
  File "..\dist\softcam\x86\softcam.dll"
  ExecWait 'regsvr32 /s "$INSTDIR\softcam\x64\softcam.dll"'
  SetRegView 32
  ExecWait '"$SYSDIR\..\SysWOW64\regsvr32.exe" /s "$INSTDIR\softcam\x86\softcam.dll"'
  SetRegView 64
  ; softcam.dll x64 à côté de l'exe (l'exe est AnyCPU -> 64 bits sur Win10 x64)
  CopyFiles "$INSTDIR\softcam\x64\softcam.dll" "$INSTDIR\softcam.dll"
  CreateShortcut "$DESKTOP\VirtuStream Browser.lnk" "$INSTDIR\VirtuStream.exe"
  CreateDirectory "$SMPROGRAMS\VirtuStream"
  CreateShortcut "$SMPROGRAMS\VirtuStream\VirtuStream Browser.lnk" "$INSTDIR\VirtuStream.exe"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\VirtuStream" "DisplayName" "VirtuStream Browser"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\VirtuStream" "UninstallString" '"$INSTDIR\Uninstall.exe"'
SectionEnd

Section "Microphone virtuel (VB-Audio Virtual Cable)" SecMic
  IfFileExists "$INSTDIR\vbcable\VBCABLE_Setup_x64.exe" 0 +2
  ExecWait '"$INSTDIR\vbcable\VBCABLE_Setup_x64.exe" -i -h'
SectionEnd

Section "Uninstall"
  ExecWait 'regsvr32 /u /s "$INSTDIR\softcam\x64\softcam.dll"'
  ExecWait '"$SYSDIR\..\SysWOW64\regsvr32.exe" /u /s "$INSTDIR\softcam\x86\softcam.dll"'
  Delete "$DESKTOP\VirtuStream Browser.lnk"
  RMDir /r "$SMPROGRAMS\VirtuStream"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\VirtuStream"
  ; Les données utilisateur (%LOCALAPPDATA%\VirtuStream) sont conservées ; VB-Cable se désinstalle séparément.
SectionEnd
