#!/usr/bin/env python
"""契约自检：解析 openapi.v1.yaml，验证所有 $ref 可解析、必做端点齐全、代码路由全部在契约里。

用法：
    python scripts/check/check_contract.py

退出码 0 表示通过；非 0 表示契约有结构性问题，CI 应视为失败。
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover
    sys.exit("需要 pyyaml：.tools/venv/Scripts/python.exe -m pip install pyyaml")

ROOT = Path(__file__).resolve().parent.parent.parent
CONTRACT = ROOT / "contracts" / "openapi.v1.yaml"
SERVER_DIR = ROOT / "src" / "MediaRack.Server"

# 不写进契约的注册：内嵌页面本身（静态资源，不是 API）。
# 门户前缀 /s/{slug} 不在路由表里（中间件剥前缀），所以不会出现在这里。
IGNORED_ROUTES = {("GET", "/")}

# 认得出 `app.MapGet("/api/v1/x", ...)` 这种字面量注册。
# 用常量/拼接传路径的注册抓不到——那种写法本来就该避免。
ROUTE_RE = re.compile(r'Map(Get|Post|Patch|Delete|Put)\(\s*"([^"]+)"')
PARAM_RE = re.compile(r"\{[^}]*\}")

# 契约必须覆盖的端点（缺一个就说明实现会缺能力）
REQUIRED_PATHS = [
    "/api/v1/sources",
    "/api/v1/sources/{source}/library",
    "/api/v1/library",
    "/api/v1/media/{source}/{key}",
    "/media/{source}/{key}/video.m4s",
    "/media/{source}/{key}/audio.m4s",
    "/media/{source}/{key}/file",
    "/play/{source}/{key}",
    "/poster/{source}/{key}.jpg",
    "/api/v1/meta/{source}/{key}",
    "/api/v1/collections",
    "/api/v1/progress/{source}/{key}",
    "/api/v1/scan",
    "/api/v1/health",
    "/api/v1/export/{source}/{key}.mp4",
]


def walk_refs(node, path="$"):
    """产出 (引用字符串, 所在位置)。"""
    if isinstance(node, dict):
        for k, v in node.items():
            if k == "$ref" and isinstance(v, str):
                yield v, f"{path}.$ref"
            else:
                yield from walk_refs(v, f"{path}.{k}")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            yield from walk_refs(v, f"{path}[{i}]")


def deref(spec: dict, ref: str):
    if not ref.startswith("#/"):
        return True  # 外部引用不在本次校验范围
    cur = spec
    for part in ref[2:].split("/"):
        part = part.replace("~1", "/").replace("~0", "~")
        if not isinstance(cur, dict) or part not in cur:
            return False
        cur = cur[part]
    return True


def norm(path: str) -> str:
    """路径同名化：`{source}` 与 `{id}`、`{folderId:long}` 都折成 `{}`。

    参数名本身不是契约的一部分（客户端按位置填），路由形状才是。
    """
    return PARAM_RE.sub("{}", path)


def code_routes() -> set[tuple[str, str]]:
    """代码里注册的全部 (方法, 路径)。只认字面量路径，见 ROUTE_RE 的说明。"""
    routes: set[tuple[str, str]] = set()
    if not SERVER_DIR.exists():
        return routes
    for f in SERVER_DIR.rglob("*.cs"):
        if {"bin", "obj"} & set(f.parts):
            continue
        text = f.read_text(encoding="utf-8", errors="ignore")
        for m in ROUTE_RE.finditer(text):
            routes.add((m.group(1).upper(), m.group(2)))
    return routes


def contract_routes(spec: dict) -> set[tuple[str, str]]:
    """契约里声明了方法的 (方法, 路径)。只写参数没写方法的 path 不算。"""
    out: set[tuple[str, str]] = set()
    for path, ops in (spec.get("paths") or {}).items():
        if not isinstance(ops, dict):
            continue
        for method in ops:
            if method.lower() in ("get", "post", "patch", "delete", "put"):
                out.add((method.upper(), path))
    return out


def main() -> int:
    if not CONTRACT.exists():
        print(f"FAIL 找不到契约文件：{CONTRACT}")
        return 1

    spec = yaml.safe_load(CONTRACT.read_text(encoding="utf-8"))
    errors: list[str] = []

    if spec.get("openapi", "").split(".")[0] != "3":
        errors.append(f"openapi 版本应为 3.x，实际 {spec.get('openapi')!r}")

    # 1. $ref 全部可解析
    bad = [(r, where) for r, where in walk_refs(spec) if not deref(spec, r)]
    for ref, where in bad:
        errors.append(f"$ref 无法解析：{ref}  ({where})")

    # 2. 必做端点齐全
    have = set(spec.get("paths", {}))
    for p in REQUIRED_PATHS:
        if p not in have:
            errors.append(f"缺少端点：{p}")

    # 3. 每个流端点必须声明 Range 支持
    for p in REQUIRED_PATHS:
        if not (p.startswith("/media/") or p.startswith("/play/")) or p not in have:
            continue
        params = spec["paths"][p].get("get", {}).get("parameters", [])
        names = set()
        for prm in params:
            if "$ref" in prm:
                ref = prm["$ref"]
                if ref.endswith("/Range"):
                    names.add("Range")
            else:
                names.add(prm.get("name"))
        if "Range" not in names:
            errors.append(f"流端点未声明 Range：{p}")

    # 4. 不带 {source} 的条目类端点
    for p in have:
        if p.startswith("/api/v1/media/") and "{source}" not in p:
            errors.append(f"条目端点缺少 {{source}} 段：{p}")

    # 5. 代码里的每条路由都必须写在契约里，反之亦然。
    #    这条检查是补的：在此之前契约只校验"自己长得对不对"，实现可以悄悄多出
    #    十个端点而没人发现（管理台那批就是这么漂出去的）。
    code = {(m, p) for m, p in code_routes() if (m, p) not in IGNORED_ROUTES}
    declared = contract_routes(spec)
    code_norm = {(m, norm(p)) for m, p in code}
    declared_norm = {(m, norm(p)) for m, p in declared}

    for m, p in sorted(code_norm - declared_norm):
        errors.append(f"代码里有、契约没写：{m} {p}")
    for m, p in sorted(declared_norm - code_norm):
        errors.append(f"契约里有、代码没实现：{m} {p}")

    total = len(have)
    if errors:
        print(f"FAIL 契约校验未通过（{len(errors)} 个问题）")
        for e in errors:
            print("  -", e)
        return 1

    print(f"OK   openapi {spec['openapi']} · {total} 个路径 · {len(declared)} 个方法 · "
          f"代码 {len(code)} 条路由全部在契约里 · 全部 $ref 可解析")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
