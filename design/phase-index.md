# 分期与规格来源索引

Fix-Refactor P0–P6 是缺陷修复轮次，不是旧 PRD02–08、阶段 A–E 或 M0/M6 的重新编号。以下对应按现存记录的明确引用整理；交付范围以各记录为准。

| 本套范围 | 现存交付来源 | 与旧分期的交集 |
|---|---|---|
| P0 / P1 契约、支付与配置 | [交付记录](fix-refactor-p0-p1.md)，提交 `6902a57` | 阶段 A 的可靠互动与原积分体系；不是旧 PRD 全量重做 |
| P2 双来源游标、三端恢复 | 提交 `2d8ab4b`；当前合同见[图片与历史检查点](backend-entrypoints.md#图片与历史检查点) | 阶段 A / 三端同步；与 PRD-06 的多端接口有交集 |
| P3 连接、异步存储 | [交付记录](fix-refactor-p3.md)，提交 `48b72f7` | 横切基础连接和存储，不对应单一旧 PRD |
| P4 输入预算、错误动作 | [交付记录](fix-refactor-p4.md)，提交 `1de7933` | 阶段 C 的生成/检查及所有外呼边界 |
| P5 记忆召回评测、同义修复 | [交付记录](fix-refactor-p5.md)，提交 `64aee71` / `6e9d66b` | 阶段 B / PRD-02 记忆；P4 后的新候选需自己的证据 |
| P6 协议、装配和结构清理 | [交付记录](fix-refactor-p6.md) | 横切兼容入口与维护文档，不是旧第六阶段 |

| 旧名称 | 可检索权威记录 | 已明确的范围 / 未知对应 |
|---|---|---|
| 阶段 A | [foundation-stage-a](foundation-stage-a.md) | 记录明确引用 01 F01–F06 的可靠链路；不能据此认定 02–08 全交付 |
| 阶段 B / PRD-02 v0.3 | [memory-stage-b](memory-stage-b.md) | 记忆、权限、证据与分数；历史语义报告见 P5 来源表 |
| 阶段 C / PRD-03、08 v0.3 | [controlled-chat-stage-c](controlled-chat-stage-c.md) | 受控聊天、公共约束与文字体验 |
| 阶段 D / PRD-04 v0.3 | [character-daily-stage-d](character-daily-stage-d.md) | 角色日常、人设；人工风格与真实模型按原门槛 |
| 阶段 E / PRD-05 v0.3 | [proactive-messages-stage-e](proactive-messages-stage-e.md) | 主动消息、频率与来源权限 |
| PRD-06 | [foundation-contracts](foundation-contracts.md)、阶段 E 的交集说明 | 三端事件/记忆/通知的局部依据；完整原规格与独立完成范围待补回 |
| PRD-07 | 当前无完整原规格 | 编号和交付对应未知，不据相邻编号推断 |
| M0 | [verify_m0.py](../scripts/verify_m0.py)、[inventory_m0.py](../scripts/inventory_m0.py) | 原 `docs/m0/README.md`、数据字典、台账及部分 fixture 缺失；脚本显式检查缺件，不能将其描述为当前完整可复现门禁 |
| Windows M6 | [发布与人工验收](windows-release.md) | Windows 安装包和发布候选，不等同 P6 或后端阶段 E |

## 缺失原规格不靠删链接掩盖

当前入库材料不能还原完整 PRD02–08、`TKS_互动积分等级体系_PRD_v2.md`、M0 原手册和 Windows M5/PRD §16 清单。原文负责人需补回版本明确的源文件后，才能审计其全量交付。现存实施记录只证明所列局部范围，不能替代缺失规格。

本轮 P6 需求来自用户指定的仓库旁 `Fix-Refactor PRD/00_总则与执行索引.md` 与 `P6_第六期_协议收敛与结构清理.md`（v0.2，2026-10-21）；它们是需求来源，完成证据在本仓库交付记录中。旧本地 `docs/开发环境_Windows.md`、`Taki_Shiina_Bot/docs/部署_nginx与多进程路由.md` 和 Windows M6 指南存在未入库/过时段落，README 改指向本轮按代码核对的可随提交分发版本，旧本地文件保持原样。
