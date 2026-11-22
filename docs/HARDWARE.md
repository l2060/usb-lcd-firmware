# 硬件资料 / Hardware Reference

本目录收集 MSU2 LITE 屏（CH32X033F8P6 + GC9307 320×172 面板）的硬件资料。

## 目录

```
docs/
├─ datasheet/
│   ├─ wch_ds.pdf              WCH 芯片数据手册
│   ├─ riscv-board.pdf         板卡硬件说明
│   └─ MSU2-dev-guide.pdf      原厂开发指南
├─ schematic/
│   ├─ sch_mcu_right.png       MCU 侧原理图（右侧）
│   └─ sch_u3.png              U3 器件原理图
└─ images/
    ├─ factory_flash_8MB.bin   原厂外部 Flash 完整备份（W25Q64 8MB）
    ├─ vendor_app.bin          原厂 APP 固件
    ├─ boot_background.png     开机图参考
    └─ dashboard_reference.png 仪表盘布局参考
```

## 核心器件

| 器件 | 型号 | 关键参数 |
|---|---|---|
| MCU | CH32X033F8P6 | RISC-V RV32IMAC @48MHz，62KB Flash，20KB SRAM，USBFS |
| 显示驱动 | GC9307 | 172×320 IPS，4-wire SPI，RGB565/666 |
| 外部 Flash | W25Q64 | 8MB SPI NOR，4KB 扇区 |
| 可见区域 | 1.47" | 320×172（横屏使用） |

## 引脚分配（来自固件，已实测）

| 功能 | 引脚 | 说明 |
|---|---|---|
| LCD SCK | PA5 | SPI1 SCK |
| LCD MOSI | PA7 | SPI1 MOSI |
| LCD DC / Flash MISO | **PA6** | 复用！切 Flash 时须改方向 |
| LCD RST | PA0 | 复位（低有效） |
| LCD CS | PC3 | 片选（低有效） |
| Flash CS | PB7 | 片选（低有效） |
| USB D+ / D- | — | CH32X035 USBFS，CDC 虚拟串口 |

**注意**：PA6 同时用于 LCD 的 DC 与 Flash 的 MISO，任何读 Flash 的操作都必须先把 LCD CS 拉高，
否则会中断正在进行的窗口写入（这是最容易踩的坑）。

## 内部 Flash 分区（62KB）

| 地址 | 用途 | 大小 |
|---|---|---|
| 0x0000–0x2FFF | Bootloader | 12 KB |
| 0x3000 | 升级标志 + 屏幕方向（第 4/5 字节） | 256 B |
| 0x3100 | 元数据（APP 长度 / CRC） | 256 B |
| 0x3200–0x91FF | 开机图（RLE） | 24 KB |
| 0x9200–0xF6FF | APP | 25.8 KB |
| 0xF700 | 预留（自检暂存） | 256 B |

## 外部 Flash 分区（8MB）

| 地址 | 用途 | 大小 |
|---|---|---|
| 0x020000–0x03FFFF | APP 背景图（RGB565 RLE） | 128 KB |
| 0x040000–0x0FFFFF | 字库（.afont 多字面） | 768 KB |
| 其余 | 空闲（可用于日志/资源） | ~7 MB |

## 还缺的资料（待补充）

| 项目 | 重要性 | 说明 |
|---|---|---|
| **GC9307 数据手册** | 高 | 目前初始化序列是照厂商示例改的，缺少寄存器全表 |
| **面板模组规格书** | 高 | 型号、视角、亮度、色域、机械尺寸、FPC 定义 |
| **完整原理图 PDF** | 高 | 现有只有两张局部截图，缺整页与网络标号 |
| **PCB 布局 / Gerber** | 中 | 排查 EMI、走线阻抗、去耦位置时需要 |
| **机械图（STEP/DWG）** | 中 | 外壳设计、开孔尺寸、视窗公差 |
| **背光驱动参数** | 中 | LED 串并数、限流电阻、PWM 调光是否可用 |
| **W25Q64 数据手册** | 低 | 常见器件，时序已知 |
| **USB 接口与 ESD 保护** | 低 | 连接器型号、保护器件 |
| **测试点 / 烧录接口定义** | 中 | SWD 引脚位置，便于工厂烧录 |
| **功耗实测数据** | 低 | 背光全亮/熄灭、待机电流 |

## 版本

- 资料整理：2026-11-22
- 固件：APP v104 / BL v67
