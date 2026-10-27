-- MediaRack — schema v5
-- 主题：single 条目的内容指纹台账。
--
-- 背景：single 条目的 key = sha1(rel_path)，meta 挂在 (source_id, key) 上。
-- 路径一动（整理文件夹 / 移动 / 换源重加）key 就换新，用户的旋转/隐藏/标签
-- 全部悬空。dual 条目没有这个问题——key 是 B站内容身份（avid_cid）。
--
-- 方案：给 single 文件算内容指纹（大小 + 首尾 64KB 的 SHA-256），记在一张
-- **全局**台账里（跨源、跨源移除/重加都活着）。扫描遇到新位置时按指纹找到
-- 旧位置，把 media_meta 认领过来；台账只负责"指路"，meta 始终只有一份真身。
--
-- 一个 fp 只记最近一次见到的位置：同内容多份共存时，第二份也能按指纹认领
-- 同一套设置（语义见用户拍板："只要确定是同一个视频，就用它的设置"）。

CREATE TABLE IF NOT EXISTS content_fp (
    fp         TEXT PRIMARY KEY,        -- 内容指纹，hex
    source_id  TEXT NOT NULL,           -- 该内容最近一次被扫描到的位置
    key        TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    updated_at INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_content_fp_loc ON content_fp(source_id, key);
