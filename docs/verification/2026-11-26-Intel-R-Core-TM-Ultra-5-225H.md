# 验证矩阵报告 —— Microsoft Windows 11 家庭版 中文版 10.0.26300

- 采集时间：2026-11-26 04:55:46
- 机器名：FLY-NB  型号：21TK  厂商：LENOVO
- CPU：Intel(R) Core(TM) Ultra 5 225H（厂商：GenuineIntel，逻辑核：14，物理核：14）
- 显卡：GameViewer Virtual Display Adapter / Intel(R) Arc(TM) 130T GPU (16GB)
- 电池：有（本机是移动平台）
- 主板：LENOVO LNVNB161216
- 内存条：2 条
- 管理员权限：无（MSR 温度多半读不到，属正常）

## 本机覆盖了矩阵的哪几格

F1（AMD 平台）—— **未覆盖**：本机是 Intel(R) Core(TM) Ultra 5 225H
F2（Intel 桌面平台）—— **未覆盖**：本机有电池（移动平台）；桌面平台的 SuperIO 温度路径需在台式机上另跑一次
F3（核显 + 独显组合）—— **未覆盖**：本机显卡为 GameViewer Virtual Display Adapter / Intel(R) Arc(TM) 130T GPU (16GB)

## 探针原始输出

使用 dotnet：`D:\YQDESK\.dotnet\dotnet.exe`

### lhm-probe（CPU 温度传感器）

```
管理员权限：无（读 MSR 温度多半读不到）
CPU 硬件：Intel Core Ultra 5 225H
  Core Max                           = (空)
  Core Average                       = (空)
  P-Core #1                          = (空)
  P-Core #2                          = (空)
  E-Core #1                          = (空)
  E-Core #2                          = (空)
  E-Core #3                          = (空)
  E-Core #4                          = (空)
  E-Core #5                          = (空)
  E-Core #6                          = (空)
  E-Core #7                          = (空)
  E-Core #8                          = (空)
  P-Core #3                          = (空)
  P-Core #4                          = (空)
  E-Core #9                          = (空)
  E-Core #10                         = (空)
  CPU Package                        = (空)
  P-Core #1 Distance to TjMax        = (空)
  P-Core #2 Distance to TjMax        = (空)
  E-Core #1 Distance to TjMax        = (空)
  E-Core #2 Distance to TjMax        = (空)
  E-Core #3 Distance to TjMax        = (空)
  E-Core #4 Distance to TjMax        = (空)
  E-Core #5 Distance to TjMax        = (空)
  E-Core #6 Distance to TjMax        = (空)
  E-Core #7 Distance to TjMax        = (空)
  E-Core #8 Distance to TjMax        = (空)
  P-Core #3 Distance to TjMax        = (空)
  P-Core #4 Distance to TjMax        = (空)
  E-Core #9 Distance to TjMax        = (空)
  E-Core #10 Distance to TjMax       = (空)
=> 选中：无（读数未知，界面显示 --）

```

### hid-probe e2e（2.4G 接收器）

```
[log] HID 外设电量：联想 Idea5003 2.4G 接收器 58%
--- 快照（1 台）---
  名称=联想 Idea5003 2.4G 接收器 类型=other 电量=58% 档位=- 充电=False 观测于=04:55:49

```
## 结论（诚实版）

- 上面「覆盖了哪几格」里标 **可覆盖** 的格子，表示本机**具备验证条件**，不代表已经验证通过 ——
  具体结论要读探针原始输出：输出里读不到的项就是**读不到**，产品应当显示「未知/需管理员/缺驱动」。
- 标 **未覆盖** 的格子，需要在对应类型的另一台机器上再跑一次本脚本。
- 本脚本不修改产品代码，只做采集与报告。

## 下一步（按优先级）

1. 把本报告连同探针输出一起归档（docs\verification\ 下按日期命名）。
2. 在缺的机型上重复本步骤：AMD 机 / Intel 台式机 / 核显+独显机 / 换一款 2.4G 接收器。
