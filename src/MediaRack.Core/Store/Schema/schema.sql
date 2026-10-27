-- MediaRack — schema v1
-- 与 MediaRack.Core/Store/Migrations 严格等价：本文件即 v1 迁移的全文。
-- 设计约束：
--   1. 一切条目以 (source_id, key) 复合键标识，"源独立"由结构保证
--   2. 只存相对路径，换盘/换挂载点不失效
--   3. 不存在任何指向源目录的写路径；虚拟层只写本库

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

-- ── 版本表 ──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS schema_version (
    version    INTEGER PRIMARY KEY,
    name       TEXT    NOT NULL,
    applied_at INTEGER NOT NULL          -- unix ms
);

-- ── 源 ──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS source (
    id            TEXT PRIMARY KEY,
    name          TEXT NOT NULL,
    layout        TEXT NOT NULL,                  -- dual | single
    root          TEXT NOT NULL,                  -- 绝对路径，仅本机有效
    virtual_write TEXT NOT NULL DEFAULT 'full',   -- none | whitelist | full
    enabled       INTEGER NOT NULL DEFAULT 1,
    scan_include  TEXT,                           -- 通配过滤，可空
    last_scan_at  INTEGER,
    last_count    INTEGER
);

-- ── 条目 ────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS media (
    source_id   TEXT NOT NULL,
    key         TEXT NOT NULL,                    -- dual: <avid>_c<cid> | single: s_<sha1(relpath)[:16]>
    rel_path    TEXT NOT NULL,
    rel_dir     TEXT,                             -- 物理目录（single 源即合集名）
    layout      TEXT NOT NULL,
    title_raw   TEXT,
    up_name     TEXT,
    up_mid      TEXT,
    duration_ms INTEGER,
    width       INTEGER,
    height      INTEGER,
    codec       TEXT,
    tag_code    INTEGER,                          -- dual 源清晰度档位
    size_bytes  INTEGER,
    poster_rel  TEXT,                             -- 封面/抽帧统一入口，相对 meta_root
    has_danmaku INTEGER DEFAULT 0,
    status      TEXT DEFAULT 'ok',                -- ok | missing | corrupt
    scanned_at  INTEGER,
    PRIMARY KEY (source_id, key)
);

-- ── 分轨（dual 源；single 源也可登记一条 video 轨）────────
CREATE TABLE IF NOT EXISTS media_track (
    source_id  TEXT NOT NULL,
    key        TEXT NOT NULL,
    role       TEXT NOT NULL,                     -- video | audio
    codecid    INTEGER,
    bandwidth  INTEGER,
    size_bytes INTEGER,
    md5        TEXT,
    PRIMARY KEY (source_id, key, role)
);

-- ── 虚拟元数据（唯一可写层，磁盘永不变）──────────────────
CREATE TABLE IF NOT EXISTS media_meta (
    source_id     TEXT NOT NULL,
    key           TEXT NOT NULL,
    title_display TEXT,
    tags          TEXT,                           -- JSON array
    hidden        INTEGER DEFAULT 0,
    rotation      INTEGER DEFAULT 0,              -- 0/90/180/270 覆盖
    play_pos_ms   INTEGER DEFAULT 0,
    play_count    INTEGER DEFAULT 0,
    last_play_at  INTEGER,
    rev           INTEGER NOT NULL DEFAULT 1,     -- 到达序 LWW
    updated_at    INTEGER,
    updated_by    TEXT,
    PRIMARY KEY (source_id, key)
);

-- ── 收藏夹（多对多，支持树）──────────────────────────────
CREATE TABLE IF NOT EXISTS collection (
    id         TEXT PRIMARY KEY,
    name       TEXT NOT NULL,
    parent_id  TEXT,
    sort_order INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS collection_item (
    collection_id TEXT NOT NULL,
    source_id     TEXT NOT NULL,
    key           TEXT NOT NULL,
    sort_order    INTEGER DEFAULT 0,
    PRIMARY KEY (collection_id, source_id, key)
);

-- ── 继续观看（单行）─────────────────────────────────────
CREATE TABLE IF NOT EXISTS last_session (
    id        INTEGER PRIMARY KEY CHECK (id = 1),
    source_id TEXT,
    key       TEXT,
    view      TEXT,
    pos_ms    INTEGER,
    at        INTEGER
);

-- ── 索引 ────────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_media_source ON media(source_id);
CREATE INDEX IF NOT EXISTS idx_media_dir    ON media(source_id, rel_dir);
CREATE INDEX IF NOT EXISTS idx_media_title  ON media(title_raw);
CREATE INDEX IF NOT EXISTS idx_media_status ON media(source_id, status);
CREATE INDEX IF NOT EXISTS idx_item_key     ON collection_item(source_id, key);
