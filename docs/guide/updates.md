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
CfSpeedtest.Client-win-x64-1.0.1.zip
CfSpeedtest.Client-linux-x64-1.0.1.zip
CfSpeedtest.Client-linux-musl-x64-1.0.1.zip
```

## 客户端更新行为

客户端启动时会：

1. 上报当前版本号和当前平台
2. 服务端返回该平台是否有新版本
3. 若指定了 `--auto-update`，客户端会自动下载更新包

当前实现是：

- 自动检查
- 自动下载更新包
- 提示手动替换当前客户端文件

## WebUI 可看到的信息

- 当前投放目录
- 命名规则
- 各平台更新包状态
- 文件名
- 最后修改时间
