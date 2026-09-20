# MediaScribe Windows 安装器

`installer.iss` 使用 Inno Setup 6 生成 Windows x64 安装包。首次交互式安装时，“在桌面创建快捷方式（推荐）”默认勾选，用户可在安装向导中取消。

静默安装时可显式控制名为 `desktopicon` 的任务：

```powershell
# 创建桌面快捷方式
.\MediaScribe-Setup-x64.exe /VERYSILENT /TASKS=desktopicon

# 不创建桌面快捷方式
.\MediaScribe-Setup-x64.exe /VERYSILENT /MERGETASKS=!desktopicon
```

卸载程序会同步删除由安装器创建的桌面快捷方式。
