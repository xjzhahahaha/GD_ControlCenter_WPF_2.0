; =========================================================================
; GD Control Center - Inno Setup 自动化打包脚本
; =========================================================================

; 1. 基础信息定义 (你可以直接在这里修改软件名称、版本号等)
#define MyAppName "GD Control Center"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Your Lab/Company Name"
#define MyAppExeName "GD_ControlCenter_WPF.exe"

; 这里使用了【相对路径】。因为这个脚本文件在项目根目录，所以可以直接指向 bin 里的 publish 文件夹
#define MyPublishFolder "bin\Release\net6.0-windows\publish"

[Setup]
; 唯一标识符。如果是新项目，建议在 Inno Setup 中点击 Tools -> Generate GUID 替换这一行
AppId={{8A8C7A3C-4D2A-4B6A-9E8F-1B2C3D4E5F6A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}

; 默认安装路径 (C:\Program Files\GD Control Center)
DefaultDirName={autopf}\{#MyAppName}

; 开始菜单中的文件夹名称
DefaultGroupName={#MyAppName}

; ★ 安装包输出路径 (打包好的 exe 会放在项目根目录下的 InstallerOutput 文件夹里)
OutputDir=InstallerOutput
; ★ 安装包文件名
OutputBaseFilename=GD_ControlCenter_Setup_v{#MyAppVersion}

; 极限压缩，让安装包体积尽可能小
Compression=lzma2/ultra64
SolidCompression=yes
; 需要管理员权限安装
PrivilegesRequired=admin

[Tasks]
; 提供“创建桌面快捷方式”的选项
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 打包主执行文件
Source: "{#MyPublishFolder}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; 打包 publish 文件夹下的所有其他依赖文件和子文件夹
Source: "{#MyPublishFolder}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单快捷方式
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; 卸载快捷方式
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
; 桌面快捷方式
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 安装完成后询问是否立刻运行
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
