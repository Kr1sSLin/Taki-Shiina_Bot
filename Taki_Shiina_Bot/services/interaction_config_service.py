"""
互动积分 · 等级体系 —— 后台可配置项（PRD 5.5 / 4.1 / 4.3 / 4.4 / FR-6 / 九·可配置性）

统一管理四类可在后台调整、**无需发版**的配置：

1. ``interaction_items``  —— 互动物品（名称、图标、所需积分、排序、上下架）
2. ``prompt_templates``   —— 每个物品对应的 Prompt 模板（FR-6）
3. ``level_config``       —— 等级阈值表（4.4 节）
4. ``points_rules``       —— 积分规则、断签阈值、补签卡上限、DeepSeek 重试策略等

存储为**明文 JSON**（``config/gamification_config.json``）：内容不含用户隐私数据，
明文可让运营直接改文件，同时提供 ``/api/v1/admin/*`` 接口读写（需求方确认方案）。

热加载：完整校验后发布修订，最近有效版本独立持久化。
一致性（EDGE-9）：互动请求在**发起时**快照模板，后台更新只影响之后新发起的请求。
"""

from __future__ import annotations

import json
import math
import os
import threading
import time
import copy
import tempfile
from contextlib import contextmanager
from string import Formatter
from typing import Any
from services.file_lock import FileLockTimeoutError, acquire_file_lock, release_file_lock

DEFAULT_CONFIG_FILENAME = "gamification_config.json"

# 熊猫成长意象等级（PRD 4.4，传奇熊猫为当前最高等级）
DEFAULT_LEVEL_CONFIG: list[dict[str, Any]] = [
    {"level_code": "PANDA_LV1", "level_name": "初生熊猫", "threshold_days": 3, "sort_order": 10},
    {"level_code": "PANDA_LV2", "level_name": "好奇宝宝", "threshold_days": 7, "sort_order": 20},
    {"level_code": "PANDA_LV3", "level_name": "竹林新秀", "threshold_days": 15, "sort_order": 30},
    {"level_code": "PANDA_LV4", "level_name": "黑白骑士", "threshold_days": 30, "sort_order": 40},
    {"level_code": "PANDA_LV5", "level_name": "功夫大师", "threshold_days": 60, "sort_order": 50},
    {"level_code": "PANDA_LV6", "level_name": "熊猫长老", "threshold_days": 100, "sort_order": 60},
    {"level_code": "PANDA_LV7", "level_name": "传奇熊猫", "threshold_days": 200, "sort_order": 70},
]

# 互动物品（PRD 4.1 单价表；sort_order 即平铺列表顺序，新物品追加到末尾）
DEFAULT_INTERACTION_ITEMS: list[dict[str, Any]] = [
    {
        "id": "coffee",
        "name": "咖啡",
        "icon": "☕",
        "icon_url": "",
        "cost_points": 5,
        "prompt_template_id": "coffee",
        "sort_order": 10,
        "is_active": True,
    },
    {
        "id": "noodles",
        "name": "泡面",
        "icon": "🍜",
        "icon_url": "",
        "cost_points": 7,
        "prompt_template_id": "noodles",
        "sort_order": 20,
        "is_active": True,
    },
    {
        "id": "gamepad",
        "name": "手柄",
        "icon": "🎮",
        "icon_url": "",
        "cost_points": 13,
        "prompt_template_id": "gamepad",
        "sort_order": 30,
        "is_active": True,
    },
    {
        "id": "white_dragon",
        "name": "白龙",
        "icon": "🚬",
        "icon_url": "",
        "cost_points": 16,
        "prompt_template_id": "white_dragon",
        "sort_order": 40,
        "is_active": True,
    },
    {
        "id": "energy_bar",
        "name": "能量棒",
        "icon": "🍫",
        "icon_url": "",
        "cost_points": 10,
        "prompt_template_id": "energy_bar",
        "sort_order": 50,
        "is_active": True,
    },
]

# Prompt 模板：以椎名立希“酷但笨拙”的人设撰写，渲染后拼进同一 session 的 system prompt。
# 可用占位符：{item_name} {item_icon} {cost_points} {item_id}
DEFAULT_PROMPT_TEMPLATES: dict[str, str] = {
    "coffee": (
        "【互动事件】Kris 刚刚送了你一杯{item_icon}{item_name}（消耗 {cost_points} 积分）。\n"
        "先看上文两处，再决定台词：\n"
        "1）【当前时段】（系统已判定，直接用）：\n"
        "   - 深夜：默认他也在陪你熬，用「既然都送来了」的共犯语气收下，别劝他去睡。\n"
        "   - 清晨 / 上午 / 下午 / 晚上：可以吐槽他挑时间、乱花钱，但照样收下，口气随刚才聊的内容走。\n"
        "2）【立希当前状态】里的「情绪底色」：心情不错就高兴地收下（是立希式别扭的高兴，不是撒娇甜话）；心情不好就一边嫌弃一边收下。\n"
        "反应方向：先别扭一句，然后收下；不要写「不要」「你自己喝」这类硬拒；不要直接说出自己的状态，心情只体现在语气里。\n"
        "输出要求：1～3 行短句，总长 40 字以内；严禁动作叙事或星号描写。"
    ),
    "noodles": (
        "【互动事件】Kris 刚刚给你送来一份{item_icon}{item_name}（消耗 {cost_points} 积分）。\n"
        "这是聊天中的虚拟礼物，不表示现实送餐、见面或已经吃过。不要假定当前是深夜或正在吃饭。\n"
        "反应方向：随口吐槽口味或热量，再简短领情；可以问他喜欢什么口味，不约现实中一起吃。\n"
        "输出要求：1～3 行短句，总长 40 字以内；严禁动作叙事或星号描写。"
    ),
    "gamepad": (
        "【互动事件】Kris 刚刚递给你一个{item_icon}{item_name}（消耗 {cost_points} 积分）。\n"
        "你要知道：你手痒了，会立刻摆出「来就来，输的人别哭」的挑衅姿态，其实很高兴有人陪。\n"
        "反应方向：挑衅约战 → 顺带关心他有没有时间。\n"
        "输出要求：1～3 行短句，总长 40 字以内；严禁动作叙事或星号描写。"
    ),
    "white_dragon": (
        "【互动事件】Kris 刚刚送了你一只{item_icon}{item_name}（消耗 {cost_points} 积分）——白龙是烟的代称，你清楚他递过来的是什么。\n"
        "先看上下文挑一种反应，只写一种，不要两种都写：\n"
        "- 嘴炮：他精神还行、就是来撩你/欠怼，就顺着怼回去（损他两句、接他的梗），但别真把东西推回去。\n"
        "- 关心：他今天状态不对（熬夜、赶工、心情差、喊累），就嘴硬地管他一句（少抽点、别一边写代码一边点），把关心藏在不耐烦里。\n"
        "反应方向：无论选哪种都收下，不要写「拿走」「我不要」。\n"
        "输出要求：1～3 行短句，总长 45 字以内；严禁动作叙事或星号描写。"
    ),
    "energy_bar": (
        "【互动事件】Kris 刚刚塞给你一根{item_icon}{item_name}（消耗 {cost_points} 积分）。\n"
        "先看上文两处，再决定台词：\n"
        "1）【当前时段】（系统已判定，直接用）：\n"
        "   - 深夜：摆明了他还打算继续熬，用「正好，我也没打算睡」的默契收下。\n"
        "   - 清晨 / 上午 / 下午 / 晚上：吐槽他把你当运动员使唤，但还是收下，顺便问他在忙什么。\n"
        "2）【立希当前状态】里的「情绪底色」：心情不错就用轻松的语气接住（允许「谢了，正好」这种别扭的领情）；心情不好就怼他一句多管闲事，但东西照样收。\n"
        "反应方向：一律收下，不做硬拒；不要直接说出自己的状态，心情只体现在语气里。\n"
        "输出要求：1～3 行短句，总长 40 字以内；严禁动作叙事或星号描写。"
    ),
}

# 积分规则：需求方已确认的数值（PRD 4.3 / 4.5 / 九）
DEFAULT_POINTS_RULES: dict[str, Any] = {
    "daily_first_chat_points": 1,
    "streak_cycle_days": 3,
    "streak_reward_points": 3,
    "anniversaries": [
        {"date": "12-26", "name": "纪念日", "points": 100},
        {"date": "08-09", "name": "Taki生日", "points": 100},
    ],
    "makeup_card": {
        "monthly_grant": 1,
        "max_available": 12,
        "grant_on_first_seen": True,
    },
    "break_gap_days": 5,
    "warning_gap_days": [3, 4],
    "interaction_daily_limit": None,
    "deepseek_retry": {
        # 2026-10-20 确认：完整生成/检查/重写共享 60 秒；超时仍退款。
        "max_retries": 5,
        "total_timeout_seconds": 60,
        "attempt_timeout_seconds": 20,
    },
}

CONFIG_SECTIONS = ("interaction_items", "prompt_templates", "level_config", "points_rules")


class ConfigValidationError(ValueError):
    pass


class ConfigUnavailableError(RuntimeError):
    pass


class ConfigRevisionConflict(ConfigValidationError):
    pass


def default_config() -> dict[str, Any]:
    return {
        "version": 1,
        "revision": 1,
        "updated_at": int(time.time() * 1000),
        "interaction_items": json.loads(json.dumps(DEFAULT_INTERACTION_ITEMS, ensure_ascii=False)),
        "prompt_templates": dict(DEFAULT_PROMPT_TEMPLATES),
        "level_config": json.loads(json.dumps(DEFAULT_LEVEL_CONFIG, ensure_ascii=False)),
        "points_rules": json.loads(json.dumps(DEFAULT_POINTS_RULES, ensure_ascii=False)),
    }


# ===================== 校验 =====================

def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ConfigValidationError(message)


def _as_int(value: Any, field: str, minimum: int | None = None) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        raise ConfigValidationError(f"{field} 必须是整数")
    if minimum is not None and parsed < minimum:
        raise ConfigValidationError(f"{field} 不能小于 {minimum}")
    return parsed


def _positive_seconds(value: Any, field: str) -> float:
    try:
        parsed = float(value)
    except (TypeError, ValueError):
        raise ConfigValidationError(f"{field} 必须是正数秒数") from None
    _require(math.isfinite(parsed) and parsed > 0, f"{field} 必须是有限正数秒数")
    return parsed


def validate_interaction_items(items: Any) -> list[dict[str, Any]]:
    _require(isinstance(items, list), "interaction_items 必须是数组")
    seen_ids: set[str] = set()
    normalized: list[dict[str, Any]] = []
    for index, item in enumerate(items):
        _require(isinstance(item, dict), f"interaction_items[{index}] 必须是对象")
        item_id = str(item.get("id") or "").strip()
        name = str(item.get("name") or "").strip()
        _require(bool(item_id), f"interaction_items[{index}].id 不能为空")
        _require(bool(name), f"interaction_items[{index}].name 不能为空")
        _require(item_id not in seen_ids, f"interaction_items 存在重复 id: {item_id}")
        _require(type(item.get("cost_points")) is int, "cost_points 必须是整数")
        seen_ids.add(item_id)
        normalized.append(
            {
                "id": item_id,
                "name": name,
                "icon": str(item.get("icon") or "").strip(),
                "icon_url": str(item.get("icon_url") or "").strip(),
                "cost_points": _as_int(item.get("cost_points"), f"{item_id}.cost_points", 1),
                "prompt_template_id": str(item.get("prompt_template_id") or item_id).strip(),
                "sort_order": _as_int(item.get("sort_order", (index + 1) * 10), f"{item_id}.sort_order"),
                "is_active": bool(item.get("is_active", True)),
            }
        )
    return normalized


def validate_prompt_templates(templates: Any) -> dict[str, str]:
    _require(isinstance(templates, dict), "prompt_templates 必须是对象")
    normalized: dict[str, str] = {}
    for key, value in templates.items():
        template_id = str(key).strip()
        _require(bool(template_id), "prompt_templates 存在空 key")
        text = str(value or "").strip()
        _require(bool(text), f"prompt_templates.{template_id} 不能为空")
        try:
            for _, field, spec, conversion in Formatter().parse(text):
                if field is not None:
                    _require(field in {"item_name", "item_icon", "cost_points", "item_id"}, "模板包含未知占位符")
            text.format(item_name="物品", item_icon="", cost_points=5, item_id="item")
        except (ValueError, KeyError, IndexError, AttributeError, TypeError):
            raise ConfigValidationError("模板格式错误") from None
        normalized[template_id] = text
    return normalized


def validate_level_config(levels: Any) -> list[dict[str, Any]]:
    _require(isinstance(levels, list), "level_config 必须是数组")
    _require(len(levels) > 0, "level_config 不能为空")
    seen: set[str] = set()
    normalized: list[dict[str, Any]] = []
    for index, level in enumerate(levels):
        _require(isinstance(level, dict), f"level_config[{index}] 必须是对象")
        code = str(level.get("level_code") or "").strip()
        name = str(level.get("level_name") or "").strip()
        _require(bool(code), f"level_config[{index}].level_code 不能为空")
        _require(code != "NONE", "level_config 不应包含默认态 NONE（系统内置）")
        _require(bool(name), f"level_config[{index}].level_name 不能为空")
        _require(code not in seen, f"level_config 存在重复 level_code: {code}")
        seen.add(code)
        normalized.append(
            {
                "level_code": code,
                "level_name": name,
                "threshold_days": _as_int(level.get("threshold_days"), f"{code}.threshold_days", 1),
                "sort_order": _as_int(level.get("sort_order", (index + 1) * 10), f"{code}.sort_order"),
            }
        )
    normalized.sort(key=lambda entry: (entry["threshold_days"], entry["sort_order"]))
    return normalized


def validate_points_rules(rules: Any) -> dict[str, Any]:
    _require(isinstance(rules, dict), "points_rules 必须是对象")
    anniversaries_raw = rules.get("anniversaries", [])
    _require(isinstance(anniversaries_raw, list), "points_rules.anniversaries 必须是数组")
    anniversaries: list[dict[str, Any]] = []
    for index, entry in enumerate(anniversaries_raw):
        _require(isinstance(entry, dict), f"anniversaries[{index}] 必须是对象")
        date_text = str(entry.get("date") or "").strip()
        _require(
            len(date_text) == 5 and date_text[2] == "-",
            f"anniversaries[{index}].date 需为 MM-DD 格式",
        )
        try:
            month = int(date_text[:2])
            day = int(date_text[3:])
        except ValueError:
            raise ConfigValidationError(f"anniversaries[{index}].date 需为 MM-DD 格式")
        _require(1 <= month <= 12, f"anniversaries[{index}].date 月份非法")
        _require(1 <= day <= 31, f"anniversaries[{index}].date 日期非法")
        anniversaries.append(
            {
                "date": date_text,
                "name": str(entry.get("name") or "纪念日").strip(),
                "points": _as_int(entry.get("points"), f"anniversaries[{index}].points", 0),
            }
        )

    makeup_raw = rules.get("makeup_card", {})
    _require(isinstance(makeup_raw, dict), "points_rules.makeup_card 必须是对象")

    retry_raw = rules.get("deepseek_retry", {})
    _require(isinstance(retry_raw, dict), "points_rules.deepseek_retry 必须是对象")

    warning_raw = rules.get("warning_gap_days", [3, 4])
    _require(isinstance(warning_raw, list), "points_rules.warning_gap_days 必须是数组")

    daily_limit = rules.get("interaction_daily_limit", None)
    if daily_limit is not None:
        _require(type(daily_limit) is int and daily_limit > 0, "interaction_daily_limit 必须是正整数或 null")

    return {
        "daily_first_chat_points": _as_int(rules.get("daily_first_chat_points", 1), "daily_first_chat_points", 0),
        "streak_cycle_days": _as_int(rules.get("streak_cycle_days", 3), "streak_cycle_days", 1),
        "streak_reward_points": _as_int(rules.get("streak_reward_points", 3), "streak_reward_points", 0),
        "anniversaries": anniversaries,
        "makeup_card": {
            "monthly_grant": _as_int(makeup_raw.get("monthly_grant", 1), "makeup_card.monthly_grant", 0),
            "max_available": _as_int(makeup_raw.get("max_available", 12), "makeup_card.max_available", 0),
            "grant_on_first_seen": bool(makeup_raw.get("grant_on_first_seen", True)),
        },
        "break_gap_days": _as_int(rules.get("break_gap_days", 5), "break_gap_days", 1),
        "warning_gap_days": sorted({_as_int(item, "warning_gap_days[]", 1) for item in warning_raw}),
        "interaction_daily_limit": daily_limit,
        "deepseek_retry": {
            "max_retries": _as_int(retry_raw.get("max_retries", 5), "deepseek_retry.max_retries", 0),
            "total_timeout_seconds": _positive_seconds(retry_raw.get("total_timeout_seconds", 60), "deepseek_retry.total_timeout_seconds"),
            "attempt_timeout_seconds": _positive_seconds(retry_raw.get("attempt_timeout_seconds", 20), "deepseek_retry.attempt_timeout_seconds"),
        },
    }


_SECTION_VALIDATORS = {
    "interaction_items": validate_interaction_items,
    "prompt_templates": validate_prompt_templates,
    "level_config": validate_level_config,
    "points_rules": validate_points_rules,
}


def validate_config(config: Any) -> dict[str, Any]:
    _require(isinstance(config, dict), "配置必须是对象")
    _require(type(config.get("version", 1)) is int and config.get("version", 1) == 1, "不支持的配置版本")
    _require(type(config.get("revision", 1)) is int and config.get("revision", 1) > 0, "revision 必须是正整数")
    base = default_config()
    result: dict[str, Any] = {"version": 1, "revision": _as_int(config.get("revision", 1), "revision", 1)}
    for section in CONFIG_SECTIONS:
        if section in config:
            result[section] = _SECTION_VALIDATORS[section](config[section])
        else:
            result[section] = base[section]
    # 物品引用的模板必须存在，否则该物品无法生成回复
    templates = set(result["prompt_templates"].keys())
    for item in result["interaction_items"]:
        _require(
            item["prompt_template_id"] in templates,
            f"物品 {item['id']} 引用了不存在的 Prompt 模板: {item['prompt_template_id']}",
        )
    result["updated_at"] = int(time.time() * 1000)
    return result


# ===================== 服务 =====================

class InteractionConfigService:
    def __init__(self, config_dir: str, logger, filename: str = DEFAULT_CONFIG_FILENAME, *, initialize=False):
        self.config_dir = config_dir
        self.path = os.path.join(config_dir, filename)
        self.last_good_path = self.path + ".last-good"
        self.logger = logger
        self._lock = threading.RLock()
        self._cached = None
        self.degraded = False
        os.makedirs(config_dir, exist_ok=True)
        if initialize:
            with self._publication_lock():
                if not os.path.exists(self.path) and not os.path.exists(self.last_good_path):
                    self._publish(default_config())

    @contextmanager
    def _publication_lock(self):
        deadline = time.monotonic() + 10
        if not self._lock.acquire(timeout=10):
            raise FileLockTimeoutError("configuration thread lock timed out")
        try:
            fd = os.open(self.path + ".lock", os.O_CREAT | os.O_RDWR, 0o600)
            locked = False
            try:
                acquire_file_lock(fd, exclusive=True, timeout=max(0, deadline - time.monotonic()))
                locked = True
                yield
            finally:
                try:
                    if locked:
                        release_file_lock(fd)
                finally:
                    os.close(fd)
        finally:
            self._lock.release()

    def _write_atomic(self, config, path=None):
        target = path or self.path
        fd, tmp = tempfile.mkstemp(prefix=".gamification-", dir=self.config_dir)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                json.dump(config, handle, ensure_ascii=False, indent=2)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(tmp, target)
        finally:
            if os.path.exists(tmp):
                os.unlink(tmp)

    @staticmethod
    def _contents(config):
        return {key: config[key] for key in ("version", *CONFIG_SECTIONS)}

    def _read(self, path):
        with open(path, encoding="utf-8") as handle:
            raw = json.load(handle)
        _require(isinstance(raw, dict) and all(key in raw for key in CONFIG_SECTIONS), "配置段缺失")
        config = validate_config(raw)
        config["updated_at"] = raw.get("updated_at", config["updated_at"])
        return config

    def _publish(self, config):
        # 审计版本不可覆盖；两份持久副本均写成才向管理调用方报告成功。
        archive = self.path + f".revision-{config['revision']}"
        if not os.path.exists(archive):
            self._write_atomic(config, archive)
        self._write_atomic(config, self.last_good_path)
        self._write_atomic(config)
        self._cached = config
        self.degraded = False

    def _next_revision(self, current):
        prefix = os.path.basename(self.path) + ".revision-"
        revisions = [int(name[len(prefix):]) for name in os.listdir(self.config_dir)
                     if name.startswith(prefix) and name[len(prefix):].isdigit()]
        return max([current, *revisions]) + 1

    def _load_locked(self):
        try:
            good = self._read(self.last_good_path)
        except (OSError, ValueError, TypeError):
            good = self._cached
        try:
            current = self._read(self.path)
            if good is not None:
                if self._contents(current) != self._contents(good):
                    current["revision"] = self._next_revision(good["revision"])
                    self._publish(current)
                else:
                    current = good
            else:
                self._publish(current)
            self._cached = current
            self.degraded = False
            return current
        except (OSError, ValueError, TypeError) as error:
            self.degraded = True
            self.logger.warning("[配置] 当前配置不可用（%s）", type(error).__name__)
            if good is None:
                raise ConfigUnavailableError("礼物配置不可用") from None
            self._cached = good
            return good

    def get_config(self):
        with self._publication_lock():
            return copy.deepcopy(self._load_locked())

    def get_active_items(self):
        items = [item for item in self.get_config()["interaction_items"] if item["is_active"]]
        return sorted(items, key=lambda entry: (entry["sort_order"], entry["id"]))

    def get_item(self, item_id):
        return next((item for item in self.get_config()["interaction_items"] if item["id"] == item_id), None)

    def _non_payment_config(self):
        try:
            return self.get_config()
        except ConfigUnavailableError:
            # 只供签到、等级等非支付入口；新扣款必须读取严格快照。
            return default_config()

    def get_level_table(self):
        return self._non_payment_config()["level_config"]

    def get_points_rules(self):
        return self._non_payment_config()["points_rules"]

    def get_prompt_template(self, template_id):
        return self.get_config()["prompt_templates"].get(template_id)

    def render_prompt(self, item, *, config=None):
        config = config if config is not None else self.get_config()
        template = config["prompt_templates"][item.get("prompt_template_id") or item["id"]]
        return template.format(item_name=item.get("name", ""), item_icon=item.get("icon", ""),
                               cost_points=item["cost_points"], item_id=item["id"])

    def interaction_snapshot(self, item_id):
        config = self.get_config()
        item = next((item for item in config["interaction_items"] if item["id"] == item_id), None)
        return config, item

    def update_sections(self, payload, *, expected_revision=None):
        with self._publication_lock():
            unknown = set(payload) - set(CONFIG_SECTIONS)
            if unknown:
                raise ConfigValidationError("未知配置段")
            current = self._load_locked()
            if expected_revision is not None and expected_revision != current["revision"]:
                raise ConfigRevisionConflict("配置版本已改变，请刷新后重试")
            config = validate_config({**current, **payload})
            config["revision"] = self._next_revision(current["revision"])
            self._publish(config)
            return copy.deepcopy(config)

    def reset_to_default(self, *, expected_revision=None):
        return self.update_sections({key: default_config()[key] for key in CONFIG_SECTIONS},
                                    expected_revision=expected_revision)
