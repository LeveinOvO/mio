# Mio 安装包构建

版本来自项目根目录 `VERSION`。先按照主 README 完成应用发布，再在 WSL 执行：

```bash
python3 Installer/build.py --check-only
python3 Installer/build.py
```

需要 Python 3.11+ 和 NSIS 3.11。
输入：`artifacts/build/V<版本>/`。
输出：`artifacts/releases/V<版本>/Mio-Setup-<版本>.exe`、SHA-256 和文件清单。

`--test` 使用独立的验证注册项和快捷方式名称，输出到 `artifacts/validation`。
安装器不打包 data、日志、PDB 或临时文件，语言目录只保留 en/zh。

卸载可选删除整个安装目录、清理 Mio 新建且有快照的路由。未清理的防火墙/路由逐项列出，可保留并继续。
安装和卸载测试须在隔离 Windows 环境执行，不能在开发者的生产网络自动测试。
