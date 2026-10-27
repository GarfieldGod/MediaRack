-- MediaRack — schema v4
-- 主题：合集 = 源级结构（排他归属、只读）；"收藏"独立成一层（多对多、可跨端）。
--
-- 四个设计决定：
--   1. 排他用单列而不是关联表。collection_item 是多对多结构，靠"每条只写一行"实现排他
--      是软约束；media.collection_id 把"这个视频属于哪个合集"写在条目行上，写死、查得快。
--   2. 合集 id 保持语义化、可调试：
--        single（文件夹）：dir:<源>:/<相对目录>   （源根是 dir:<源>:/）
--        dual（B站缓存）：up:<源>:<up_mid>        （缺 owner 的是 up:<源>:unknown）
--      合集行由代码全量重建（CollectionRebuilder），启动与每次扫描后幂等执行——
--      名字取"目录末段"这类逻辑写在 SQL 里会变成一坨 substr/replace。
--   3. 平铺，不要树：父目录没有视频就不成合集（用户明确要求），所以 parent_id 一律 NULL。
--      留着这一列是为了不改表结构，不是打算用。
--   4. 收藏与合集彻底分家：favorite* 三张表是"用户层"，可以多对多、可以跨源；
--      合集是"结构层"，只由磁盘（single）或 entry.json（dual）决定。两者不再共用表。

ALTER TABLE collection ADD COLUMN source_id TEXT;
ALTER TABLE collection ADD COLUMN kind      TEXT NOT NULL DEFAULT 'folder';  -- folder | up
ALTER TABLE collection ADD COLUMN path      TEXT;                            -- folder: 相对目录；up: up_mid

ALTER TABLE media ADD COLUMN collection_id TEXT;

-- ── 收藏夹（用户层）─────────────────────────────────────
CREATE TABLE IF NOT EXISTS favorite (
    id         TEXT PRIMARY KEY,             -- 沿用老 col:<...> 的 id，新建的是 fav:<uuid>
    name       TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_favorite_name ON favorite(name);

CREATE TABLE IF NOT EXISTS favorite_item (
    favorite_id TEXT NOT NULL,
    source_id   TEXT NOT NULL,
    key         TEXT NOT NULL,
    added_at    INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (favorite_id, source_id, key)
);

CREATE TABLE IF NOT EXISTS favorite_collection (
    favorite_id   TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    added_at      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (favorite_id, collection_id)
);

-- 老的手工收藏夹（col:）原样迁进 favorite*——id 不改，已配对的客户端不用重扫。
INSERT INTO favorite (id, name, sort_order, created_at)
    SELECT id, name, COALESCE(sort_order, 0), 0 FROM collection WHERE id LIKE 'col:%';

INSERT INTO favorite_item (favorite_id, source_id, key, added_at)
    SELECT collection_id, source_id, key, 0 FROM collection_item WHERE collection_id LIKE 'col:%';

-- 合集表清空：v4 的合集行全部由 CollectionRebuilder 按 media 重建。
DELETE FROM collection;

-- 关联表退役（自动合集改由 media.collection_id 承载）。收藏已经搬走，没有别的东西用它了。
DROP TABLE IF EXISTS collection_item;
