-- MediaRack — schema v3
-- 主题：一个源挂多个文件夹，布局方式跟着文件夹走。
--
-- 设计决定：
--   1. 文件夹不是独立实体，而是"源下的一个列表行"。"一个文件夹可以出现在多个源里"
--      指的是路径级共享（两个源各登记同一路径），不是外键共享——
--      这样增删重排都只是本源内部的行操作，不会牵连别的源。
--   2. layout 存在文件夹行上（auto | single | dual）。auto 在扫描时现场识别并写回。
--   3. source 表的 root / layout 列退役但保留（SQLite 改 NOT NULL 要重建表，不值）：
--      Upsert 继续往里写首个文件夹（兼容一切还读这两列的旧代码），真相以 source_folder 为准。
--   4. media 表不加 folder 关联：条目的 rel_path 相对"扫描它时的文件夹根"，
--      定位时按文件夹顺序 File.Exists 探测即可命中；key 规则不变，老库迁移后无需重扫。

CREATE TABLE IF NOT EXISTS source_folder (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    source_id TEXT NOT NULL,
    position  INTEGER NOT NULL DEFAULT 0,
    root      TEXT NOT NULL,                  -- 绝对路径，仅本机有效
    layout    TEXT NOT NULL DEFAULT 'auto',   -- auto | single | dual
    UNIQUE(source_id, root)
);

CREATE INDEX IF NOT EXISTS idx_source_folder_source ON source_folder(source_id);

-- 老库迁移：每个源的单一根目录成为它的第一个文件夹，布局沿用原值。
INSERT INTO source_folder (source_id, position, root, layout)
    SELECT id, 0, root, layout FROM source
    WHERE root IS NOT NULL AND TRIM(root) != ''
    ORDER BY id;
