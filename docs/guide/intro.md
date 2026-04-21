# 项目简介

CfSpeedtest 是一个基于 C# / .NET 10 的 Cloudflare IP 测速汇总系统。

系统分为两部分：

- 服务端：分发任务、汇总结果、管理 IP 池、更新 DNS、提供 WebUI
- 客户端：按运营商执行 TCP 和下载测速，将最优结果回传服务端

## 主要特性

- 按运营商拆分 IP 池：`Telecom`、`Unicom`、`Mobile`
- IP 来源支持：手动添加、HTTP API、DoH CNAME
- 服务端统一轮次控制测速开始时间
- 客户端支持 NativeAOT 单文件发布
- 支持最低下载速度阈值和下载测速限速
- 支持 DNS 聚合更新
- 支持严格白名单 clientId
- 支持服务端手动投放客户端更新包

## 项目结构

```text
CfSpeedtest/
├── src/
│   ├── CfSpeedtest.Shared/
│   ├── CfSpeedtest.Server/
│   └── CfSpeedtest.Client/
├── docs/
├── build.bat
├── build.sh
└── README.md
```

## 运行要求

- .NET SDK 10
- Windows 或 Linux
- 若使用已发布 NativeAOT 客户端，客户端机器不需要安装 .NET 运行时
