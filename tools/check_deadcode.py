#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""模组废码检查：跑在 mod/QoL/*.cs 上，报告三类问题。

用法：python tools/check_deadcode.py
退出码：发现「确定的废码」返回 1，否则 0（可直接挂进验收流程）。

判定口径保守——只报能确证的：
  1) 私有/内部成员：全工程只出现一次（声明处），且名字不是公开约定
  2) 跨文件同名私有方法：同逻辑可能被抄了多份
  3) 设置项：定义了但没有任何模块读取
"""

import os
import re
import sys
import glob
import collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.join(ROOT, "mod", "QoL")


def load():
    files = sorted(glob.glob(os.path.join(SRC_DIR, "*.cs")))
    return {os.path.basename(f): open(f, encoding="utf-8", errors="ignore").read() for f in files}


def find_dead_members(src, counts):
    """私有成员只在声明处出现一次 => 多半没人用。"""
    dead = []
    for name, text in src.items():
        pattern = r"^\s*(?:private|internal)\s+(?:static\s+|readonly\s+|const\s+|sealed\s+)*[\w<>\[\],\s\.]+\s+([A-Za-z_]\w*)\s*[\(=;]"
        for m in re.finditer(pattern, text, re.M):
            ident = m.group(1)
            if ident.startswith("_"):
                continue          # 字段：可能只被本类别处用，按单词计数已覆盖
            if counts[ident] <= 1:
                dead.append((name, ident))
    return sorted(set(dead))


def _method_bodies(text):
    """抽出「私有/内部静态方法名 -> 方法体（去掉空白与注释）」。"""
    result = {}
    for m in re.finditer(r"^\s*(?:private|internal)\s+static\s+[\w<>\[\],\s\.]+\s+([A-Za-z_]\w*)\s*\(", text, re.M):
        name = m.group(1)
        i = text.find("{", m.end())
        if i < 0:
            continue
        depth, j = 0, i
        while j < len(text):
            if text[j] == "{":
                depth += 1
            elif text[j] == "}":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        body = text[i:j + 1]
        body = re.sub(r"//[^\n]*", "", body)
        body = re.sub(r"\s+", "", body)
        result.setdefault(name, []).append((body, name))
    return result


def find_duplicate_methods(src):
    """只报「方法体几乎一样」的跨文件方法——真重复实现，才值得合并。"""
    import difflib

    per_name = {}
    for name, text in src.items():
        for ident, bodies in _method_bodies(text).items():
            for body, _ in bodies:
                per_name.setdefault(ident, []).append((name, body))

    dups = {}
    for ident, items in per_name.items():
        if len(items) < 2:
            continue
        hit = set()
        for a in range(len(items)):
            for b in range(a + 1, len(items)):
                if items[a][0] == items[b][0]:
                    continue
                ratio = difflib.SequenceMatcher(None, items[a][1], items[b][1]).ratio()
                if ratio >= 0.9:
                    hit.add(items[a][0])
                    hit.add(items[b][0])
        if hit:
            dups[ident] = sorted(hit)
    return dups


def find_unused_settings(src):
    """QoLSettings 里定义了、但别的文件从不读的设置项。"""
    settings = src.get("QoLSettings.cs", "")
    props = re.findall(r"internal ConfigEntry<[^>]+>\s+(\w+)\s*\{", settings)
    unused = []
    for prop in props:
        used = 0
        for name, text in src.items():
            if name == "QoLSettings.cs":
                continue
            used += len(re.findall(r"\." + prop + r"\b", text))
        if used == 0:
            unused.append(prop)
    return unused, len(props)


def find_legacy_logging(src):
    """约定统一走 QLog；这里列出还在用 Debug.Log* 的文件（不是 bug，但属于旧写法）。"""
    hits = {}
    for name, text in src.items():
        if name == "QLog.cs":
            continue
        n = len(re.findall(r"Debug\.Log", text))
        if n:
            hits[name] = n
    return hits


def main():
    src = load()
    if not src:
        print("没找到 mod/QoL/*.cs")
        return 1

    counts = collections.Counter(re.findall(r"[A-Za-z_]\w*", "\n".join(src.values())))

    dead = find_dead_members(src, counts)
    dups = find_duplicate_methods(src)
    unused, total = find_unused_settings(src)

    print("=" * 60)
    print("废码检查：%d 个文件" % len(src))
    print("=" * 60)

    print("\n[1] 只声明、没人引用（确定的废码）")
    if dead:
        for f, n in dead:
            print("    %-28s %s" % (f, n))
    else:
        print("    无")

    print("\n[2] 跨文件同名私有方法（重复实现，按需合并）")
    noisy = {"Apply", "Remove", "Tick", "Show", "Hide", "Build", "Refresh", "Ensure",
             "Notify", "Patch", "Scan", "Run", "Init", "Install"}
    shown = 0
    for name, files in sorted(dups.items()):
        if name in noisy:
            continue
        print("    %-22s %s" % (name, ", ".join(files)))
        shown += 1
    if shown == 0:
        print("    无")

    legacy = find_legacy_logging(src)
    print("\n[3] 日志调用是否统一走 QLog")
    if legacy:
        total = sum(legacy.values())
        print("    %d 处 Debug.Log* 仍在 %d 个文件里（旧写法）" % (total, len(legacy)))
        for name, n in sorted(legacy.items(), key=lambda kv: -kv[1]):
            print("      %-28s %d" % (name, n))
    else:
        print("    全部走 QLog")

    print("\n[4] 设置项是否都有人读")
    if unused:
        for p in unused:
            print("    未使用：%s" % p)
    else:
        print("    %d 个设置项全部在用" % total)

    print("")
    return 1 if dead or unused else 0


if __name__ == "__main__":
    sys.exit(main())
