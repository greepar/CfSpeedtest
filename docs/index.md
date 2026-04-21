---
layout: home

hero:
  name: CfSpeedtest
  text: Cloudflare IP 测速汇总系统
  tagline: 服务端聚合多客户端测速结果，按运营商筛选最优 IP，并支持 DNS 更新、白名单和客户端更新。
  actions:
    - theme: brand
      text: 开始阅读
      link: /guide/intro
    - theme: alt
      text: Cloudflare Pages 部署
      link: /deploy/cloudflare-pages

features:
  - title: 多运营商分池
    details: 电信、联通、移动独立 IP 池、独立测速结果、独立 DNS 更新。
  - title: 服务端统一轮次
    details: 服务端控制统一轮询时间点，客户端按统一开始时间执行测速并回传结果。
  - title: 可视化 WebUI
    details: 提供概览、测速记录、客户端、IP 池、DNS 更新、配置等完整管理界面。
  - title: 白名单与更新
    details: 支持严格白名单 clientId、心跳保活、客户端启动参数生成以及分平台更新包投放。
---

CfSpeedtest 由三个 .NET 10 项目组成：

- `CfSpeedtest.Server`：服务端和 WebUI
- `CfSpeedtest.Client`：NativeAOT 客户端
- `CfSpeedtest.Shared`：共享模型与 JSON 上下文

如果你要快速上手，建议按以下顺序阅读：

1. `项目简介`
2. `快速开始`
3. `WebUI 概览`
4. `客户端使用`
5. `白名单与客户端管理`
