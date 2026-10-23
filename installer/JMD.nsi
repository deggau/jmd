Unicode true
!include "MUI2.nsh"
!include "StrFunc.nsh"
${StrStr}

!ifndef APP_VERSION
  !define APP_VERSION "0.1.81"
!endif
!ifndef PUBLISH_DIR
  !error "PUBLISH_DIR must point to the published application directory"
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "..\artifacts"
!endif
!ifndef APP_RUNTIME
  !define APP_RUNTIME "win-x64"
!endif

!define APP_NAME "JMD"
!define APP_PUBLISHER "JMD"
!define APP_EXE "JMD.App.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\JMD"

Name "${APP_NAME} ${APP_VERSION}"
Caption "${APP_NAME} ${APP_VERSION} Setup"
OutFile "${OUTPUT_DIR}\JMD-${APP_VERSION}-${APP_RUNTIME}-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "Software\${APP_NAME}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Install" SEC_INSTALL
  SectionIn RO
  DetailPrint "Closing any running JMD instance before updating..."
  nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /F /IM "${APP_EXE}"'
  Pop $0
  Sleep 500
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"

  FileOpen $0 "$INSTDIR\jmd.cmd" w
  FileWrite $0 "@echo off$\r$\n"
  FileWrite $0 "$\"%~dp0${APP_EXE}$\" --cli %*$\r$\n"
  FileClose $0

  DetailPrint "Configuring PowerShell command completion..."
  ExecWait '"$INSTDIR\${APP_EXE}" --cli -install-completion' $0
  StrCmp $0 "0" completionConfigured
  DetailPrint "PowerShell completion could not be configured automatically. Run jmd -install-completion later."
completionConfigured:

  ReadRegStr $0 HKCU "Environment" "Path"
  ${StrStr} $1 $0 "$INSTDIR"
  StrCmp $1 "" addJmdToPath pathUpdated
addJmdToPath:
  StrCmp $0 "" emptyUserPath
  WriteRegExpandStr HKCU "Environment" "Path" "$INSTDIR;$0"
  Goto pathUpdated
emptyUserPath:
  WriteRegExpandStr HKCU "Environment" "Path" "$INSTDIR"
pathUpdated:
  System::Call 'user32::SendMessageTimeoutW(p 0xffff, i 0x001A, p 0, w "Environment", i 0x0002, i 5000, *p .r1)'

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\${APP_NAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$"$INSTDIR\Uninstall.exe$"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$"$INSTDIR\Uninstall.exe$" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ExecShell "open" "$INSTDIR\${APP_EXE}"
SectionEnd

Section "Uninstall" un.Uninstall
  ExecWait '"$INSTDIR\${APP_EXE}" --cli -uninstall-completion' $0
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  Delete "$INSTDIR\jmd.cmd"
  ReadRegStr $0 HKCU "Environment" "Path"
  StrLen $1 "$INSTDIR;"
  StrCpy $2 $0 $1
  StrCmp $2 "$INSTDIR;" removeJmdFromPath
  StrCmp $0 "$INSTDIR" removeOnlyJmdPath
  Goto pathRemovalDone
removeJmdFromPath:
  StrCpy $0 $0 "" $1
  StrCmp $0 "" removeOnlyJmdPath
  WriteRegExpandStr HKCU "Environment" "Path" $0
  Goto pathRemovalDone
removeOnlyJmdPath:
  DeleteRegValue HKCU "Environment" "Path"
pathRemovalDone:
  System::Call 'user32::SendMessageTimeoutW(p 0xffff, i 0x001A, p 0, w "Environment", i 0x0002, i 5000, *p .r1)'
  RMDir "$SMPROGRAMS\${APP_NAME}"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\${APP_NAME}"
  RMDir /r "$INSTDIR"
SectionEnd
