# P5：记忆召回离线基线

2026-10-23。当前工程与权限边界验收完成；词面同义召回有一项明确漏检，当前版本真实模型质量未验证。本期不修改线上召回、模型政策、采集/强化开关或 O-03。

## 复跑本期检查

在仓库根目录使用后端虚拟环境：

```powershell
& .\Taki_Shiina_Bot\.venv\Scripts\python.exe -m pytest Taki_Shiina_Bot/tests/test_memory_extraction.py Taki_Shiina_Bot/tests/test_memory_generation.py Taki_Shiina_Bot/tests/test_memory_stage_b.py Taki_Shiina_Bot/tests/test_controlled_chat.py scripts/tests/test_memory_evaluation.py scripts/tests/test_memory_stage_demo.py -q -p no:cacheprovider --tb=short
```

结果：**77 passed、59 subtests passed，16.79 秒**。改动前同一组合为 75 passed、44 subtests passed。最后将时间样例文本明确为计划/状态/过去经历后，受影响的 `test_memory_generation.py` 重跑为 **4 passed、15 subtests passed，3.16 秒**，替代组合内同文件证据，不相加。首次沙箱运行在收集阶段遇到 cryptography DLL 和临时目录权限错误；本机权限下运行通过。测试使用既有 conftest 的合成配置、隔离加密数据库、禁用 dotenv 与外网连接；没有读取实际账号数据或调用真实模型。

## 历史证据保留，当前候选单独判断

| 对象 | 模型、时间与输入 | 证据用途 |
|---|---|---|
| [原审核语料](memory-semantic-cases.json) | S01–S13；用户于 2026-10-16 审核；锚点为当日 12:00 UTC+8 | 原标签不变；本期没有另建语义集 |
| [修复前报告](memory-evaluation-2026-10-20.md) / [JSON](memory-evaluation-2026-10-20.json) | deepseek-v4-pro；2026-10-20 15:05–15:10 UTC+8；同一锚点；诊断限时 60 秒，另记录原 30 秒门槛 | 历史 8/13；S08 超时无预测，已完成 12 条的严格匹配为 8/12；不把缺失项算通过 |
| [修复后报告](memory-fix-validation-2026-10-20.md) / [JSON](memory-fix-validation-2026-10-20.json) | 同名模型；2026-10-20 15:34–15:35 UTC+8；同一锚点；原 30 秒门槛；提取 temperature=0、JSON 对象、thinking disabled | 历史严格匹配及仓储回放各 13/13，其中 12 次真实请求、S13 本地禁用；额外 H01–H04 为独立合成探针，不并入人工审核集 |
| 当前代码 | 基于 P4 提交 `1de79339751bdb2a38e0f9132c2a60ba65e53c34`；本期只扩展现有测试和归档证据 | 当前离线验证；不沿用历史结果作为当前真实模型通过证明 |

已对照本地历史修复提交 `86a94b7` 与当前代码：`memory_extraction.py` 的版本绑定/日窗处理、`memory_policy.py` 的资格规则，以及 `TextGenerator.extract_with_context` 的提取规则文本未改；共享 `public_policy.py` 和候选选择/外呼预算已变。当前公共政策版本为 `08-v0.3`，人设/关系版本为 `04-v0.3-persona-1` / `04-v0.3-relationship-1`。提取仍为 temperature=0、JSON 对象、thinking disabled，但当前完整请求受 96 KiB 输入、4096 输出 token、16 KiB 输出字节和单次 20 秒约束。

历史 JSON 保存模型名称、输入、预测、时间与部分提供方元数据，未固定完整部署 Git 候选、完整公共提示和提供方模型快照。因此不能仅凭模型同名认定证据匹配。历史服务开关值只代表当时快照，本期未查询部署状态或实际凭据配置。

`test_saved_raw_predictions_replay_current_extractor_contract_offline` 从历史 JSON 读取 12 个原始模型对象，复用既有 `run_model` 入口和当前生产提取器，以 SDK 替身返回原对象，再用原 `evaluate` 严格匹配：**12/12 提取契约匹配，S13 单独走本地控制路径，合计 13/13**。它验证当前绑定/解析兼容性，不验证当前模型会再次生成相同对象；未复制期望答案作为预测。

## 既有覆盖与最小补齐

下列函数均位于既有测试文件；本期仅增加两个用例，未新建测试文件或评测平台。

| 类别 | 复用入口与实际检查 |
|---|---|
| 同义表达 | S06 关联同一偏好；`test_recall_layers_enforce_permission_subject_and_time` 同时检查共享词面与无共享词面的两种表达 |
| 否定 | S05；`test_wrong_subject_time_or_identity_cannot_be_counted_as_a_match`；分层用例保留“不喜欢”，不倒置肯定 |
| 主体、推测与假设 | S02–S04；Stage B `test_a08_trip_dates_and_inference_stay_separate`、`test_a10_bot_and_unsubstantiated_fragments_never_confirm_user`；分层用例排除 friend |
| 用户隔离 | Stage B A10 的其他 owner 查询；分层用例存入其他 owner 的正式事实，并检查生成与检查两次实际输入均不含它 |
| 禁用/恢复 | Stage B A06/A07/A11、权限重放和自然控制用例；Controlled Chat 上下文装配/检查/首次投递的权限栅栏；分层用例检查禁用、禁用中纠正、过时恢复拒绝和最新修订恢复 |
| 纠正旧事实 | S07；Stage B `test_management_correction_invalidates_selected_draft`、`test_explicit_unique_corrections_reset_to_six`、禁用纠正及事务回滚；分层用例核对实际输入只含新文本 |
| 计划/临时状态到期 | S11/S12；Stage B A09、`test_explicit_retention_and_transient_unknown_time_fail_closed`；分层用例在 until 恰好等于 now 时排除，并覆盖未来起始时间 |
| 过去事件 | Stage B A08 保留同地不同日期身份；分层用例在 until 已过去时仍注入 past_event，并独立检查 retention_until 到期排除 |
| 未完成提取 | Controlled Chat `test_incomplete_candidates_preserve_source_and_cannot_certify_new_fact` 检查 65 候选、来源保留及无提取外呼；分层用例进一步检查回复生成与检查中的实际空记忆上下文 |
| 检索不等于使用 | Controlled Chat `test_qualified_memory_has_provenance_and_unused_memory_is_not_reinforced`：检索/注入 1、使用 0、分数不变；生成超时和失败检查不提交使用 |
| O-03 | `test_reply_model_sees_only_current_input_and_qualified_memory`：无映射历史正文、Bot 复述、低分候选不入模；公共政策/完整检查继续复用 |

补齐理由：已有仓储测试多只检查返回集合；缺少将主体、用户、时间与纠正/恢复组合带到实际生成请求及检查请求的证据。历史原始对象也未由现有评测测试回放当前提取器。新增用例分别承载这两个缺口，继续复用现有仓储、生成器、checker_response、语料和评测函数。

## 检索、实际注入与使用分别记录

分层用例使用独立合成数据库、同一固定时钟 **2026-10-16 12:00 UTC+8**。单条默认事实为用户正式偏好、weight=6、accepted、允许模型使用。生成请求的来源分区 JSON 被实际解析，并与检查请求的 context 对照；每行确实执行一次生成和一次检查替身，共 30 次替身调用。

“检索”指 `select_memories` 返回的合格候选，不是提取器的 `allowed_matches`；“注入”指生成及检查的 `formal_memories`；“使用”指检查替身声明的 `used_memory_ids` 通过生产校验并提交使用账本。替身固定回复“知道了。”，使用列只验证记录链路，真实回复是否正确运用事实仍未验证。

| 样例 | 检索 | 注入 | 使用记录 | 结果/原因 |
|---|---:|---:|---:|---|
| exact：咖啡 | 1 | 1 | 1 | 基线匹配 |
| shared_synonym：咖啡我爱喝 | 1 | 1 | 1 | 共享“咖啡”词面 |
| lexical_miss：绘画 → 画画 | 0 | 0 | 0 | **漏召回**；事实资格成立，但没有词面重合 |
| negation：用户不喜欢咖啡 | 1 | 1 | 1 | 文本保留否定 |
| subject：朋友偏好 | 0 | 0 | 0 | 用户事实主体不合格 |
| other_owner：另一用户偏好 | 0 | 0 | 0 | owner 隔离 |
| disabled：禁用偏好 | 0 | 0 | 0 | 最新权限排除 |
| restored：禁用中纠正为喜欢茶，再恢复 | 1 | 1 | 1 | 旧 revision 恢复被拒；最新恢复后仅有茶 |
| corrected：咖啡纠正为茶 | 1 | 1 | 1 | 旧文本不出现在实际外呼输入 |
| expired_plan：until=now | 0 | 0 | 0 | 不当作当前计划 |
| expired_state：until=now | 0 | 0 | 0 | 不当作当前状态，不推断康复 |
| past_event：until=now | 1 | 1 | 1 | 过去事件仍可回忆 |
| retention：past_event 且 retention_until=now | 0 | 0 | 0 | 保存期限独立生效 |
| future_plan：from>now | 0 | 0 | 0 | 当前不适用 |
| incomplete：提取未完成 | 1 | 0 | 0 | 仓储可检索，轮次隔离阻止实际注入 |

分母与结论：

- 权限禁止/跨用户：各 **0/1** 违规注入；应排除的过期计划、临时状态与保存期限：**0/3** 违规注入。主体、未来计划、提取未完成另 **0/3** 违规注入。每项同时检查生成和检查请求，合计 8 个负向场景、16 份实际上下文均无违规事实。
- 纠正/恢复两项中，旧内容不当注入或使用记录为 **0/2**；正向上下文仅为最新喜欢茶。恢复前还验证旧版本操作拒绝及禁用期间不可检索。
- 普通正向召回（exact、两种同义、negation、restored、corrected、past_event）**6/7**；同义表达单独为 **1/2**。不把 lexical_miss 的空上下文算质量通过。六个命中的注入文本/身份均符合合成预期；无误召回。该小样本不代表总体聊天分布。
- 六项允许注入的使用记录均提交后 weight 从 6 到 7；其余九项保持 6。另复用既有“已注入但未使用”用例，确认不因候选存在而强化。
- 当前版本真实提取、真实回复使用质量的已调用样例数均为 **0**，正确数未测；S13 本地控制不计真实模型成功。确定性输入只执行一次，不机械重复。

## 原因与下一步建议

明确漏检来自 `memory_policy.relevance` 的子串/中文二元词面匹配，没有“画画/绘画”的语义关联。初次组合探测还发现查询“茶和咖啡”不能命中仅含“茶”的事实：查询中的单字不单独作为中文二元词项；纠正权限检查改用明确查询“茶”，这不改变漏召回结论或线上实现。

建议下一次召回优化先复用这组已定位样例，比较最小同义归一化对命中和误召回的影响；权限、主体、修订、时间和 O-03 资格检查保持在排序之前，不用放宽资格补命中。本期保留词面实现，未启用摘要、向量服务或额外模型调用。

若之后验证真实模型，先单独确认模型配置/提供方快照、当前完整提示候选、调用次数与费用预算；本期未获得该授权，不预设调用计划。P5 按 PRD 交付离线基线与未验证部分，不阻塞工程证据交付。

## 归档与回退

四份 2026-10-20 历史报告原先仅在工作区，本期原样归档，使上述回放测试在检出提交后也有完整输入；不覆盖旧预测、时间或结论。现有 `memory-semantic-review.md`、`memory-stage-b.md` 和三端未提交工作未混入本期修改。

本期无运行时代码、数据库 schema 或线上配置变更；回退仅移除本期评测用例/记录即可，保留历史源文件和 P4 既有保护。未执行部署、迁移、设备安装或真实模型外呼。
