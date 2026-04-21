import { defineConfig } from 'vitepress'

export default defineConfig({
  lang: 'zh-CN',
  title: 'CfSpeedtest 文档',
  description: 'Cloudflare IP 测速汇总系统文档，支持服务端管理、客户端测速、DNS 更新、白名单与客户端更新。',
  cleanUrls: true,
  themeConfig: {
    nav: [
      { text: '指南', link: '/guide/intro' },
      { text: '部署', link: '/deploy/cloudflare-pages' },
      { text: 'GitHub', link: 'https://github.com/greepar/CfSpeedtest' }
    ],
    sidebar: [
      {
        text: '开始',
        items: [
          { text: '项目简介', link: '/guide/intro' },
          { text: '快速开始', link: '/guide/quick-start' },
          { text: 'WebUI 概览', link: '/guide/webui' }
        ]
      },
      {
        text: '核心功能',
        items: [
          { text: '客户端使用', link: '/guide/client' },
          { text: 'IP 池与拉取源', link: '/guide/ip-pool' },
          { text: 'DNS 更新', link: '/guide/dns' },
          { text: '白名单与客户端管理', link: '/guide/whitelist' },
          { text: '客户端更新', link: '/guide/updates' }
        ]
      },
      {
        text: '部署',
        items: [
          { text: 'Cloudflare Pages', link: '/deploy/cloudflare-pages' }
        ]
      }
    ],
    socialLinks: [
      { icon: 'github', link: 'https://github.com/greepar/CfSpeedtest' }
    ]
  }
})
