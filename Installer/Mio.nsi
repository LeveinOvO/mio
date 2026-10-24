Unicode true
!ifndef MIO_VERSION
!error "Build using python3 Installer/build.py"
!endif
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "nsDialogs.nsh"

!ifndef PRODUCT_NAME
!define PRODUCT_NAME "Mio"
!endif
!ifndef REG_KEY
!define REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Mio"
!endif
!ifndef OUTPUT_FILE
!define OUTPUT_FILE "../artifacts/releases/V${MIO_VERSION}/Mio-Setup-${MIO_VERSION}.exe"
!endif

Name "${PRODUCT_NAME} ${MIO_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$PROGRAMFILES64\${PRODUCT_NAME}"
RequestExecutionLevel admin
Var RestartMode
Var DeleteUserData
Var DeleteDataCheckbox
Var DeleteOwnedRoutes
Var DeleteRoutesCheckbox
ManifestDPIAware true
!ifdef VALIDATION_BUILD
SetCompressor /SOLID zlib
!else
SetCompressor /SOLID lzma
SetCompressorDictSize 32
!endif
VIProductVersion "${MIO_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${MIO_VERSION}"
VIAddVersionKey /LANG=2052 "FileVersion" "${MIO_VERSION}.0"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "LegalCopyright" "MIO"
Icon "../Assets/AppIcon.ico"
UninstallIcon "../Assets/AppIcon.ico"

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Mio · Windows 网络工具箱$\r$\n$\r$\n点击下一步开始安装。"
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE CheckInstallDirectory
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
UninstPage custom un.ShowDataOption un.SaveDataOption
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
    ${IfNot} ${RunningX64}
        MessageBox MB_ICONSTOP "本安装程序需要 Windows x64。"
        Abort
    ${EndIf}
    ${IfNot} ${AtLeastWin10}
        MessageBox MB_ICONSTOP "本安装程序需要 Windows 10 或更高版本。"
        Abort
    ${EndIf}
    SetRegView 64
    SetShellVarContext all
    ReadRegStr $0 HKLM "${REG_KEY}" "InstallLocation"
    ${If} $0 != ""
        StrCpy $INSTDIR $0
    ${EndIf}
FunctionEnd

Function un.onInit
    SetRegView 64
    SetShellVarContext all
    StrCpy $DeleteUserData 0
    StrCpy $DeleteOwnedRoutes 0
FunctionEnd

Function un.ShowDataOption
    !insertmacro MUI_HEADER_TEXT "卸载 MIO" "选择是否保留应用数据。"
    nsDialogs::Create 1018
    Pop $0
    ${If} $0 == error
        Abort
    ${EndIf}
    ${NSD_CreateLabel} 0 0 100% 30u "将卸载 MIO。$\r$\n安装位置：$INSTDIR"
    Pop $1
    ${NSD_CreateCheckbox} 0 48u 100% 16u "删除整个安装目录（包含规则、设置、日志及其他文件）"
    Pop $DeleteDataCheckbox
    ${NSD_SetState} $DeleteDataCheckbox $DeleteUserData
    ${NSD_CreateCheckbox} 0 76u 100% 16u "删除 MIO 创建的系统路由"
    Pop $DeleteRoutesCheckbox
    ${NSD_SetState} $DeleteRoutesCheckbox $DeleteOwnedRoutes
    nsDialogs::Show
FunctionEnd

Function un.SaveDataOption
    ${NSD_GetState} $DeleteDataCheckbox $DeleteUserData
    ${NSD_GetState} $DeleteRoutesCheckbox $DeleteOwnedRoutes
FunctionEnd

; Never overwrite/delete through a directory junction, including parent directories.
!macro DirectoryCheck PREFIX
Function ${PREFIX}CheckInstallDirectory
    ${If} $INSTDIR == ""
        Abort
    ${EndIf}
    StrCpy $0 $INSTDIR
    check_parent:
        System::Call 'kernel32::GetFileAttributesW(w r0) i.r1'
        ${If} $1 != -1
            IntOp $2 $1 & 0x400
            ${If} $2 != 0
                MessageBox MB_ICONSTOP "安装路径不能是链接或目录联接：$0"
                Abort
            ${EndIf}
        ${EndIf}
        StrCpy $1 $0 3
        ${If} $0 == $1
            ${If} $0 == $INSTDIR
                MessageBox MB_ICONSTOP "请选择独立的软件目录，不能安装到磁盘根目录。"
                Abort
            ${EndIf}
            Return
        ${EndIf}
        ${GetParent} "$0" $1
        ${If} $1 == ""
        ${OrIf} $1 == $0
            Return
        ${EndIf}
        StrCpy $0 $1
        Goto check_parent
FunctionEnd
!macroend
!include "FileFunc.nsh"
!insertmacro DirectoryCheck ""
!insertmacro DirectoryCheck "un."

; A write-open of a running image fails. Retry only after the user's acknowledgement.
; Silent mode fails instead of hanging or killing the user's process.
!macro WaitForFile PREFIX
Function ${PREFIX}WaitForFile
    Pop $3
    ; Check intermediate payload directories too, before following their paths.
    ${GetParent} "$3" $4
    check_file_parent:
        System::Call 'kernel32::GetFileAttributesW(w r4) i.r1'
        ${If} $1 != -1
            IntOp $2 $1 & 0x400
            ${If} $2 != 0
                MessageBox MB_ICONSTOP "安装目录不能是链接：$4" /SD IDOK
                Abort
            ${EndIf}
        ${EndIf}
        ${If} $4 == $INSTDIR
            Goto retry_file
        ${EndIf}
        ${GetParent} "$4" $1
        ${If} $1 == ""
        ${OrIf} $1 == $4
            Goto retry_file
        ${EndIf}
        StrCpy $4 $1
        Goto check_file_parent
    retry_file:
        IfFileExists "$3" 0 done_file
        System::Call 'kernel32::GetFileAttributesW(w r3) i.r1'
        IntOp $2 $1 & 0x400
        ${If} $2 != 0
            MessageBox MB_ICONSTOP "安装文件不能是链接：$3" /SD IDOK
            Abort
        ${EndIf}
        System::Call 'kernel32::CreateFileW(w r3, i 0xC0000000, i 0, p 0, i 3, i 0, p 0) p.r0'
        ${If} $0 P<> -1
            System::Call 'kernel32::CloseHandle(p r0)'
            Goto done_file
        ${EndIf}
        MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "无法更新文件（可能仍被程序占用）：$\r$\n$3$\r$\n$\r$\n请从托盘退出 MIO，关闭后点击确定继续；不会强制结束进程。" /SD IDCANCEL IDOK retry_file
        SetErrorLevel 2
        Abort
    done_file:
FunctionEnd
!macroend
!insertmacro WaitForFile ""
!insertmacro WaitForFile "un."

Section "Install"
    Call CheckInstallDirectory
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File "Check-Prerequisites.ps1"
    SetOutPath "$WINDIR"
    ${DisableX64FSRedirection}
    nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Check-Prerequisites.ps1"'
    Pop $0
    Pop $1
    ${EnableX64FSRedirection}
    ${If} $0 != 0
        MessageBox MB_ICONSTOP "运行环境检查未通过，尚未停止或更新程序：$\r$\n$1" /SD IDOK
        SetErrorLevel 2
        Abort
    ${EndIf}
    StrCpy $RestartMode 0
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File "Stop-ForUpgrade.ps1"
    ; NSIS System.dll is a native plugin, not the .NET System.dll used by Add-Type.
    ; Do not run the helper with the plugin directory as its working directory.
    SetOutPath "$WINDIR"
    DetailPrint "正在检查运行中的程序并请求正常退出（转发会短暂中断）…"
    ${DisableX64FSRedirection}
    ExecWait '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Stop-ForUpgrade.ps1" -Directory "$INSTDIR"' $RestartMode
    ${EnableX64FSRedirection}
    ${If} $RestartMode != 0
    ${AndIf} $RestartMode != 10
    ${AndIf} $RestartMode != 11
        MessageBox MB_ICONSTOP "程序未能正常退出。请从托盘退出后重试升级；未强制结束进程。" /SD IDOK
        SetErrorLevel 2
        Abort
    ${EndIf}
    !include "build/check-files.nsh"
    Push "$INSTDIR\Uninstall.exe"
    Call WaitForFile
    SetOverwrite on
    !include "build/install-files.nsh"
    SetOutPath "$INSTDIR"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
    CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\Mio.exe" "" "$INSTDIR\Assets\AppIcon.ico"
    CreateShortcut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\Mio.exe" "" "$INSTDIR\Assets\AppIcon.ico"
    WriteRegStr HKLM "${REG_KEY}" "DisplayName" "${PRODUCT_NAME}"
    WriteRegStr HKLM "${REG_KEY}" "DisplayVersion" "${MIO_VERSION}"
    WriteRegStr HKLM "${REG_KEY}" "Publisher" "Mio"
    WriteRegStr HKLM "${REG_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${REG_KEY}" "DisplayIcon" "$INSTDIR\Mio.exe"
    WriteRegStr HKLM "${REG_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegStr HKLM "${REG_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
    WriteRegDWORD HKLM "${REG_KEY}" "NoModify" 1
    WriteRegDWORD HKLM "${REG_KEY}" "NoRepair" 1
    ${If} $RestartMode == 11
        Exec '"$INSTDIR\Mio.exe"'
    ${ElseIf} $RestartMode == 10
        Exec '"$INSTDIR\Mio.Host.exe" --no-ui'
    ${EndIf}
SectionEnd

Section "Uninstall"
    Call un.CheckInstallDirectory
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File "Stop-ForUpgrade.ps1"
    SetOutPath "$WINDIR"
    ${DisableX64FSRedirection}
    nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Stop-ForUpgrade.ps1" -Directory "$INSTDIR"'
    Pop $0
    Pop $1
    ${EnableX64FSRedirection}
    ${If} $0 != 0
    ${AndIf} $0 != 10
    ${AndIf} $0 != 11
        MessageBox MB_ICONSTOP "MIO 未正常退出，卸载已停止。$\r$\n$1" /SD IDOK
        SetErrorLevel 2
        Abort
    ${EndIf}
    !include "build/uncheck-files.nsh"
    SetOutPath "$PLUGINSDIR"
    File "Confirm-FirewallCleanup.ps1"
    SetOutPath "$WINDIR"
    StrCpy $2 ""
    IfSilent 0 +2
    StrCpy $2 "-Silent"
    ${DisableX64FSRedirection}
    nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Confirm-FirewallCleanup.ps1" -Directory "$INSTDIR" $2'
    Pop $0
    Pop $1
    ${EnableX64FSRedirection}
    ${If} $0 != 0
        ${If} $0 != 3
            MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "防火墙检查无法完成，继续卸载将保留未清理规则。$\r$\n$1" /SD IDOK IDOK firewall_done
        ${EndIf}
        SetErrorLevel 2
        Abort
    ${EndIf}
    firewall_done:
    ${If} $DeleteOwnedRoutes == ${BST_CHECKED}
        SetOutPath "$PLUGINSDIR"
        File "Cleanup-OwnedRoutes.ps1"
        SetOutPath "$WINDIR"
        ${DisableX64FSRedirection}
        nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\Cleanup-OwnedRoutes.ps1" -Directory "$INSTDIR"'
        Pop $0
        Pop $1
        ${EnableX64FSRedirection}
        ${If} $0 != 0
            ${If} $0 != 3
                MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "路由清理无法完成，是否保留规则并继续卸载？$\r$\n$1" /SD IDOK IDOK route_cleanup_done
            ${EndIf}
            SetErrorLevel 2
            Abort
        ${EndIf}
    ${EndIf}
    route_cleanup_done:
    ${If} $DeleteUserData == ${BST_CHECKED}
        InitPluginsDir
        SetOutPath "$PLUGINSDIR"
        File "Remove-UserData.ps1"
        SetOutPath "$WINDIR"
        ${DisableX64FSRedirection}
        nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\Remove-UserData.ps1" -Directory "$INSTDIR"'
        Pop $0
        Pop $1
        ${EnableX64FSRedirection}
        ${If} $0 != 0
            MessageBox MB_ICONSTOP "安装目录未能完全删除：$\r$\n$1" /SD IDOK
            SetErrorLevel 2
            Abort
        ${EndIf}
    ${EndIf}
    !include "build/uninstall-files.nsh"
    Delete "$INSTDIR\Uninstall.exe"
    Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
    RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
    Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
    DeleteRegKey HKLM "${REG_KEY}"
    ; Full removal is explicit; otherwise preserve user data as before.
    RMDir "$INSTDIR"
SectionEnd
