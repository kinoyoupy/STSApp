-- audio_files.duration_ms は初期版で使用しないため削除します。
-- 音声処理にかかった時間は turn_events.duration_ms で管理します。
--
-- 実行前に、対象DBが開発用DBであることを確認してください。
ALTER TABLE audio_files
    DROP COLUMN duration_ms;
