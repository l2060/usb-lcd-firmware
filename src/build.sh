#!/usr/bin/env bash
# Build Aurora firmware (APP or BL) on the Linux side.
#   ./build.sh app     # -> app/app.hex
#   ./build.sh bl      # -> bl/bl.hex
set -euo pipefail

TARGET="${1:-app}"
CC=riscv64-unknown-elf-gcc
CF="--specs=picolibc.specs -march=rv32imac_zicsr_zifencei -mabi=ilp32 -Os \
-ffunction-sections -fdata-sections -ICore -IPeripheral/inc -IUser -IStartup"

cd "$(dirname "$0")/$TARGET"
rm -f *.o *.elf *.hex

# core + peripherals + user
for f in Core/*.c Peripheral/src/*.c User/*.c; do
  $CC $CF -include stddef.h -c "$f" -o "$(basename "${f%.c}").o"
done
# startup (assembly)
$CC $CF -c Startup/startup_ch32x035.S -o startup.o

$CC $CF -T Ld/Link.ld -nostartfiles -Wl,--gc-sections *.o -o "$TARGET.elf"
riscv64-unknown-elf-size "$TARGET.elf"
riscv64-unknown-elf-objcopy -O ihex "$TARGET.elf" "$TARGET.hex"
echo "built $TARGET/$TARGET.hex"