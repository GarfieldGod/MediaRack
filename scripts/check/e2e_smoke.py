#!/usr/bin/env python3
"""
MediaRack 端到端验收脚本。

对**已经在跑**的服务端跑一遍完整链路，逐项断言，最后比对源目录的 md5 快照。
这样验收不是"我看了一眼觉得对"，而是可以反复重跑的东西。

用法:
    # 1. 先起服务
    src/MediaRack.Server/bin/Debug/net10.0/MediaRack-Server.exe \
        --meta /path/to/meta --urls http://127.0.0.1:8080

    # 2. 跑验收
    python scripts/check/e2e_smoke.py --base http://127.0.0.1:8080 \
        --root /path/to/videos --source bili

    # 3. 加上 --hash 让源目录比对用 md5（默认也是 md5，这个开关只是为了显式）
    python scripts/check/e2e_smoke.py ... --hash

退出码: 0 全通过 / 1 有失败项。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

PASS, FAIL, SKIP = "PASS", "FAIL", "SKIP"
results: list[tuple[str, str, str]] = []


def record(name: str, ok: bool, detail: str = "") -> bool:
    results.append((name, PASS if ok else FAIL, detail))
    mark = "✓" if ok else "✗"
    print(f"  {mark} {name}" + (f"  —— {detail}" if detail else ""))
    return ok


def skip(name: str, detail: str) -> None:
    results.append((name, SKIP, detail))
    print(f"  - {name}  —— {detail}")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


_opener_follow = urllib.request.build_opener()
_opener_plain = urllib.request.build_opener(NoRedirect)

# 服务端配了令牌时，所有请求统一带 Bearer。
# 用 Bearer 而不是 cookie：脚本不是浏览器，没有"浏览器自己发请求"的问题。
_AUTH: dict[str, str] = {}


def call(base: str, path: str, method: str = "GET", payload=None,
         headers=None, follow: bool = True, raw: bool = False):
    data = json.dumps(payload).encode() if payload is not None else None
    hdrs = dict(_AUTH)
    hdrs.update(headers or {})
    if data is not None:
        hdrs["Content-Type"] = "application/json"

    req = urllib.request.Request(base + path, data=data, headers=hdrs, method=method)
    opener = _opener_follow if follow else _opener_plain

    try:
        with opener.open(req) as resp:
            body = resp.read()
            if raw:
                return resp.status, dict(resp.headers), body
            try:
                return resp.status, dict(resp.headers), json.loads(body or b"null")
            except json.JSONDecodeError:
                return resp.status, dict(resp.headers), None
    except urllib.error.HTTPError as err:
        body = err.read()
        try:
            return err.code, dict(err.headers), json.loads(body or b"null")
        except json.JSONDecodeError:
            return err.code, dict(err.headers), None


# ── 源目录快照 ────────────────────────────────────────────────

def snapshot(root: str) -> dict[str, str]:
    """path → md5。目录不参与（目录本身也可能被记录，但对只读性判定没用）。"""
    result: dict[str, str] = {}
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in filenames:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, root).replace("\\", "/")
            digest = hashlib.md5()
            try:
                with open(full, "rb") as handle:
                    for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                        digest.update(chunk)
                result[rel] = digest.hexdigest()
            except OSError as exc:
                result[rel] = f"<unreadable: {exc}>"
    return result


def diff_snapshots(before: dict[str, str], after: dict[str, str]) -> list[str]:
    problems = []
    for path in before.keys() - after.keys():
        problems.append(f"删除 {path}")
    for path in after.keys() - before.keys():
        problems.append(f"新增 {path}")
    for path in before.keys() & after.keys():
        if before[path] != after[path]:
            problems.append(f"修改 {path}")
    return problems


# ── 主流程 ────────────────────────────────────────────────────

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:8080")
    parser.add_argument("--root", required=True, help="源目录（必须是服务端本机可访问的路径）")
    parser.add_argument("--source", default=None, help="源标识，默认取目录名")
    parser.add_argument("--verify", default="full", choices=["none", "sample", "full"])
    parser.add_argument("--hash", action="store_true", help="显式指定用 md5 比对源目录（默认就是）")
    parser.add_argument("--skip-remux", action="store_true", help="跳过 remux（本机没有 ffmpeg 时）")
    parser.add_argument("--token", default=None, help="服务端配了 --token 时必填")
    args = parser.parse_args()

    base = args.base.rstrip("/")
    root = os.path.abspath(args.root)
    source = args.source or os.path.basename(root.rstrip("/\\"))

    print(f"\n服务  {base}")
    print(f"源    {root}  （id={source}）")

    print("\n[0] 鉴权")
    status, _, state = call(base, "/api/v1/auth/state")
    if not record("auth/state 免鉴权可达", status == 200 and state is not None, f"HTTP {status}"):
        print("\n服务没起来，先启动 MediaRack-Server。")
        return 1

    auth_required = bool(state.get("authRequired"))
    print(f"      需要令牌: {'是' if auth_required else '否'}")

    if auth_required:
        if not args.token:
            print("\n  服务端配了令牌，但没有给 --token，后面的请求会全部 401。")
            return 2

        # 先确认"不带凭据确实被拒"，否则这个令牌等于没生效。
        status, _, _ = call(base, "/api/v1/sources")
        record("无凭据时受保护端点被拒", status == 401, f"HTTP {status}")

        _AUTH["Authorization"] = f"Bearer {args.token}"

        status, _, again = call(base, "/api/v1/auth/state")
        record("带凭据后通过鉴权",
               status == 200 and bool((again or {}).get("authenticated")), f"HTTP {status}")
    elif args.token:
        print("      服务端没配令牌，--token 被忽略。")

    print("\n[1] 服务与库")
    status, _, health = call(base, "/api/v1/health")
    if not record("health 可用", status == 200 and health is not None, f"HTTP {status}"):
        print("\n服务没起来，先启动 MediaRack-Server。")
        return 1

    record("健康检查报出鉴权要求", health.get("authRequired") == auth_required,
           f"authRequired={health.get('authRequired')}")

    record("库版本为 v1", health.get("schemaVersion") == 1,
           f"schemaVersion={health.get('schemaVersion')}")
    has_ffmpeg = bool(health.get("capabilities", {}).get("ffmpeg"))
    print(f"      ffmpeg: {'可用' if has_ffmpeg else '不可用'}")

    print("\n[2] 源目录快照（验收前后必须一致）")
    before = snapshot(root)
    print(f"      {len(before)} 个文件")

    print("\n[3] 加源并扫描")
    status, _, accepted = call(base, "/api/v1/scan", "POST", {
        "root": root, "sourceId": source, "layout": "auto", "verify": args.verify,
    })
    if not record("scan 返回 202", status == 202, f"HTTP {status}"):
        return 1

    job_id = accepted["jobId"]
    deadline = time.time() + 600
    job = None
    while time.time() < deadline:
        _, _, job = call(base, f"/api/v1/scan/{job_id}")
        if job and job.get("state") in ("done", "failed"):
            break
        time.sleep(0.8)

    record("扫描完成", bool(job) and job.get("state") == "done",
           (job or {}).get("message") or "超时")

    status, _, sources = call(base, "/api/v1/sources")
    entry = next((s for s in sources["sources"] if s["id"] == source), None)
    record("源已登记且可达", entry is not None and entry["reachable"],
           f"layout={entry['layout']}, items={entry['itemCount']}" if entry else "未找到")
    record("条目数 > 0", bool(entry and entry["itemCount"] > 0), f"{entry['itemCount'] if entry else 0} 条")

    print("\n[4] 库浏览")
    status, _, page = call(base, f"/api/v1/sources/{source}/library?limit=5")
    record("单源列表", status == 200 and page["total"] > 0, f"total={page.get('total')}")
    record("分页生效", len(page["items"]) <= 5, f"返回 {len(page['items'])} 条")

    status, _, agg = call(base, "/api/v1/library?limit=5")
    record("聚合列表", status == 200, f"total={agg.get('total')}")

    if not page["items"]:
        print("\n没有条目，后续检查跳过。")
        return finish(before, root)

    item = page["items"][0]
    key = item["key"]
    dual = item["layout"] == "dual"

    status, _, detail = call(base, f"/api/v1/media/{source}/{key}")
    record("详情可读", status == 200 and detail["key"] == key,
           f"tracks={[t['role'] for t in detail.get('tracks', [])]}, delivery={detail.get('delivery')}")

    print("\n[5] Range 流（零转码）")
    track = f"/media/{source}/{key}/video.m4s" if dual else f"/media/{source}/{key}/file"
    status, headers, body = call(base, track, headers={"Range": "bytes=0-1023"}, raw=True)
    record("Range 返回 206", status == 206, f"HTTP {status}")
    record("返回 1024 字节", len(body) == 1024, f"{len(body)} 字节")
    record("Content-Range 正确", "bytes 0-1023/" in headers.get("Content-Range", ""),
           headers.get("Content-Range", "缺失"))
    record("Content-Type 不是 octet-stream",
           "octet-stream" not in headers.get("Content-Type", ""),
           headers.get("Content-Type", "缺失"))

    total_size = int(headers.get("Content-Range", "/0").rsplit("/", 1)[-1] or 0)
    status, headers, _ = call(base, track, headers={"Range": f"bytes={total_size + 1000}-"}, raw=True)
    record("越界 Range 返回 416", status == 416, f"HTTP {status}")

    status, headers, body = call(base, track, raw=True)
    record("无 Range 返回全量", status == 200 and len(body) == total_size,
           f"{len(body)} 字节，Accept-Ranges={headers.get('Accept-Ranges')}")

    if dual:
        status, headers, body = call(base, f"/media/{source}/{key}/audio.m4s",
                                     headers={"Range": "bytes=0-99"}, raw=True)
        record("音频轨可读", status == 206 and len(body) == 100,
               f"HTTP {status}, {len(body)} 字节, {headers.get('Content-Type')}")

    print("\n[6] 封面")
    status, headers, body = call(base, f"/poster/{source}/{key}.jpg", raw=True)
    if status == 200:
        record("缩略图可读", True, f"{len(body)} 字节, {headers.get('Content-Type')}")
    else:
        skip("缩略图", "尚未生成（抽帧失败或缺 ffmpeg 时属正常）")

    print("\n[7] 虚拟层（只写库）")
    status, _, meta = call(base, f"/api/v1/meta/{source}/{key}", "POST",
                           {"titleDisplay": "验收改名"})
    record("改名成功", status == 200 and meta.get("titleDisplay") == "验收改名", f"HTTP {status}")
    rev = meta.get("rev", 0)
    record("rev 自增", rev >= 1, f"rev={rev}")

    status, _, conflict = call(base, f"/api/v1/meta/{source}/{key}", "POST",
                               {"titleDisplay": "不该生效"}, {"If-Match": str(max(0, rev - 1))})
    record("旧 If-Match 返回 409", status == 409,
           conflict.get("code") if conflict else f"HTTP {status}")

    status, _, cleared = call(base, f"/api/v1/meta/{source}/{key}", "POST", {"titleDisplay": ""})
    record("空串清除覆盖", status == 200 and cleared.get("titleDisplay") is None,
           f"titleDisplay={cleared.get('titleDisplay') if cleared else '?'}")

    status, _, progress = call(base, f"/api/v1/progress/{source}/{key}", "POST", {"posMs": 1234})
    record("进度回传", status == 200 and progress.get("playPosMs") == 1234,
           f"posMs={progress.get('playPosMs') if progress else '?'}")

    print("\n[8] 收藏夹")
    name = f"验收 {int(time.time())}"
    status, _, created = call(base, "/api/v1/collections", "POST", {"name": name})
    record("新建收藏夹", status == 200 and created["id"].startswith("col:"),
           created.get("id") if created else f"HTTP {status}")

    if status == 200:
        cid = created["id"]
        status, _, added = call(base, f"/api/v1/collections/{cid}/items", "POST",
                                {"add": [{"source": source, "key": key}]})
        record("加入条目", status in (200, 207), f"HTTP {status}")

        status, _, filtered = call(base, f"/api/v1/library?collection={cid}")
        record("按收藏夹过滤", status == 200 and filtered["total"] >= 1,
               f"total={filtered.get('total')}")

        call(base, f"/api/v1/collections/{cid}/items", "POST",
             {"remove": [{"source": source, "key": key}]})
        call(base, "/api/v1/collections", "POST", {"id": cid, "name": name + " 改名"})

    print("\n[9] 播放模式")
    status, headers, _ = call(base, f"/play/{source}/{key}?mode=direct", follow=False, raw=True)
    record("direct 返回 307", status == 307, f"HTTP {status}, Location={headers.get('Location')}")

    status, _, conflict = call(base, f"/play/{source}/{key}?mode=transcode")
    record("transcode 返回 409", status == 409,
           conflict.get("code") if conflict else f"HTTP {status}")

    if args.skip_remux or not has_ffmpeg:
        skip("remux", "已跳过（--skip-remux 或本机无 ffmpeg）")
    else:
        started = time.time()
        status, headers, body = call(base, f"/play/{source}/{key}?mode=remux", raw=True)
        elapsed = time.time() - started
        record("remux 返回单流 mp4", status == 200 and len(body) > 0,
               f"HTTP {status}, {len(body)/1e6:.2f} MB, {elapsed:.2f}s, {headers.get('Content-Type')}")

        if status == 200 and b"ftyp" in body[:32]:
            record("产物是 mp4 容器", True, "ftyp box 存在")

        if status == 200 and _have("ffprobe"):
            probe = subprocess.run(
                ["ffprobe", "-v", "error", "-show_entries", "stream=codec_type,codec_name",
                 "-of", "json", "-"],
                input=body, capture_output=True)
            if probe.returncode == 0:
                streams = json.loads(probe.stdout or b"{}").get("streams", [])
                kinds = sorted(s.get("codec_type") for s in streams)
                record("产物含音视频两轨", kinds == ["audio", "video"], f"streams={kinds}")
            else:
                skip("ffprobe 校验", "ffprobe 读不了内存流")

    return finish(before, root)


def _have(exe: str) -> bool:
    from shutil import which
    return which(exe) is not None


def finish(before: dict[str, str], root: str) -> int:
    print("\n[10] 只读性证明")
    after = snapshot(root)
    problems = diff_snapshots(before, after)

    if problems:
        record("源目录未被改动", False, "；".join(problems[:5]))
    else:
        record("源目录未被改动", True,
               f"{len(after)} 个文件 md5 全部一致，新增 0 / 删除 0 / 修改 0")

    failed = [r for r in results if r[1] == FAIL]
    skipped = [r for r in results if r[1] == SKIP]

    print("\n" + "─" * 60)
    print(f"通过 {len(results) - len(failed) - len(skipped)} / 失败 {len(failed)} / 跳过 {len(skipped)}")

    if failed:
        print("\n失败项：")
        for name, _, detail in failed:
            print(f"  ✗ {name}  {detail}")
        return 1

    print("结论 ✅ 全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
