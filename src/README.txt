firmware/src — 完整固件工程（CH32X033F8P6）

目录
  app\    APP 工程
            User\main.c              应用主逻辑（帧缓冲/字库/图元/协议）
            User\ch32x035_usbfs_device.c  USB CDC 设备驱动
            User\usb_desc.c / UART.c / debug.c / system_ch32x035.c / ch32x035_it.c
            Core\                    RISC-V 内核支持
            Peripheral\inc,src\      CH32X035 外设库
            Startup\startup_ch32x035.S
            Ld\Link.ld               链接脚本（ORIGIN = 0x00009200）
  bl\     Bootloader 工程（结构同上，ORIGIN = 0x00000000）
  build.sh                          一键编译脚本（Linux 侧）

编译（在 Linux 上，需要 riscv64-unknown-elf-gcc 14.2 + picolibc）
  ./build.sh app      # 产出 app/app.hex
  ./build.sh bl       # 产出 bl/bl.hex

Windows 侧的做法
  把整个 app\ 或 bl\ 目录上传到服务器（例如 /root/app），
  然后执行同样的编译命令（脚本里的 CF 变量就是完整参数）。

Flash 布局
  0x0000-0x2FFF  Bootloader（12KB）
  0x3000        升级标志页（256B）
  0x3100        元数据页（256B）
  0x3200-0x91FF 开机图（24KB）
  0x9200-0xF6FF APP（25.8KB）
  外部 Flash（W25Q64 8MB）
  0x020000-0x03FFFF  APP 背景（128KB）
  0x040000-0x0FFFFF  字库（768KB）

注意
  * LCD 窗口打开期间（CS 低）禁止读取外部 Flash。
  * 屏幕方向存放于 0x3000 页第 4/5 字节，断电保持。
