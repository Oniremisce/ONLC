# 第三方组件声明（THIRD-PARTY NOTICES）

本软件（逐字歌词 / ONLC）使用了以下第三方开源组件，在此一并致谢。各组件版权归其各自作者所有。

## 直接移植的代码

### WXRIW/QQMusicDecoder — MIT License

QQ 音乐 QRC 歌词使用的修改版 3DES（`Core\Web\Providers.cs` 中的 `QrcTripleDes`）移植自该项目 C# 原版实现（DESHelper.cs），结构与常量逐项对应。

- 项目地址：<https://github.com/WXRIW/QQMusicDecoder>
- 许可证：MIT License

> 原始许可声明：
>
> MIT License
>
> Copyright (c) WXRIW
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## 其他致谢

- **LDDC**（https://github.com/chenmozhijin/LDDC，GPL-3.0）：多平台歌词检索与匹配的接口调用思路参考了该项目。
- **NeteaseCloudMusicApi**：网易云音乐游客登录所用的设备 ID 池来自社区项目公开的数据。

## NuGet 依赖

| 组件                               | 版本              | 许可证      | 用途                            |
| -------------------------------- | --------------- | -------- | ----------------------------- |
| Microsoft.WindowsAppSDK          | 1.6.250205002   | MIT      | WinUI 3 应用框架                  |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.1742 | MIT      | Windows SDK 构建工具              |
| Microsoft.Graphics.Win2D         | 1.2.0           | MIT      | 歌词逐字染色 / 波形图渲染                |
| TagLibSharp                      | 2.3.0           | LGPL-2.1 | 音频标签读写（歌词/封面内嵌）               |
| NAudio                           | 2.2.1           | MIT      | 音频波形解码（MediaFoundationReader） |

其中 TagLibSharp 采用 LGPL-2.1 许可证，以 NuGet 包形式动态链接使用，不影响本软件的 MIT 许可；如需修改该库本身，请遵循其原许可证。各依赖组件的完整许可证文本可在对应 NuGet 包或官方仓库中查阅。
