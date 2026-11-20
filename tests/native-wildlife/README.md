运行原版服务器中的野生动物回归测试：

```powershell
./tests/native-wildlife/run.ps1 -Vanilla
./tests/native-wildlife/run.ps1
```

`-GamePath` 指向实际游戏安装，`-ModpackPath` 指向要审查的整合包 Mods；默认值对应本项目测试服。`-Vanilla` 只保留原版、本模组及 Terrain Sampler 依赖。完整模式复制整合包到独立目录，替换为当前编译版本。所有存档、配置、日志均在新建的 `artifacts/wildlife-native-*` 中，脚本不会启动或写入正式存档，也不会部署正式 Mods。

测试加载实际原版实体与 AI 配置，指定追击目标并周期性重试原版 `AiTaskSeekEntity`，运行其异步寻路、转向和任务生命周期。无客户端时服务器不自动推进测试实体物理，因此测试通过原版 `EntityBehaviorControlledPhysics` 推进位置，而不是传送熊或直接调用破坏行为。猎物是固定在柱顶的虚拟目标，以便确认剩余悬空平台也被破坏。

断言覆盖三格高柱、四层带屋顶支撑、从五格外接近、贴墙目标、不同土块家族、大小熊体型、原版掉落实体、石块保护、封闭石围墙遮挡及成年狼穿过一格宽高通道。另有只触发一次原版追击的夯土柱场景，验证追击结束后仍能挖掉剩余顶层。逐场景结果写入 `console.txt`；任一断言失败或超过十分钟，脚本以失败退出。这是无界面服务器回归，不代替玩家联机操作测试。

初次生成完整整合包世界较慢。可使用 `-SeedWorldPath artifacts/某个已关闭的测试目录/Saves/某个测试.vcdbs` 复制已生成的测试存档来加速；脚本仅接受本仓库 `artifacts` 下的存档，始终复制到新的运行目录。
