<div align="center">

# 逐字歌词

**ONLC — Onire Lyric Checker**

全能歌词匹配工具

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2B-0078D4.svg)](https://github.com/Oniremisce/ONLC/releases)
[![Framework](https://img.shields.io/badge/Framework-WinUI%203-5C2D91.svg)](https://learn.microsoft.com/windows/apps/winui/)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

一个面向 Windows 桌面的歌词工具：搜索、匹配、写入、逐字打词、时间轴校准，一站式完成。

[下载最新版本](https://github.com/Oniremisce/ONLC/releases/latest) · [问题反馈](https://github.com/Oniremisce/ONLC/issues/new)

</div>

---

## ✨ 功能总览

### 🎵 歌曲库管理

- **读取文件夹**：一键扫描目录下全部常见音频格式，也支持直接把文件夹 / 音乐文件**拖拽**进列表
- **歌曲信息编辑**：读取并修改歌名、歌手、专辑标签，一键写入
- **写入状态管理**：已写入的歌曲序号自动标绿，列表按「失败 → 无匹配 → 待处理 → 已写入」自动排序
- **已处理归档**：写入完成的歌曲自动移动到 `已处理` 文件夹，或在设置中指定自定义保存路径

### 🎧 内置播放器

- 基于 Windows Media Foundation 的本地播放，支持 **0.7 倍速变速不变调**（校准时间轴利器）
- KTV 式**逐字染色歌词**渲染，支持翻译行、罗马音 / 发音行同步展示
- 自动读取并显示歌曲内嵌封面，进度条点击 / 拖动即点即播

### 🔍 匹配歌词（单曲）

- 聚合 **网易云音乐 / QQ 音乐 / 酷狗音乐 / LRCLIB** 四大平台搜索
- 候选列表**流式加载**：搜到一个加一个，附匹配度评分、时长匹配、逐字 / 逐行类型徽章
- 内置试听、写入歌词、写入歌词＋标签、下载封面写入

### 🔁 全部替换（批量）

- **网络歌词**：5 线程并发批量处理整个曲库，自动严格匹配（评分 100% ＋ 时长校验，逐字歌词优先）
- **本地歌词**：按文件名匹配指定文件夹内的 `.lrc` 文件批量写入

### ✍️ 手工打词（逐字歌词制作）

- **听打逐字**：播放状态下按 `空格` / `回车`（或点击打词按钮），为每个字自动打上 `[逐行]` ＋ `<逐字>` 双时间戳，即点即录
- **波形图导航**：全曲波形一目了然，点击 / 拖动从任意位置播放
- **时间轴微调**：拖动滑杆整体平移歌词时间戳（±5 秒），垂直基准线实时联动滚动歌词，边看边对轴
- **滚动歌词预览**：开关切换，实时渲染你打出的逐字歌词

## 📀 支持格式

| 音频 | 歌词 |
|---|---|
| mp3 / flac / wav / m4a / aac / ogg / wma / ape / opus | LRC、增强 LRC（翻译＋发音）、QRC、KRC、yrc |

歌词以 **ID3v2 USLT** 等标准帧内嵌进音频文件，主流播放器均可读取。

## 🚀 快速开始

1. 前往 [Releases](https://github.com/Oniremisce/ONLC/releases/latest) 下载最新版本
2. 解压后运行 `LyricsApp.WinUI.exe`（Windows 10 1809 及以上）
3. 点击「读取文件夹」或拖入歌曲，开始使用

### 从源码构建

```bash
git clone https://github.com/Oniremisce/ONLC.git
dotnet build WinUIApp/LyricsApp.WinUI.csproj -c Release -p:Platform=x64
```

需要 .NET 8 SDK 与 Windows App SDK 1.6（构建时 NuGet 自动还原）。

## 🛠️ 技术栈

[WinUI 3](https://learn.microsoft.com/windows/apps/winui/) · [Win2D](https://github.com/microsoft/Win2D)（逐字染色 / 波形渲染）· Media Foundation（播放 / 变速）· [TagLib#](https://github.com/mono/taglib-sharp)（标签与歌词内嵌）· [NAudio](https://github.com/naudio/NAudio)（波形解码）

第三方组件与许可证详见 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)。

## 📄 许可证

本项目基于 [MIT License](LICENSE) 开源发布。

Copyright © 2026 Oniremisce
