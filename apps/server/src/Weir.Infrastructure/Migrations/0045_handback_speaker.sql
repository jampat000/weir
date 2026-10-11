-- Weir schema 0045 (revision 0080_handback_speaker): who said what about a copy, and which hand-off an answer rides on.
--
-- Hand-offs of one file, from several connections and several managers, share the one copy Weir wrote, so what the copy says
-- is the word of one speaker at a time. The word was kept under the manager's display name, and two Deluno connections both
-- read "Deluno", so one connection's import could release the copy another had refused. The copy now keeps who spoke: the kind
-- of manager, the connection when Weir could tell, and whether the message proved who sent it.
ALTER TABLE handbacks ADD COLUMN outcome_source_key TEXT;

ALTER TABLE handbacks ADD COLUMN outcome_connection_id INTEGER;

-- 0 for a word that carried no secret (anybody could have sent it), which a signed word may replace and which replaces nothing.
ALTER TABLE handbacks ADD COLUMN outcome_authenticated INTEGER NOT NULL DEFAULT 1;

-- 1. The kind of manager that spoke, from the name the copy kept.
UPDATE handbacks
SET outcome_source_key = lower(outcome_by)
WHERE outcome IS NOT NULL AND outcome_by IN ('Deluno', 'Sonarr', 'Radarr');

-- 2. The connection, from the hand-off that recorded the same word at the same moment: one that named the file itself, or a
--    folder hand-off that covered it (the hand-off row holds the folder's path; its targets hold the files). A word no hand-off
--    can be matched to keeps no connection, and Weir takes a word with none for the same kind of manager's.
UPDATE handbacks
SET outcome_connection_id = (
        SELECT h.connection_id FROM media_manager_handoffs h
        WHERE h.library_id = handbacks.library_id
          AND (h.relative_path = handbacks.relative_path
               OR EXISTS (SELECT 1 FROM media_manager_handoff_targets t
                          WHERE t.handoff_row_id = h.id AND t.relative_path = handbacks.relative_path))
          AND h.source_key = handbacks.outcome_source_key
          AND h.outcome = handbacks.outcome
          AND h.outcome_at = handbacks.outcome_at
          AND h.connection_id IS NOT NULL
        ORDER BY h.id DESC LIMIT 1)
WHERE outcome_source_key IS NOT NULL;

-- 3. An import that was only recorded because its message carried no webhook secret.
UPDATE handbacks
SET outcome_authenticated = 0
WHERE outcome = 'imported' AND release_note LIKE '%carry no webhook secret%';

-- 4. A hand-off received while another hand-off's pass was already working on the same file is answered from the file's own
--    state and covers no file of its own. It rides on the hand-off that owns the pass, so an answer for it can be tied to the
--    copy that pass wrote and to no later one.
CREATE TABLE media_manager_handoff_riders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    handoff_row_id INTEGER NOT NULL,
    -- The file's path in the watched folder, the same key as files.relative_path.
    relative_path TEXT NOT NULL,
    -- The hand-off whose pass is working on the file.
    owner_row_id INTEGER NOT NULL,
    CONSTRAINT fk_media_manager_handoff_riders_handoff_row_id FOREIGN KEY (handoff_row_id) REFERENCES media_manager_handoffs (id) ON DELETE CASCADE,
    CONSTRAINT fk_media_manager_handoff_riders_owner_row_id FOREIGN KEY (owner_row_id) REFERENCES media_manager_handoffs (id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX uq_media_manager_handoff_riders_handoff_row_id_relative_path ON media_manager_handoff_riders (handoff_row_id, relative_path);
