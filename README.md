# Mio

Windows 网络工具箱：TCP/UDP 端口转发、IPv4 静态路由与防火墙规则管理。

当前发行版本：**2.0.1**。

[下载 2.0.1](https://github.com/LeveinOvO/mio/releases/tag/v2.0.1) · [反馈问题](https://github.com/LeveinOvO/mio/issues)

## 安装与使用

下载发行附件 `Mio-Setup-2.0.1.exe`，核对同目录 SHA-256 后运行安装器。
安装完成后通过开始菜单、桌面快捷方式或 `Mio.exe` 启动。
安装器及应用需要管理员权限。升级时正常退出目标安装目录中的程序，更新后恢复原运行状态。

## 功能

- IPv4 TCP/UDP 转发，目标支持 IPv4 或域名；使用 Realm 转发核心。
- 原生 C++ 宿主常驻托盘，WinUI 配置界面按需打开。
- DNS 定期检查，本机转发回环检测，监听冲突检测。
- 防火墙规则限定监听端口、协议、来源 CIDR 和网络配置文件。
- IPv4 路由增删改、临时/持久路由、静态项优先排序和筛选。
- 根据下一跳自动匹配接口；多接口歧义、直连或特殊网络可手动指定。
- 路由操作审计、过期快照拒绝及未完成操作写入阻止。

## 系统要求与验证范围

- Windows x64 桌面环境；安装器要求 build 19044 或更新。该门槛不等于所有对应系统均已验收。
- Windows PowerShell 5.1 x64、NetTCPIP 模块和对应 CIM 提供程序。
- Microsoft Visual C++ x64 运行库（Realm 依赖）；安装器缺失时提示，不自动下载。
- Windows 防火墙 BFE/MpsSvc 服务运行，相关系统策略允许本地管理。
- .NET 与 Windows App SDK 随应用自包含分发，不需要用户安装开发 SDK。

实际集成测试包括 Windows Server 2022 Desktop Experience。其他 Windows 版本、域策略、WDAC/AppLocker、Server Core/ARM64 不应视为已验证。
Windows 路由、防火墙与应用数据库不是单一事务；失败可能需要查看日志并核对系统现状。
本机回环检测不保证识别远端设备或 NAT 反射形成的循环。

## 数据与卸载

- 工作空间为安装目录 `data`，数据库为 `data/Mio.db`，日志为 `data/logs`。
- 新发行包不含开发者数据库、日志或测试机信息。
- 卸载默认保留数据，可勾选删除整个安装目录。
- 防火墙清理仅针对数据库记录且快照匹配的规则。未清理项列出详情，允许保留并继续。
- 可单独选择删除 Mio 创建且有归属记录的系统路由；不接管历史或外部路由。
- 不安装后台 Windows 服务，不添加系统自启动。

## 源码结构

```text
Mio.csproj           WinUI 主程序
Core/                模型与校验
Windows/             宿主通信及 Windows 适配
NativeHost/          C++ 宿主、SQLite、Realm 生命周期、路由模块
Installer/           NSIS 安装器
Tests/               回归测试
vendor/              固定版本的 Realm 及现有许可文件
VERSION              统一发行版本来源
```

部分内部 C# 命名空间仍为 `PortForwarder`；这只是实现命名，不包含旧品牌数据迁移功能。

## 构建

构建环境：Windows .NET SDK（版本见 `global.json`）、Debian/WSL、Python 3.11+、llvm-mingw、NSIS 3.11。
先设置 `LLVM_MINGW` 为解压后的工具链绝对路径，然后在 WSL 项目根目录运行：

```bash
export LLVM_MINGW=/path/to/llvm-mingw
bash scripts/build-release.sh
python3 Installer/build.py
```

`DOTNET_EXE` 可指定 Windows dotnet.exe 路径；默认使用 `/mnt/c/Program Files/dotnet/dotnet.exe`。
构建输出：`artifacts/build/V2.0.1/`；安装包：`artifacts/releases/V2.0.1/`。

默认回归测试（Windows 项目根目录）：

```powershell
dotnet run --project Tests/Mio.Tests.csproj
```

默认测试不修改系统路由或防火墙。真实网络修改测试只能在明确指定的隔离测试机进行；本地客机运维脚本不纳入公开仓库。

## 项目许可状态

项目自身许可证尚未确定，当前不授予额外的开源复用许可。现有第三方组件的原始许可证仍保留且独立适用。
安装包未代码签名。
