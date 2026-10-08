# 客户端更新

客户端更新采用“服务端手动投放更新包”的方式。

## 服务端如何提供更新

将更新文件手动放入：

```text
client-updates/
```

然后在 WebUI 配置页：

- 启用客户端自动更新
- 填写最新客户端版本号

## 按平台区分更新包

支持以下平台：

- `win-x64`
- `linux-x64`
- `linux-musl-x64`

建议文件命名：

```text
cfspeedtest-client-win-x64.zip
cfspeedtest-client-linux-x64.zip
cfspeedtest-client-linux-musl-x64.zip
```

## 客户端更新行为

客户端启动时会：

1. 上报当前版本号和当前平台
2. 服务端返回该平台是否有新版本
3. 默认自动下载并安装更新包；`--disable-auto-update` 可禁用自动安装

当前实现是：

- 自动检查
- 自动下载更新包
- 替换当前实际运行的程序，并重启；服务或容器部署由管理器重新拉起

## WebUI 可看到的信息

- 当前投放目录
- 命名规则
- 各平台更新包状态
- 文件名
- 最后修改时间

## 容器内更新

服务端和客户端均支持在容器内更新 NativeAOT 程序。服务端按 WebUI 自动更新配置执行，客户端默认允许更新，并受服务端客户端更新开关控制；设置 `CF_DISABLE_AUTO_UPDATE: "1"` 可禁用客户端自更新。

更新后程序退出，部署时需使用 `restart: unless-stopped`（直接命令为 `--restart unless-stopped`），让容器运行时重新启动更新后的程序。更新保存在容器可写层，删除或重建容器后恢复为镜像版本；数据卷中的配置和历史记录继续保留。
