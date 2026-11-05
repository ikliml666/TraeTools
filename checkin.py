#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Trae 每日签到脚本（GitHub Actions 版）

原理：
  Trae 网页端的 JWT 只有 8 小时有效期，但真正的会话凭证是 HttpOnly Cookie
  `X-Cloudide-Session`（约 14 天有效）。本脚本通过该 Cookie 调用
  `GetUserToken` 接口换取全新 JWT，再用新 JWT 执行每日签到。

依赖：仅标准库，无第三方依赖。

环境变量：
  TRAE_SESSION        账号 1 的 X-Cloudide-Session Cookie（必填；后端兼容单账号部署）
  TRAE_DEVICE_ID      账号 1 的 x-device-id，16 位数字（选填，缺省随机）
  TRAE_SESSION_N      第 N(N≥2) 个账号的会话 Cookie；缺失即停止读取更多账号
  TRAE_DEVICE_ID_N    第 N 个账号的 x-device-id（选填，缺省随机）
  全部账号共享：       FEISHU_WEBHOOK（选填，签到后推送一条汇总）

用法：
  python checkin.py
"""

import datetime
import json
import os
import random
import sys
import time
import urllib.error
import urllib.request

BASE = "https://api.trae.cn"

# 9074 风控的尝试上限。原先 3 次、累计约 15 秒，在上午高峰几乎必然打满，
# 让 workflow 长期标红——被误读成「部署失败」，实际是服务端限流。
MAX_ATTEMPTS = 6


def _post(path, headers, body=""):
    """POST 请求。非 2xx 不抛 HTTPError，而是以 (status, body) 正常返回，便于上层判断原因。"""
    req = urllib.request.Request(BASE + path, data=body.encode("utf-8"), headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")


def get_token(session: str) -> str:
    """用 X-Cloudide-Session Cookie 换取全新 JWT。"""
    headers = {
        "Cookie": "X-Cloudide-Session=" + session,
        "Referer": "https://www.trae.cn/",
        "Origin": "https://www.trae.cn",
        "User-Agent": "TraeCheckin/1.0",
        "Accept": "application/json, text/plain, */*",
    }
    status, text = _post("/cloudide/api/v3/common/GetUserToken", headers)
    data = json.loads(text)
    token = (data.get("Result") or {}).get("Token")
    if status == 401:
        raise RuntimeError(
            "账号会话已失效(HTTP 401)：X-Cloudide-Session 可能已过期(~14天有效期)。"
            "请在浏览器重新登录 trae.cn 并复制新的 Cookie 值，更新到 TRAE_SESSION 后重试。"
            "原始返回: " + text[:200])
    if status != 200 or not token:
        raise RuntimeError("GetUserToken 失败: HTTP %s %s" % (status, text[:200]))
    return token


def _auth_headers(token: str, device_id: str) -> dict:
    return {
        "Authorization": "Cloud-IDE-JWT " + token,
        "X-User-Region": "cn",
        "x-device-id": device_id,
        "Content-Type": "application/json",
        "User-Agent": "TraeCheckin/1.0",
    }


def _post_json(path: str, token: str, device_id: str) -> dict:
    """带 JWT 认证的 POST，返回 {"http": 状态码, "body": 解析后的 JSON}。"""
    status, text = _post(path, _auth_headers(token, device_id), "{}")
    try:
        return {"http": status, "body": json.loads(text)}
    except json.JSONDecodeError:
        return {"http": status, "body": {"raw": text}}


def checkin(token: str, device_id: str) -> dict:
    """执行每日签到（claim）。响应只含 code/message，不含本次所得积分。"""
    return _post_json("/trae/api/v2/ug/checkin_credits/claim", token, device_id)


def get_status(token: str, device_id: str) -> dict:
    """查询今日签到状态。单日奖励（credits）只能从该接口读到。"""
    return _post_json("/trae/api/v2/ug/checkin_credits/status", token, device_id)


def resolve_gained_credits(body: dict):
    """从 status 响应解析当日已得积分，口径对齐本地 CheckinEvaluator.ResolveGainedCredits：
    credits 是基础所得；extra_credits 是连签加成、仅会员到账，脚本不做会员判定故不计入。
    code 非 0 或未签到则数值不可信，返回 None。缺 credits 字段直接抛 KeyError——
    接口结构变了就该暴露，而不是返回 0 让日志重现「恒为 0」的假象。"""
    if body.get("code") != 0 or not body.get("checked_in"):
        return None
    return body["credits"]


def is_checked_in_status(status: dict) -> bool:
    """status 响应是否表示「今日已签到」。只判定状态，不依赖积分字段。"""
    body = status["body"]
    return status["http"] == 200 and body.get("code") == 0 and bool(body.get("checked_in"))


def fetch_gained_credits(token: str, device_id: str):
    """claim 后补查 status 拿真实积分。返回 (积分或 None, 说明或 None)。
    claim 响应不含积分字段，不看 status 就只能报 0——那正是旧版日志恒为 0 的原因。"""
    try:
        status = get_status(token, device_id)
    except (urllib.error.URLError, OSError) as e:
        return None, "status 查询失败，积分未知：%s" % e
    if status["http"] != 200:
        return None, "status 查询返回 HTTP %s，积分未知" % status["http"]
    gained = resolve_gained_credits(status["body"])
    if gained is None:
        return None, "status 返回未签到或 code=%s，积分未知" % status["body"].get("code")
    return gained, None


def notify_feishu(webhook, text):
    """向飞书机器人推送一条文本消息；webhook 为空则跳过。返回 HTTP 状态码，失败返回 None。"""
    if not webhook:
        return None
    try:
        payload = json.dumps({"msg_type": "text", "content": {"text": text}}).encode("utf-8")
        req = urllib.request.Request(webhook, data=payload, headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status
    except Exception:
        return None


def beijing_now_str():
    """返回北京时间字符串（GitHub Actions 运行在 UTC，需 +8 小时）。"""
    beijing = datetime.timezone(datetime.timedelta(hours=8))
    return datetime.datetime.now(beijing).strftime("%Y-%m-%d %H:%M:%S")


def iter_sessions():
    """按顺序产出 (账号序号, session, device_id)。账号 1 读 TRAE_SESSION；
    之后依次读 TRAE_SESSION_2, TRAE_SESSION_3… 直到缺空为止。"""
    s = os.environ.get("TRAE_SESSION", "").strip()
    if s:
        yield 1, s, os.environ.get("TRAE_DEVICE_ID", "").strip()
    n = 2
    while True:
        s = os.environ.get("TRAE_SESSION_%d" % n, "").strip()
        if not s:
            break
        yield n, s, os.environ.get("TRAE_DEVICE_ID_%d" % n, "").strip()
        n += 1


def random_device_id():
    """随机生成 16 位数字风控设备号（仅在缺省时兜底）。"""
    return str(random.randint(10**15, 10**16 - 1))


def main():
    accounts = list(iter_sessions())
    if not accounts:
        print("错误：缺少环境变量 TRAE_SESSION")
        sys.exit(1)

    webhook = os.environ.get("FEISHU_WEBHOOK", "").strip()
    ok_names, fail_names = [], []

    for index, session, device_id in accounts:
        # 多账号之间错开 3~6 秒随机间隔，降低请求密度，规避 9074「参与用户太多」风控
        if index > 1:
            time.sleep(random.uniform(3, 6))
        name = "账号 %d" % index
        device_id = device_id or random_device_id()
        print("[%s] device_id=%s" % (name, device_id))
        try:
            token = get_token(session)
            print("[%s] 已换取新 JWT，长度=%d" % (name, len(token)))
            # 签前预检：已签过就不再 claim。claim 本身幂等，但预检能让日志给出
            # 「今日已签到」而不是把旧签到误报成本次所得。
            pre = get_status(token, device_id)
            if is_checked_in_status(pre):
                today = pre["body"].get("credits")
                if today is None:
                    print("[%s] 今日已签到，跳过 claim（积分字段缺失，数值未知）" % name)
                else:
                    print("[%s] 今日已签到，跳过 claim（今日积分 %g）" % (name, today))
                ok_names.append(name + "（已签）")
                continue
            if pre["http"] != 200:
                print("[%s] 签前状态查询返回 HTTP %s，继续执行 claim" % (name, pre["http"]))
            result = checkin(token, device_id)
            body = result["body"]
            code = body.get("code", -1)
            # 9074「参与用户太多」= 设备号被风控标记；换全新设备号退避重试。
            # 退避按 3·2^(n-1) 秒递增（3/6/12/24/48），共 6 次尝试约 93 秒窗口。
            # 原先只试 3 次、累计等 15 秒，上午高峰几乎必然打满——那正是云端
            # workflow 长期标红的原因，不是账号或仓库部署的问题。
            attempt = 1
            while code == 9074 and attempt < MAX_ATTEMPTS:
                wait = min(3 * (2 ** (attempt - 1)), 60)
                device_id = random_device_id()
                attempt += 1
                print("[%s] 命中风控 9074，%.1f 秒后换新设备号重试（第 %d 次）" % (name, wait, attempt))
                time.sleep(wait + random.uniform(0, 1.5))
                result = checkin(token, device_id)
                body = result["body"]
                code = body.get("code", -1)
            # 9095 = 该设备今日已签到（签发额度按设备计），等价于成功而非失败
            if code == 9095:
                gained, note = fetch_gained_credits(token, device_id)
                if note:
                    print("[%s] 设备今日已签到（9095），但%s" % (name, note))
                else:
                    print("[%s] 设备今日已签到（9095），今日积分 %g" % (name, gained))
                ok_names.append(name + "（已签）")
                continue
            checked = body.get("checked_in", False)
            ok = (result["http"] == 200) and (code == 0 or checked)
            if ok:
                # claim 响应里没有积分字段，补查一次 status 才是真实数值
                gained, note = fetch_gained_credits(token, device_id)
                if note:
                    print("[%s] 签到成功，但%s" % (name, note))
                else:
                    print("[%s] 签到成功，本次获得：%g 积分" % (name, gained))
                ok_names.append(name)
            else:
                reason = body.get("message") or ("HTTP %s" % result["http"])
                print("[%s] 签到失败：%s" % (name, reason))
                fail_names.append(name)
        except Exception as e:
            print("[%s] 签到异常: %s" % (name, e))
            fail_names.append(name)

    # 汇总一条飞书推送（无论成功/失败都汇总，webhook 为空则跳过）
    summary = ["Trae 多账号签到结果", "时间：%s" % beijing_now_str()]
    if ok_names:
        summary.append("成功：" + "、".join(ok_names))
    if fail_names:
        summary.append("失败：" + "、".join(fail_names))
    if webhook and (ok_names or fail_names):
        notify_feishu(webhook, "\n".join(summary))

    # 只有全部账号都失败才算本次部署失败。部分失败（典型是某个账号撞上
    # 9074 风控）不该让整个 workflow 标红——那会把「服务端限流」误报成
    # 「部署坏了」，运维看不出区别。
    if not ok_names:
        print("全部账号签到失败")
        sys.exit(1)
    if fail_names:
        print("部分账号签到失败：%s（其余 %d 个账号已完成）" % ("、".join(fail_names), len(ok_names)))
        return
    print("全部账号签到完成")


if __name__ == "__main__":
    main()
