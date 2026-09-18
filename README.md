# ets2pilot

欧洲卡车模拟 2 的自动驾驶程序。它直接读取屏幕导航，将卡车行驶到目标地点。

## 限制

- 只支持 Scania S High Roof，并且需要锁定特定的视角
- 无法泊车
- 没有在 DLC 地图上进行训练，DLC 地图的城区很可能需要大量接管
- 只支持 Windows 11 24H2 及以上，需要 NVIDIA 显卡

## 依赖

- 使用 [vJoy](https://github.com/BrunnerInnovation/vJoy/releases) 虚拟手柄操作游戏
- 依赖 [scs-sdk-plugin](https://github.com/RenCloud/scs-sdk-plugin/releases) 获取自车状态。把 `scs-telemetry.dll` 放到 `<游戏目录>\bin\win_x64\plugins`
- 依赖 [NVIDIA 驱动](https://www.nvidia.com/drivers) 580 及更新的版本(终端运行 `nvidia-smi` 检查版本)

## 下载

在 [Releases](https://github.com/ets2pilot/ets2pilot/releases/latest) 页面下载压缩包，解压后运行 `ets2pilot-gui.exe`。

| 压缩包 | 内容 |
| --- | --- |
| `ets2pilot-gui-<版本>-win-x64.zip` | 图形界面 `ets2pilot-gui.exe` |
| `ets2pilot-gui-cli-<版本>-win-x64.zip` | 图形界面 `ets2pilot-gui.exe` 和命令行 `ets2pilot.exe` |

## 游戏设置

画面设置:

https://github.com/user-attachments/assets/fbe87353-ae7f-4ba1-a16d-2eab3149f950

以及需要将输入类型设置为 vJoy
