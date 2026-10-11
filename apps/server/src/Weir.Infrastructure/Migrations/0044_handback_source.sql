-- Weir schema 0044 (revision 0079_handback_source): the source a hand-back copy was cleaned from, kept with the copy.
--
-- "A source is cleaned once" was decided from the file's row, so a file taken off the list was cleaned again if it came back
-- unchanged. The new copy started the hand-back's story over and a media manager's earlier "imported" was lost, so the file
-- waited for a manager that had already taken it. The hand-back row is keyed by path, not by the file's row, so it now
-- carries what it needs to recognise the same source.
ALTER TABLE handbacks ADD COLUMN source_size BIGINT;

ALTER TABLE handbacks ADD COLUMN source_mtime_ns BIGINT;

-- 1. A copy whose file is still listed: the source its row recorded when the pass finished.
UPDATE handbacks
SET source_size = (
        SELECT f.processed_source_size FROM files f
        WHERE f.library_id = handbacks.library_id AND f.relative_path = handbacks.relative_path),
    source_mtime_ns = (
        SELECT f.processed_source_mtime_ns FROM files f
        WHERE f.library_id = handbacks.library_id AND f.relative_path = handbacks.relative_path)
WHERE EXISTS (
    SELECT 1 FROM files f
    WHERE f.library_id = handbacks.library_id AND f.relative_path = handbacks.relative_path
      AND f.processed_source_size IS NOT NULL AND f.processed_source_mtime_ns IS NOT NULL);

-- 2. A copy whose file was taken off the list: the record of the pass that wrote the copy, kept in the file's history, names
--    the source it measured and the copy it wrote. The newest such record is the one for the copy that is there now. A
--    record that was shortened when it was saved names neither, and leaves the copy as it is.
UPDATE handbacks
SET source_size = (
        SELECT CASE WHEN json_valid(l.detail_json) THEN json_extract(l.detail_json, '$.source_fingerprint_size') END
        FROM file_logs l
        WHERE l.relative_path = handbacks.relative_path
          AND CASE WHEN json_valid(l.detail_json) THEN json_extract(l.detail_json, '$.output_file') END = handbacks.output_path
          AND CASE WHEN json_valid(l.detail_json) THEN json_extract(l.detail_json, '$.output_collision_action') END = 'write'
        ORDER BY l.id DESC LIMIT 1),
    source_mtime_ns = (
        SELECT CASE WHEN json_valid(l.detail_json) THEN json_extract(l.detail_json, '$.source_fingerprint_mtime_ns') END
        FROM file_logs l
        WHERE l.relative_path = handbacks.relative_path
          AND CASE WHEN json_valid(l.detail_json) THEN json_extract(l.detail_json, '$.output_file') END = handbacks.output_path
          AND CASE WHEN json_valid(l.detail_json) THEN json_extract(l.detail_json, '$.output_collision_action') END = 'write'
        ORDER BY l.id DESC LIMIT 1)
WHERE source_size IS NULL;

-- A copy with only one of the two is as good as one with neither: the pair is what is compared.
UPDATE handbacks
SET source_size = NULL, source_mtime_ns = NULL
WHERE source_size IS NULL OR source_mtime_ns IS NULL;
