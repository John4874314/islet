; ============================================================
;  IExpress 自解压安装包配置
;  由 build-installer.ps1 生成，路径为占位符，脚本会自动替换
; ============================================================

[Version]
Class=IEXPRESS
SEDVersion=3

[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=浮屿 Islet 安装完成。
TargetName=__TARGET_NAME__
FriendlyName=浮屿 Islet
AppLaunched=install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles

[Strings]
FILE0="Islet.exe"
FILE1="Islet.Core.dll"
FILE2="Islet.exe.config"
FILE3="install.cmd"
FILE4="uninstall.cmd"

[SourceFiles]
SourceFiles0=__SOURCE_DIR__

[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
%FILE4%=
