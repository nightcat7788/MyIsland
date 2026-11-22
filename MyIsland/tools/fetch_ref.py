# -*- coding: utf-8 -*-
# 下载 BrimDeck 官方设计图并分析（README 引用 docs/zh-CN/images/）
import urllib.request, os, struct, zlib, collections, io

RAW = "https://raw.githubusercontent.com/MoFeng2223/BrimDeck/main/docs/zh-CN/images/"
OUT = r"C:\Users\fengl\WorkBuddy\2026-11-21-13-46-35\MyIsland\branding\ref"
os.makedirs(OUT, exist_ok=True)

imgs = ["hero.png", "compact-monitor.png", "compact-notch.png", "usage.png", "monitor.png", "devices.png"]
for name in imgs:
    try:
        urllib.request.urlretrieve(RAW + name, os.path.join(OUT, name))
        print(f"下载 {name}: {os.path.getsize(os.path.join(OUT, name))} bytes")
    except Exception as e:
        print(f"失败 {name}: {e}")
