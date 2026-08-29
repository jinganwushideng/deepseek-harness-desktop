Unicode true

!ifndef APP_VERSION
  !define APP_VERSION "1.1.2"
!endif
!ifndef PUBLISH_DIR
  !error "PUBLISH_DIR is required"
!endif
!ifndef OUTPUT_DIR
  !error "OUTPUT_DIR is required"
!endif

!define APP_NAME "DeepSeek Harness Desktop"
!define APP_EXE "DeepSeekHarnessDesktop.exe"
!define APP_ID "DeepSeekHarnessDesktop"
!define COMPANY_NAME "jinganwushideng"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"

Name "${APP_NAME}"
OutFile "${OUTPUT_DIR}\DeepSeek-Harness-Desktop-Setup-${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "Software\${APP_ID}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
CRCCheck force
ManifestDPIAware true
Icon "..\DeepSeekHarnessDesktop\Assets\Harness.ico"
UninstallIcon "..\DeepSeekHarnessDesktop\Assets\Harness.ico"
BrandingText "DeepSeek Harness Desktop"

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "FileDescription" "DeepSeek Harness 桌面壳安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "CompanyName" "${COMPANY_NAME}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright © 2026 jinganwushideng"

!include "MUI2.nsh"
!include "LogicLib.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "..\DeepSeekHarnessDesktop\Assets\Harness.ico"
!define MUI_UNICON "..\DeepSeekHarnessDesktop\Assets\Harness.ico"
!define MUI_WELCOMEPAGE_TITLE "安装 ${APP_NAME}"
!define MUI_WELCOMEPAGE_TEXT "这将安装 DeepSeek Harness 的 Windows 桌面壳。$\r$\n$\r$\n程序安装到当前用户目录，无需管理员权限。已有 DSH_HOME、会话、插件、Skill 和凭据不会被覆盖。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "启动 ${APP_NAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"

Function EnsureAppIsClosed
  retry:
  FindWindow $0 "" "DeepSeek Harness Desktop"
  ${If} $0 != 0
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "DeepSeek Harness Desktop 仍在运行。$\r$\n$\r$\n请在系统托盘中选择“彻底退出”，然后点击“重试”。" IDRETRY retry IDCANCEL cancel
  ${EndIf}
  Return
  cancel:
  Abort
FunctionEnd

Function .onInit
  Call EnsureAppIsClosed
FunctionEnd

Section "主程序" SEC_MAIN
  SectionIn RO
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File /oname=${APP_EXE} "${PUBLISH_DIR}\${APP_EXE}"
  File /oname=LICENSE.txt "..\LICENSE"
  File /oname=THIRD_PARTY_NOTICES.md "..\THIRD_PARTY_NOTICES.md"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0

  WriteRegStr HKCU "Software\${APP_ID}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${COMPANY_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" 322500
SectionEnd

Function un.onInit
  Call un.EnsureAppIsClosed
FunctionEnd

Function un.EnsureAppIsClosed
  retry:
  FindWindow $0 "" "DeepSeek Harness Desktop"
  ${If} $0 != 0
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "DeepSeek Harness Desktop 仍在运行。$\r$\n$\r$\n请在系统托盘中选择“彻底退出”，然后点击“重试”。" IDRETRY retry IDCANCEL cancel
  ${EndIf}
  Return
  cancel:
  Abort
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}.lnk"
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\THIRD_PARTY_NOTICES.md"
  Delete "$INSTDIR\Uninstall.exe"

  ; 只删除可重建的运行时与缓存。保留 launcher.json、backups
  ; 以及用户选择的 DSH_HOME，避免卸载时损坏会话或凭据。
  RMDir /r "$INSTDIR\runtime"
  RMDir /r "$INSTDIR\webview-data"
  RMDir /r "$INSTDIR\logs"
  RMDir /r "$INSTDIR\staging"
  Delete "$INSTDIR\helper.mjs"
  Delete "$INSTDIR\launcher.patch.yml"
  RMDir "$INSTDIR"

  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\${APP_ID}"
SectionEnd
