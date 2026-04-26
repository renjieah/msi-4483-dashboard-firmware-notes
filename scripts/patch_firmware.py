"""
MSI Dashboard 固件 Patch 工具
修复两个持久性 Bug：
  Bug 1 — g_i32NVTStage0 == 21010100 导致的救援 loading 无限循环
  Bug 2 — User Screen 与 System Clock 双 slot 同时激活导致界面混乱闪烁

====================================================================
Bug 1 根本原因：
  sub_FC930 主显示循环中（地址 0xfccd0），当 stage==1 时将 g_i32NVTStage0
  与常量 21010100（0x014096B4，NAND FAT 初始出厂版本号）比较。相等时打印
  "disable touch & jump to loading mode!" 并进入救援 loading 无限循环。
  MSI Center 定期推送 NAND 内容更新，若捆绑的 HMI_FW.ini 版本为 21010100，
  设备读取后立即触发此逻辑，卡在 loading.mp4，重启无效。

Bug 1 修复方法：
  将常量池 0xfcff4 处的触发值 0x014096B4 改为 0xFFFFFFFF，
  使比较永远为 false，救援 loading 永不触发。

====================================================================
Bug 2 根本原因：
  主循环 FCDD8"始终运行"节中，sub_8AE28（0x8afcc）与 sub_8B450（0x8b5b8）
  在将 byte_124E1C（slot 8 / System Clock enable）写 1 时，没有先将
  byte_124E5C（slot 9 / User Screen enable）清零。

  触发时序：
    1. User Screen 激活（byte_124E5C=1，此时 n90=0x5A 即 90° 转向，
       sub_8B450 的 n90==0 判断未通过，User Screen 正常运行）
    2. MSI Center 定期推送新 NAND 内容包，其中 unk_124F24 被更新为
       n90=0（0° 横屏），每帧各显示函数读取 unk_124F24 后将 n90 重置为 0
    3. 下一次外层循环进入 FCDD8 节：sub_8B450 见 n90==0，
       unk_45E838 中无"55AARESTORE5AA5"标记 → 直接写 byte_124E1C=1
       但 byte_124E5C 仍为 1 → slot 8 与 slot 9 同时 enable
    4. 外层循环同时执行 Block 2（System Clock）内层循环 与
       Block 3（User Screen）内层循环，两路渲染争用同一帧缓冲
    5. 帧缓冲状态被两路代码交替覆盖 → 界面混乱/叠加/闪烁
    6. 此状态若被 sub_D307C/sub_5EE34 写入 NAND TAG1，重启后恢复
       相同脏状态 → 重启无效，仅重刷 NAND 可恢复

  具体双 slot 触发点（字节级别）：
    · sub_8B450 @ 0x8b5b0：STRB R0,[R1,#0x217]（清 slot8.screen[12]，R0=0）
      本应清 byte_124E5C=0 但未做，是遗漏的前置清零步骤。
    · sub_8AE28 @ 0x8afc4：同上（带 slot3 gate 条件，概率较低但同理）

Bug 2 修复方法：
  将上述两处 STRB 指令的立即数偏移由 0x217（slot8.screen[12]）
  改为 0x248（byte_124E5C / slot9 enable），使 R0=0 的清零操作
  目标变为"在激活 slot 8 前先清 slot 9 enable"。
  每处仅改 1 字节（指令低字节）：0x17 → 0x48。

  对 slot8.screen[12]（byte_124E2B）的影响：
    byte_124E2B 只由上述两处代码清零，无任何代码将其置 1，
    因此跳过清零后其值仍为 0，System Clock 显示不受影响。

运行：
  python patch_firmware.py

依赖：无（仅标准库）
"""

import os
import argparse
from pathlib import Path
import shutil
import struct
import logging

logging.basicConfig(level=logging.INFO, format='%(levelname)s: %(message)s',
                    encoding='utf-8')
log = logging.getLogger(__name__)

DEFAULT_FIRMWARE_NAME = 'conprog_4BB6_22100300.bin'
DEFAULT_PATCHED_NAME = 'conprog_4BB6_22100300_patched.bin'

# ── Patch 定义列表（每项：偏移, 原始字节序列, 修改后字节序列, 说明）──────────
PATCHES = [
    {
        'name':    'Bug1 — 救援 loading 版本号比较',
        'offset':  0xfcff4,
        'orig':    bytes.fromhex('B4964001'),   # 0x014096B4 LE — 出厂版本号
        'patched': bytes.fromhex('FFFFFFFF'),   # 0xFFFFFFFF — 永不匹配
        'note':    '常量池触发值 0x014096B4→0xFFFFFFFF，救援 loading 不再触发',
    },
    {
        'name':    'Bug2 — sub_8AE28 激活 slot8 前清 slot9 enable',
        'offset':  0x8afc4,
        'orig':    bytes.fromhex('1702C1E5'),   # STRB R0,[R1,#0x217] slot8.screen[12]=0
        'patched': bytes.fromhex('4802C1E5'),   # STRB R0,[R1,#0x248] byte_124E5C=0
        'note':    'STRB offset 0x217→0x248：清 slot9 enable（而非 slot8.screen[12]）',
    },
    {
        'name':    'Bug2 — sub_8B450 激活 slot8 前清 slot9 enable',
        'offset':  0x8b5b0,
        'orig':    bytes.fromhex('1702C1E5'),   # STRB R0,[R1,#0x217] slot8.screen[12]=0
        'patched': bytes.fromhex('4802C1E5'),   # STRB R0,[R1,#0x248] byte_124E5C=0
        'note':    'STRB offset 0x217→0x248：清 slot9 enable（而非 slot8.screen[12]）',
    },
]


def apply_patches(fw_data: bytearray) -> bytearray:
    for p in PATCHES:
        off  = p['offset']
        orig = p['orig']
        pat  = p['patched']
        n    = len(orig)
        assert n == len(pat), f"patch '{p['name']}': orig/patched 长度不一致"

        actual = bytes(fw_data[off:off + n])

        if actual == pat:
            log.warning(f"[已跳过] {p['name']} — 偏移 0x{off:x} 已是目标值")
            continue
        if actual != orig:
            raise ValueError(
                f"[中止] {p['name']}\n"
                f"  偏移 0x{off:x} 预期原始值: {orig.hex().upper()}\n"
                f"  实际值:                    {actual.hex().upper()}\n"
                f"  固件版本可能不匹配，请确认后再试。"
            )

        fw_data[off:off + n] = pat
        log.info(f"[已应用] {p['name']}")
        log.info(f"  偏移 0x{off:x}: {orig.hex().upper()} → {pat.hex().upper()}")
        log.info(f"  说明: {p['note']}")

    return fw_data


def self_check(fw_data: bytearray) -> None:
    """验证所有 patch 已正确写入"""
    ok = True
    for p in PATCHES:
        off = p['offset']
        pat = p['patched']
        actual = bytes(fw_data[off:off + len(pat)])
        if actual == pat:
            log.info(f"  自检通过: {p['name']} @ 0x{off:x}")
        else:
            log.error(f"  自检失败: {p['name']} @ 0x{off:x} "
                      f"期望 {pat.hex().upper()} 实际 {actual.hex().upper()}")
            ok = False
    assert ok, "一个或多个 patch 自检失败！"


def parse_args():
    parser = argparse.ArgumentParser(
        description='Patch MSI 4483 Dashboard firmware persistent display bugs.'
    )
    parser.add_argument(
        'firmware',
        nargs='?',
        default=DEFAULT_FIRMWARE_NAME,
        help=f'input firmware path, default: {DEFAULT_FIRMWARE_NAME}',
    )
    parser.add_argument(
        '-o', '--output',
        default=DEFAULT_PATCHED_NAME,
        help=f'patched firmware output path, default: {DEFAULT_PATCHED_NAME}',
    )
    parser.add_argument(
        '--no-backup',
        action='store_true',
        help='do not create a .backup copy next to the input firmware',
    )
    return parser.parse_args()


def main():
    args = parse_args()
    firmware_path = Path(args.firmware)
    patched_path = Path(args.output)

    # 读取原始固件
    if not firmware_path.exists():
        log.error(f"固件文件不存在：{firmware_path}")
        return

    log.info(f"读取固件：{firmware_path}")
    with firmware_path.open('rb') as f:
        data = bytearray(f.read())
    log.info(f"固件大小：{len(data)} bytes (0x{len(data):x})")

    # 执行所有 patch
    patched = apply_patches(data)

    # 备份原始固件
    if not args.no_backup:
        backup_path = firmware_path.with_name(f"{firmware_path.stem}_backup{firmware_path.suffix}")
        shutil.copy2(firmware_path, backup_path)
        log.info(f"原始固件已备份到: {backup_path}")

    # 写出 patch 后文件
    patched_path.parent.mkdir(parents=True, exist_ok=True)
    with patched_path.open('wb') as f:
        f.write(patched)
    log.info(f"Patch 文件已写出: {patched_path}")

    # 自检：验证写入结果
    with patched_path.open('rb') as f:
        check = bytearray(f.read())
    self_check(check)

    print()
    print("=" * 60)
    print(f"完成！已应用 {len(PATCHES)} 个 patch。")
    print(f"Patch 文件: {patched_path}")
    print()
    print("下一步：")
    print("  将 conprog_4BB6_22100300_patched.bin 重命名为")
    print("  conprog_4BB6_22100300.bin，放入刷新目录后刷入 NAND 即可。")
    print("=" * 60)


if __name__ == '__main__':
    main()
