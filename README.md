# CSNZ Launcher

CSO（Counter-Strike Online）私服用的小启动器：填服务器地址和账号，一键进游戏。

WPF / .NET 10，无边框深色界面，单文件发布，不依赖管理员权限。

![启动器](docs/screenshot.png)

## 功能

- 启动时把 `-ip` `-port` `-login` `-password`（可选 `-disableauthui`）传给 `Bin\\CSOLauncher.exe`
- 每秒轮询服务器连通性，保留手动「检测」按钮
- 配置持久化到 exe 同目录的 `launcher.json`（明文）
- 「修复 DPI」：启动前为游戏客户端写入 `~ HIGHDPIAWARE` 兼容性标记，解决非 100% 缩放下发虚
- 从启动器目录逐级向上自动查找客户端

## 目录结构

```
src/CSNZ.Launcher/    WPF 工程：界面、图标、逻辑
docs/                 截图与设计稿
.github/workflows/    CI：构建，推 tag 时发布 Release
```

## 构建

```powershell
dotnet build src/CSNZ.Launcher/CSNZ.Launcher.csproj -c Release
dotnet publish src/CSNZ.Launcher/CSNZ.Launcher.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

产物是框架依赖的单文件 exe，运行时需要 .NET 10 Desktop Runtime。
把 `CSNZ.Launcher.exe` 放到游戏根目录（与 `Bin` 同级）即可使用。

## 自动发布

推 `v` 开头的 tag 会触发 GitHub Actions：构建 → 上传产物 → 创建 Release。

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## 配置文件

`launcher.json` 与 exe 同目录，删掉即可恢复默认：

```json
{
  "ip": "127.0.0.1",
  "port": 30002,
  "login": "",
  "password": "",
  "disableAuthUi": false,
  "fixDpi": true
}
```

## 设计稿

`docs/design-preview.html` 是最初的四套方案对比页（A 方案已落地），浏览器直接打开即可。
